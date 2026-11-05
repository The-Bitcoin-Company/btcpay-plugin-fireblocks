using System.Globalization;
using System.Net;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Fireblocks.Core;

public sealed class SweepRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string VaultId { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string Environment { get; set; } = "";
    public string WorkspaceFingerprint { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Amount { get; set; } = "";
    public string State { get; set; } = "Confirmation";
    public string? TransactionId { get; set; }
    public string ProviderStatus { get; set; } = "";
    public string Diagnostic { get; set; } = "";
    public bool Unresolved => State == "Pending";
}

public sealed class VaultSweepSettings
{
    public string Destination { get; set; } = "";
    public List<SweepRecord> Records { get; set; } = [];
}

public interface IVaultSweepStore
{
    Task<VaultSweepSettings> Get(string storeId);
    Task Save(string storeId, VaultSweepSettings settings);
    Task<string[]> StoreIds();
}

// Independent of checkout configuration; use the collection-vault database lock for
// every mutation. Saves commit before side effects, independently of the lock lease.
public sealed class VaultSweepStore(StoreRepository stores) : IVaultSweepStore
{
    public async Task<VaultSweepSettings> Get(string storeId) =>
        await stores.GetSettingAsync<VaultSweepSettings>(storeId, "Fireblocks.VaultSweeps") ?? new();
    public Task Save(string storeId, VaultSweepSettings settings) => stores.UpdateSetting(storeId, "Fireblocks.VaultSweeps", settings);
    public async Task<string[]> StoreIds() => (await stores.GetStores()).Select(s => s.Id).ToArray();
}

public sealed class VaultSweepService(CoreApiClient client, ICoreApiSettingsStore credentials,
    IVaultProvisioningStore vaults, IVaultSweepStore store)
{
    public Task<VaultSweepSettings> Get(string storeId) => store.Get(storeId);

    private async Task<(VaultBinding Vault, CoreApiSettings Credentials)> Context(string storeId)
    {
        var settings = await credentials.Get(storeId);
        var vault = await vaults.Get(storeId);
        if (vault is not { State: "Success", VaultAccountId: not null } ||
            vault.WorkspaceFingerprint != VaultProvisioningService.Fingerprint(settings) ||
            vault.AssetId is not ("USDC_SOL" or "USDC_SOL_TEST" or "SOL_TEST"))
            throw new CoreApiException("Restore the ready collection vault's original workspace before sweeping.");
        return (vault, settings);
    }

    public async Task SaveDestination(string storeId, string? destination)
    {
        destination = destination?.Trim();
        if (destination is null || !FireblocksStoreConfig.IsValidSolanaAddress(destination))
            throw new CoreApiException("Enter one valid Solana wallet address for this vault's network.");
        await using var lease = await vaults.Lock(storeId);
        var (vault, _) = await Context(storeId);
        if (destination == vault.Address) throw new CoreApiException("The destination must differ from the collection vault address.");
        var saved = await store.Get(storeId);
        saved.Destination = destination;
        await store.Save(storeId, saved);
    }

    private async Task<decimal> Balance(CoreApiSettings settings, VaultBinding vault)
    {
        var wallet = await client.Send(settings, HttpMethod.Get, "/vault/accounts/" + Uri.EscapeDataString(vault.VaultAccountId!) + "/" + Uri.EscapeDataString(vault.AssetId));
        if (!decimal.TryParse(Text(wallet, "available"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var balance) || balance <= 0)
            throw new CoreApiException("Available balance is unavailable or zero. No transfer was submitted.");
        return balance;
    }

    public static decimal ValidateAmount(string? amount, string assetId, decimal available)
    {
        var precision = assetId == "SOL_TEST" ? 9 : 6;
        if (string.IsNullOrEmpty(amount) || amount.Length > 40 ||
            amount.Any(c => c is not (>= '0' and <= '9') && c != '.') ||
            (amount.Contains('.') && amount.Length - amount.IndexOf('.') - 1 > precision) ||
            !decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 || value > available || decimal.Round(value, precision) != value)
            throw new CoreApiException("Enter a positive amount no greater than the available balance, within the asset's decimal precision.");
        return value;
    }

    public async Task<SweepRecord> Prepare(string storeId)
    {
        await using var lease = await vaults.Lock(storeId);
        var (vault, settings) = await Context(storeId);
        var saved = await store.Get(storeId);
        if (saved.Records.Any(r => r.Unresolved)) throw new CoreApiException("An earlier sweep is unresolved. Refresh its status; do not submit another transfer.");
        if (!FireblocksStoreConfig.IsValidSolanaAddress(saved.Destination)) throw new CoreApiException("Save a sweep destination first.");
        var balance = await Balance(settings, vault);
        var record = new SweepRecord
        {
            VaultId = vault.VaultAccountId!, AssetId = vault.AssetId, Environment = vault.Environment,
            WorkspaceFingerprint = vault.WorkspaceFingerprint, Destination = saved.Destination,
            Amount = balance.ToString(CultureInfo.InvariantCulture)
        };
        // Only the newest confirmation can be used. Old tabs cannot trigger a later sweep.
        foreach (var old in saved.Records.Where(r => r.State == "Confirmation")) old.State = "Expired";
        saved.Records.Add(record);
        await store.Save(storeId, saved);
        return record;
    }

    public async Task<SweepRecord> Confirm(string storeId, string id, string? amount)
    {
        await using var lease = await vaults.Lock(storeId);
        var saved = await store.Get(storeId);
        var record = saved.Records.SingleOrDefault(r => r.Id == id) ?? throw new CoreApiException("Sweep confirmation not found. Start again from settings.");
        // Repeated forms, including after completion, never allocate another identity.
        if (record.State != "Confirmation") return record;
        if (saved.Records.Any(r => r.Unresolved)) throw new CoreApiException("An earlier sweep is unresolved. Refresh its status first.");
        var (vault, settings) = await Context(storeId);
        if (record.WorkspaceFingerprint != vault.WorkspaceFingerprint || record.VaultId != vault.VaultAccountId ||
            record.AssetId != vault.AssetId || record.Destination != saved.Destination ||
            DateTimeOffset.UtcNow - record.CreatedAt > TimeSpan.FromMinutes(15))
            throw new CoreApiException("Sweep settings changed or confirmation expired. Start again from settings.");
        var available = await Balance(settings, vault);
        record.Amount = ValidateAmount(amount, record.AssetId, available).ToString(CultureInfo.InvariantCulture);
        record.State = "Pending";
        record.Diagnostic = "Submission is unresolved. Refresh to reconcile; no automatic resubmission is performed.";
        // Never POST this record again, even if the process dies before the request.
        // The permanent externalTxId, not the expiring HTTP key, is the recovery identity.
        await store.Save(storeId, saved);
        try
        {
            var result = await client.Send(settings, HttpMethod.Post, "/transactions", new
            {
                operation = "TRANSFER", externalTxId = record.Id, assetId = record.AssetId,
                source = new { type = "VAULT_ACCOUNT", id = record.VaultId },
                destination = new { type = "ONE_TIME_ADDRESS", oneTimeAddress = new { address = record.Destination } },
                amount = record.Amount
            }, record.Id);
            record.TransactionId = Text(result, "id");
            // Submission is not completion, even if a create response claims completion.
            record.Diagnostic = "Submitted; awaiting provider transaction verification. Approvals must be handled in Fireblocks.";
        }
        catch (CoreApiException e)
        {
            if (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
                record.State = "Failed";
            record.Diagnostic = record.State == "Failed"
                ? "Submission rejected. Check native SOL gas, available balance, policy and permissions in Fireblocks. No automatic remediation is performed."
                : "Submission is uncertain. Refresh to reconcile by external ID. Do not create another transfer in the console.";
        }
        await store.Save(storeId, saved);
        return record;
    }

    public async Task Refresh(string storeId)
    {
        await using var lease = await vaults.Lock(storeId);
        var saved = await store.Get(storeId);
        foreach (var record in saved.Records.Where(r => r.Unresolved))
        {
            try
            {
                var settings = await credentials.Get(storeId);
                if (record.WorkspaceFingerprint != VaultProvisioningService.Fingerprint(settings))
                    throw new CoreApiException("Restore the original workspace credentials to reconcile this sweep.");
                var path = record.TransactionId is null
                    ? "/transactions/external_tx_id/" + Uri.EscapeDataString(record.Id)
                    : "/transactions/" + Uri.EscapeDataString(record.TransactionId);
                var result = await client.Send(settings, HttpMethod.Get, path);
                var providerId = Text(result, "id");
                if (string.IsNullOrEmpty(providerId) || Text(result, "externalTxId") != record.Id ||
                    Text(result, "assetId") != record.AssetId)
                    throw new CoreApiException("Provider transaction identity could not be verified. Reconcile in Fireblocks; this sweep remains blocked.");
                record.TransactionId = providerId;
                var status = Text(result, "status");
                // Never display free-form provider diagnostics or unknown status strings.
                record.ProviderStatus = status is "COMPLETED" or "FAILED" or "REJECTED" or "CANCELLED" or "BLOCKED" or "TIMEOUT" or
                    "SUBMITTED" or "QUEUED" or "PENDING_AUTHORIZATION" or "PENDING_SIGNATURE" or "PENDING_3RD_PARTY_MANUAL_APPROVAL" or
                    "PENDING_3RD_PARTY" or "BROADCASTING" or "CONFIRMING" or "PENDING_AML_SCREENING" or "PARTIALLY_COMPLETED"
                    ? status : "UNKNOWN";
                record.State = status == "COMPLETED" ? "Completed" : status is "FAILED" or "REJECTED" or "CANCELLED" or "BLOCKED" ? "Failed" : "Pending";
                record.Diagnostic = record.State switch
                {
                    "Completed" => "Provider confirmed completion.",
                    "Failed" => "Provider reports failure. Check native SOL gas, available balance, policy and rejection details in Fireblocks. No automatic remediation is performed.",
                    _ => "Awaiting provider completion. Handle policy, approvals or signing in Fireblocks. No automatic approval, fueling or resubmission is performed."
                };
            }
            catch (CoreApiException)
            {
                record.Diagnostic = "Status could not be verified. Restore the original workspace and check connectivity or reconcile external ID in Fireblocks. This sweep remains unresolved; no resubmission is performed.";
            }
        }
        await store.Save(storeId, saved);
    }

    public async Task ReconcileOutstanding(CancellationToken cancellationToken)
    {
        foreach (var id in await store.StoreIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(await store.Get(id)).Records.Any(r => r.Unresolved)) continue;
            await Refresh(id);
        }
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
