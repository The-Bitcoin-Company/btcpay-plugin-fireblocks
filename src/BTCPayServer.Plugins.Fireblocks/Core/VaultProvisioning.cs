using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Core;

public sealed class CoreApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class VaultBinding
{
    public string OperationId { get; set; } = Guid.NewGuid().ToString();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Environment { get; set; } = "";
    public string WorkspaceFingerprint { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string? VaultAccountId { get; set; }
    public string? ActivationTransactionId { get; set; }
    public string? Address { get; set; }
    public string State { get; set; } = "Pending";
    public string Diagnostic { get; set; } = "";
    public bool ProductionCompatible => Environment == CoreApiSettings.Us && AssetId == "USDC_SOL";
}

public interface IVaultProvisioningStore
{
    Task<VaultBinding?> Get(string storeId);
    Task Save(string storeId, VaultBinding binding);
    Task<IAsyncDisposable> Lock(string storeId);
}

// Independent store setting survives Dynamic checkout deletion. A database advisory
// transaction lock serializes requests across processes; progress saves commit separately
// before provider side effects, so a process crash cannot erase the retry identity.
public sealed class VaultProvisioningStore(StoreRepository stores, IDbContextFactory<FireblocksDbContext> factory) : IVaultProvisioningStore
{
    public Task<VaultBinding?> Get(string storeId) => stores.GetSettingAsync<VaultBinding>(storeId, "Fireblocks.CollectionVault");
    public Task Save(string storeId, VaultBinding binding) => stores.UpdateSetting(storeId, "Fireblocks.CollectionVault", binding);
    public async Task<IAsyncDisposable> Lock(string storeId)
    {
        var db = await factory.CreateDbContextAsync();
        try
        {
            await db.Database.BeginTransactionAsync();
            var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("Fireblocks.Vault:" + storeId)), 0);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})");
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
}

public sealed class VaultProvisioningService(CoreApiClient client, ICoreApiSettingsStore credentials, IVaultProvisioningStore store)
{
    public Task<VaultBinding?> Get(string storeId) => store.Get(storeId);
    public static string Fingerprint(CoreApiSettings settings) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Environment + ":" + settings.ApiKey)));

    // Deliberately narrow allowlist, intersected with the workspace's supported assets.
    public async Task<IReadOnlyList<string>> Assets(string storeId)
    {
        var settings = await credentials.Get(storeId);
        var assets = await client.Send(settings, HttpMethod.Get, "/supported_assets");
        return assets.EnumerateArray().Select(a => Text(a, "id"))
            .Where(id => settings.Environment == CoreApiSettings.Sandbox
                ? id is "SOL_TEST" or "USDC_SOL_TEST"
                : id is "USDC_SOL" or "SOL_TEST" or "USDC_SOL_TEST")
            .Select(id => id!).ToArray();
    }

    public async Task<VaultBinding> Provision(string storeId, string? assetId)
    {
        await using var lease = await store.Lock(storeId);
        var settings = await credentials.Get(storeId);
        var binding = await store.Get(storeId);
        if (binding is null)
        {
            if (assetId is null || !(await Assets(storeId)).Contains(assetId))
                throw new CoreApiException("Choose a supported Solana asset from the saved workspace.");
            binding = new() { AssetId = assetId, Environment = settings.Environment, WorkspaceFingerprint = Fingerprint(settings) };
            await store.Save(storeId, binding);
        }
        try
        {
            if (binding.WorkspaceFingerprint != Fingerprint(settings))
                throw new CoreApiException("Restore the original workspace credentials to refresh this vault. No new vault was created.");
            if (binding.State == "Success") return binding;
            if (binding.VaultAccountId is null)
            {
                // Provider idempotency keys expire after 24h. Fail closed well before
                // expiry rather than risk creating another account after an ambiguous call.
                if (DateTimeOffset.UtcNow - binding.CreatedAt > TimeSpan.FromHours(23))
                    throw new CoreApiException("Creation could not be confirmed within the retry window. Reconcile the BTCPay collection vault in the Fireblocks console; do not create another vault.");
                var vault = await client.Send(settings, HttpMethod.Post, "/vault/accounts",
                    new { name = "BTCPay collection " + binding.OperationId, autoFuel = false }, binding.OperationId);
                binding.VaultAccountId = Text(vault, "id") ?? throw new CoreApiException("Fireblocks returned no vault ID. Refresh to retry safely.");
                await store.Save(storeId, binding);
            }
            var path = "/vault/accounts/" + Uri.EscapeDataString(binding.VaultAccountId) + "/" + Uri.EscapeDataString(binding.AssetId);
            // Wallet identity is the vault/asset pair. Read first on every refresh,
            // including after an ambiguous POST, instead of creating another vault.
            try { await client.Send(settings, HttpMethod.Get, path); }
            catch (CoreApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                var wallet = await client.Send(settings, HttpMethod.Post, path, new { }, binding.OperationId + "-wallet");
                binding.ActivationTransactionId = Text(wallet, "activationTxId");
                await store.Save(storeId, binding);
            }
            if (!string.IsNullOrEmpty(binding.ActivationTransactionId))
            {
                var activation = await client.Send(settings, HttpMethod.Get, "/transactions/" + Uri.EscapeDataString(binding.ActivationTransactionId));
                var status = Text(activation, "status");
                if (status is "FAILED" or "REJECTED" or "CANCELLED" or "BLOCKED")
                    throw new CoreApiException("Asset activation failed. Resolve activation in the Fireblocks console; then refresh. No new vault will be created.");
                if (status != "COMPLETED")
                {
                    binding.State = "Pending";
                    binding.Diagnostic = "Asset activation is pending. Approve or resolve activation in the Fireblocks console; then refresh.";
                    await store.Save(storeId, binding);
                    return binding;
                }
            }
            var addresses = await client.Send(settings, HttpMethod.Get, path + "/addresses_paginated?limit=1");
            var address = addresses.GetProperty("addresses").EnumerateArray().Select(a => Text(a, "address")).FirstOrDefault();
            if (!string.IsNullOrEmpty(address) && !FireblocksStoreConfig.IsValidSolanaAddress(address))
                throw new CoreApiException("Fireblocks returned an invalid Solana address. Check the asset wallet in the console; then refresh.");
            binding.Address = address;
            binding.State = string.IsNullOrEmpty(address) ? "Pending" : "Success";
            binding.Diagnostic = binding.State == "Pending"
                ? "Waiting for a deposit address. Check wallet activation and required SOL in the Fireblocks console, then refresh. No automatic fueling is performed."
                : binding.ProductionCompatible ? "Vault ready. Use the address explicitly to update the mainnet destination." : "Non-production vault ready. This test address cannot be used for Dynamic mainnet checkout.";
        }
        catch (Exception e) when (e is CoreApiException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            binding.State = "Error";
            binding.Diagnostic = e is CoreApiException ? e.Message : "Unexpected Fireblocks response. Check the asset wallet in the console; then refresh.";
        }
        await store.Save(storeId, binding);
        return binding;
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
