using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Core;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class VaultProvisioningTests
{
    private const string Address = "11111111111111111111111111111111";

    [Theory]
    [InlineData("US", "USDC_SOL", true)]
    [InlineData("Sandbox", "SOL_TEST", false)]
    public async Task CreatesOneVaultAndWalletAndPersistsBinding(string environment, string asset, bool production)
    {
        var harness = new Harness(environment);
        var service = harness.Service;
        var binding = await service.Provision("store", asset);
        Assert.Equal("Success", binding.State);
        Assert.Equal(Address, binding.Address);
        Assert.Equal("42", binding.VaultAccountId);
        Assert.Equal(environment, binding.Environment);
        Assert.Equal(asset, binding.AssetId);
        Assert.Equal(production, binding.ProductionCompatible);
        await service.Provision("store", asset);
        Assert.Equal(1, harness.VaultCreates);
        Assert.Equal(1, harness.WalletCreates);
        Assert.NotNull((await harness.Store.Get("store"))!.Address);
    }

    [Fact]
    public async Task PendingAddressRefreshDoesNotCreateAgain()
    {
        var harness = new Harness("US") { Pending = true };
        Assert.Equal("Pending", (await harness.Service.Provision("store", "USDC_SOL")).State);
        harness.Pending = false;
        Assert.Equal("Success", (await harness.Service.Provision("store", null)).State);
        Assert.Equal(1, harness.VaultCreates);
        Assert.Equal(1, harness.WalletCreates);
    }

    [Fact]
    public async Task ProviderFailureIsRedactedAndRecoverable()
    {
        var harness = new Harness("US") { FailWallet = true };
        var binding = await harness.Service.Provision("store", "USDC_SOL");
        Assert.Equal("Error", binding.State);
        Assert.DoesNotContain("SECRET", binding.Diagnostic);
        Assert.Equal("42", binding.VaultAccountId);
        Assert.Null(binding.Address);
        harness.FailWallet = false;
        Assert.Equal("Success", (await harness.Service.Provision("store", null)).State);
        Assert.Equal(1, harness.VaultCreates);
    }

    [Fact]
    public async Task AmbiguousVaultCreationReusesPersistedIdempotencyKeyAndBody()
    {
        var harness = new Harness("US") { LoseVaultResponse = true };
        Assert.Equal("Error", (await harness.Service.Provision("store", "USDC_SOL")).State);
        harness.LoseVaultResponse = false;
        Assert.Equal("Success", (await harness.Service.Provision("store", "SOL_TEST")).State);
        Assert.Equal(2, harness.Keys.Count);
        Assert.Equal(harness.Keys[0], harness.Keys[1]);
        Assert.Equal(harness.Bodies[0], harness.Bodies[1]);
    }

    [Fact]
    public async Task ExpiredAmbiguousAttemptFailsClosed()
    {
        var harness = new Harness("US") { LoseVaultResponse = true };
        await harness.Service.Provision("store", "USDC_SOL");
        var saved = (await harness.Store.Get("store"))!;
        saved.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        await harness.Store.Save("store", saved);
        harness.LoseVaultResponse = false;
        var result = await harness.Service.Provision("store", null);
        Assert.Equal("Error", result.State);
        Assert.Contains("Reconcile", result.Diagnostic);
        Assert.Equal(1, harness.VaultCreates);
    }

    [Fact]
    public async Task ConcurrentSubmissionsSerializeBeforeAllocatingIdentity()
    {
        var harness = new Harness("US");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => harness.Service.Provision("store", "USDC_SOL")));
        Assert.Equal(1, harness.VaultCreates);
    }

    [Fact]
    public async Task WorkspaceChangesNeverRedirectPendingProvisioning()
    {
        var harness = new Harness("US") { Pending = true };
        await harness.Service.Provision("store", "USDC_SOL");
        harness.Credentials.Settings.ApiKey = "another-workspace";
        var result = await harness.Service.Provision("store", null);
        Assert.Equal("Error", result.State);
        Assert.Contains("original workspace", result.Diagnostic);
        Assert.Equal(1, harness.VaultCreates);
    }

    [Fact]
    public async Task SandboxNeverOffersMainnetAssetsOrAcceptsForgedSelection()
    {
        var harness = new Harness("Sandbox");
        Assert.DoesNotContain("USDC_SOL", await harness.Service.Assets("store"));
        await Assert.ThrowsAsync<CoreApiException>(() => harness.Service.Provision("store", "USDC_SOL"));
        Assert.Null(await harness.Store.Get("store"));
        Assert.Equal(0, harness.VaultCreates);
    }

    [Fact]
    public async Task PendingActivationDoesNotApplyAddressAndFailureCanBeRefreshed()
    {
        var harness = new Harness("US") { ActivationStatus = "SUBMITTED" };
        var pending = await harness.Service.Provision("store", "USDC_SOL");
        Assert.Equal("Pending", pending.State);
        Assert.Null(pending.Address);
        harness.ActivationStatus = "FAILED";
        Assert.Equal("Error", (await harness.Service.Provision("store", null)).State);
        harness.ActivationStatus = "COMPLETED";
        Assert.Equal("Success", (await harness.Service.Provision("store", null)).State);
        Assert.Equal(1, harness.VaultCreates);
        Assert.Equal(1, harness.WalletCreates);
    }

    [Fact]
    public async Task PostSignatureHashesExactBody()
    {
        var harness = new Harness("US");
        await harness.Service.Provision("store", "USDC_SOL");
        Assert.True(harness.SignatureChecked);
    }

    private sealed class Harness : HttpMessageHandler
    {
        public readonly MemoryStore Store = new();
        public readonly CredentialsStore Credentials;
        public readonly VaultProvisioningService Service;
        public bool Pending, FailWallet, LoseVaultResponse, SignatureChecked;
        public string? ActivationStatus;
        public int VaultCreates, WalletCreates;
        private bool _walletExists;
        public readonly List<string> Keys = [];
        public readonly List<string> Bodies = [];
        public Harness(string environment)
        {
            using var rsa = RSA.Create(2048);
            Credentials = new(new() { Environment = environment, ApiKey = "workspace", PrivateKey = rsa.ExportPkcs8PrivateKeyPem() });
            Service = new(new CoreApiClient(new HttpClient(this)), Credentials, Store);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/supported_assets") return Json("[{\"id\":\"USDC_SOL\"},{\"id\":\"SOL_TEST\"},{\"id\":\"ETH\"}]");
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var payload = request.Headers.Authorization!.Parameter!.Split('.')[1];
                using var jwt = JsonDocument.Parse(Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/').PadRight((payload.Length + 3) / 4 * 4, '=')));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant(), jwt.RootElement.GetProperty("bodyHash").GetString());
                SignatureChecked = true;
                if (path == "/v1/vault/accounts")
                {
                    VaultCreates++;
                    Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                    Bodies.Add(body);
                    if (LoseVaultResponse) throw new HttpRequestException("SECRET");
                    return Json("{\"id\":\"42\"}");
                }
                if (FailWallet) return new(HttpStatusCode.Forbidden) { Content = new StringContent("SECRET") };
                WalletCreates++;
                _walletExists = true;
                return Json(ActivationStatus is null ? "{\"id\":\"USDC_SOL\"}" : "{\"id\":\"USDC_SOL\",\"activationTxId\":\"activation-1\"}");
            }
            if (path.StartsWith("/v1/transactions/")) return Json("{\"status\":\"" + ActivationStatus + "\"}");
            if (path.EndsWith("addresses_paginated")) return Json(Pending ? "{\"addresses\":[]}" : "{\"addresses\":[{\"address\":\"" + Address + "\"}]}");
            return _walletExists ? Json("{\"id\":\"USDC_SOL\"}") : new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class CredentialsStore(CoreApiSettings settings) : ICoreApiSettingsStore
    {
        public CoreApiSettings Settings = settings;
        public Task<CoreApiSettings> Get(string storeId) => Task.FromResult(Settings);
        public Task Save(string storeId, CoreApiSettings value) => throw new NotSupportedException();
    }
    private sealed class MemoryStore : IVaultProvisioningStore
    {
        private readonly SemaphoreSlim _lock = new(1);
        private string? _json;
        public Task<VaultBinding?> Get(string storeId) => Task.FromResult(_json is null ? null : JsonSerializer.Deserialize<VaultBinding>(_json));
        public Task Save(string storeId, VaultBinding binding) { _json = JsonSerializer.Serialize(binding); return Task.CompletedTask; }
        public async Task<IAsyncDisposable> Lock(string storeId) { await _lock.WaitAsync(); return new Lease(_lock); }
        private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { semaphore.Release(); return ValueTask.CompletedTask; }
        }
    }
}
