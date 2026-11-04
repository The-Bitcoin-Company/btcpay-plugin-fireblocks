using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Fireblocks.Controllers;
using BTCPayServer.Plugins.Fireblocks.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
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
        saved.AccountAttemptAt = DateTimeOffset.UtcNow.AddDays(-2);
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
        Assert.Equal("Error", (await harness.Service.Provision("store", null)).State);
        harness.ReactivationStatus = "COMPLETED";
        Assert.Equal("Success", (await harness.Service.Provision("store", null, retryActivation: true)).State);
        Assert.Equal("activation-2", (await harness.Store.Get("store"))!.ActivationTransactionId);
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

    [Fact]
    public async Task LostWalletResponseReplaysKeyAndCannotBypassPendingActivation()
    {
        var harness = new Harness("US") { LoseWalletResponse = true, ActivationStatus = "SUBMITTED" };
        Assert.Equal("Error", (await harness.Service.Provision("store", "USDC_SOL")).State);
        harness.LoseWalletResponse = false;
        var result = await harness.Service.Provision("store", null);
        Assert.Equal("Pending", result.State);
        Assert.Null(result.Address);
        Assert.Equal(harness.WalletKeys[0], harness.WalletKeys[1]);
        Assert.Equal(1, harness.WalletCreates);
    }

    [Fact]
    public async Task ActivationRequiredWithoutTransactionCannotBypassExplicitActivation()
    {
        var harness = new Harness("US") { WalletRequiresActivation = true, ReactivationStatus = "COMPLETED" };
        Assert.Equal("Pending", (await harness.Service.Provision("store", "USDC_SOL")).State);
        Assert.Equal("Pending", (await harness.Service.Provision("store", null)).State);
        Assert.Equal("Success", (await harness.Service.Provision("store", null, retryActivation: true)).State);
    }

    [Fact]
    public async Task DefiniteReactivationRejectionNeedsExplicitRetryAndRemainsRecoverable()
    {
        var harness = new Harness("US") { ActivationStatus = "FAILED", DenyActivation = true };
        await harness.Service.Provision("store", "USDC_SOL");
        Assert.Equal("Error", (await harness.Service.Provision("store", null, retryActivation: true)).State);
        var saved = (await harness.Store.Get("store"))!;
        Assert.Null(saved.ActivationRetryKey);
        Assert.Null(saved.ActivationRetryAt);
        saved.CreatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        await harness.Store.Save("store", saved);
        harness.DenyActivation = false;
        harness.ReactivationStatus = "COMPLETED";
        Assert.Equal("Error", (await harness.Service.Provision("store", null)).State);
        Assert.Single(harness.ActivationKeys);
        Assert.Equal("Success", (await harness.Service.Provision("store", null, retryActivation: true)).State);
        Assert.Equal(2, harness.ActivationKeys.Count);
        Assert.NotEqual(harness.ActivationKeys[0], harness.ActivationKeys[1]);
    }

    [Fact]
    public async Task AmbiguousReactivationReplaysSameKeyOnRefresh()
    {
        var harness = new Harness("US") { ActivationStatus = "FAILED", LoseActivationResponse = true };
        await harness.Service.Provision("store", "USDC_SOL");
        Assert.Equal("Error", (await harness.Service.Provision("store", null, retryActivation: true)).State);
        harness.LoseActivationResponse = false;
        harness.ReactivationStatus = "COMPLETED";
        Assert.Equal("Success", (await harness.Service.Provision("store", null)).State);
        Assert.Equal(harness.ActivationKeys[0], harness.ActivationKeys[1]);
    }

    [Fact]
    public async Task AlreadyReadyActivationResponseWithoutTransactionCanSucceed()
    {
        var harness = new Harness("US") { ActivationStatus = "FAILED", ActivationAlreadyReady = true };
        await harness.Service.Provision("store", "USDC_SOL");
        Assert.Equal("Success", (await harness.Service.Provision("store", null, retryActivation: true)).State);
        Assert.Null((await harness.Store.Get("store"))!.ActivationTransactionId);
    }

    [Fact]
    public async Task AssetSelectionAndProvisioningReadCredentialsOnlyOnce()
    {
        var harness = new Harness("US");
        harness.Credentials.SwitchOnSecondRead = true;
        Assert.Equal("Success", (await harness.Service.Provision("store", "USDC_SOL")).State);
        Assert.Equal(1, harness.Credentials.Reads);
    }

    [Fact]
    public async Task AgedDefiniteRejectionCanRetryAfterPermissionsAreFixed()
    {
        var harness = new Harness("US") { DenyVault = true };
        await harness.Service.Provision("store", "USDC_SOL");
        var binding = (await harness.Store.Get("store"))!;
        Assert.False(binding.AccountAttemptAmbiguous);
        binding.AccountAttemptAt = DateTimeOffset.UtcNow.AddDays(-2);
        await harness.Store.Save("store", binding);
        harness.DenyVault = false;
        Assert.Equal("Success", (await harness.Service.Provision("store", null)).State);
        Assert.NotEqual(harness.Keys[0], harness.Keys[1]);
    }

    [Fact]
    public async Task TimeoutActivationIsTerminalAndNeedsExplicitRetry()
    {
        var harness = new Harness("US") { ActivationStatus = "TIMEOUT" };
        var result = await harness.Service.Provision("store", "USDC_SOL");
        Assert.Equal("Error", result.State);
        Assert.True(result.ActivationFailed);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UseAddressChangesOnlyDestinationAndPreservesCheckoutPolicy(bool existing, bool enabled)
    {
        var harness = new Harness("US");
        await harness.Service.Provision("store", "USDC_SOL");
        var setup = Controller(harness, existing, enabled);
        var previous = setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId)?.DeepClone();
        Assert.IsType<RedirectToActionResult>(await setup.Controller.ConfigureVault(null, "use"));
        var config = setup.Handler.ParsePaymentMethodConfig(setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId)!);
        Assert.Equal(Address, config.SolanaDestinationAddress);
        Assert.Equal(1, setup.Writer.Writes);
        Assert.Equal(existing && enabled, setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId, onlyEnabled: true) is not null);
        if (existing)
        {
            var expected = setup.Handler.ParsePaymentMethodConfig(previous!);
            expected.SolanaDestinationAddress = Address;
            Assert.True(JToken.DeepEquals(expected.ToStorageToken(setup.Handler.Serializer), config.ToStorageToken(setup.Handler.Serializer)));
        }
    }

    [Theory]
    [InlineData("test")]
    [InlineData("pending")]
    [InlineData("invalid")]
    [InlineData("workspace")]
    public async Task UnsafeBindingsNeverReplaceDestination(string variant)
    {
        var harness = new Harness("US");
        var binding = await harness.Service.Provision("store", "USDC_SOL");
        if (variant == "test") binding.Environment = "Sandbox";
        if (variant == "pending") binding.State = "Pending";
        if (variant == "invalid") binding.Address = "invalid";
        if (variant == "workspace") binding.WorkspaceFingerprint = "other";
        await harness.Store.Save("store", binding);
        var setup = Controller(harness, true, true);
        var before = setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId)!.DeepClone();
        await setup.Controller.ConfigureVault(null, "use");
        Assert.Equal(0, setup.Writer.Writes);
        Assert.True(JToken.DeepEquals(before, setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId)));
    }

    [Fact]
    public async Task ValidationFailuresRetainVaultControlsAndRedactCredentials()
    {
        var harness = new Harness("US");
        await harness.Service.Provision("store", "USDC_SOL");
        var setup = Controller(harness, true, false);
        var flow = Assert.IsType<ViewResult>(await setup.Controller.Configure(new FireblocksStoreConfigViewModel()));
        Assert.NotNull(Assert.IsType<FireblocksStoreConfigViewModel>(flow.Model).Vault);
        var core = Assert.IsType<ViewResult>(await setup.Controller.ConfigureCore(new() { CoreApi = new() { PrivateKey = "bad" } }, "save"));
        var model = Assert.IsType<FireblocksStoreConfigViewModel>(core.Model);
        Assert.NotNull(model.Vault);
        Assert.Empty(model.CoreApi.PrivateKey);
        Assert.Empty(model.CoreApi.ApiKey);
        Assert.Equal(0, setup.Writer.Writes);
    }

    [Fact]
    public async Task WhitespacePaddedTestDestinationCannotEnableMainnetCheckout()
    {
        var harness = new Harness("Sandbox");
        await harness.Service.Provision("store", "SOL_TEST");
        var setup = Controller(harness, true, false);
        var before = setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId)!.DeepClone();
        var config = TestData.Config();
        var posted = new FireblocksStoreConfigViewModel
        {
            Enabled = true, EnvironmentId = config.EnvironmentId, ApiEndpoint = config.ApiEndpoint,
            ApiCredential = config.ApiCredential, WebhookSecret = config.WebhookSecret,
            AcceptedRiskStates = config.AcceptedRiskStates, HostedPaymentBaseUrl = config.HostedPaymentBaseUrl,
            SolanaDestinationAddress = " " + Address + " ", PaymentExpiryMinutes = config.PaymentExpiryMinutes
        };
        Assert.IsType<ViewResult>(await setup.Controller.Configure(posted));
        Assert.Contains(nameof(posted.SolanaDestinationAddress), setup.Controller.ModelState.Keys);
        Assert.Equal(0, setup.Writer.Writes);
        Assert.True(JToken.DeepEquals(before, setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId)));
        Assert.Null(setup.Store.GetPaymentMethodConfig(setup.Handler.PaymentMethodId, onlyEnabled: true));
    }

    private static (UIFireblocksController Controller, StoreData Store, FireblocksPaymentMethodHandler Handler, Writer Writer) Controller(Harness harness, bool existing, bool enabled)
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var store = new StoreData { Id = "store" };
        if (existing) store.SetPaymentMethodConfig(handler.PaymentMethodId, TestData.Config().ToStorageToken(handler.Serializer));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(handler.PaymentMethodId, !enabled);
        store.SetStoreBlob(blob);
        var context = new DefaultHttpContext();
        context.SetStoreData(store);
        var writer = new Writer();
        var controller = new UIFireblocksController(writer, handler, new ConfigurationBuilder().Build(), new CoreApiClient(new HttpClient(harness)), harness.Credentials, harness.Service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new TempProvider())
        };
        return (controller, store, handler, writer);
    }
    private sealed class Writer : IStoreConfigurationWriter
    {
        public int Writes;
        public Task Update(StoreData store) { Writes++; return Task.CompletedTask; }
    }
    private sealed class TempProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class Harness : HttpMessageHandler
    {
        public readonly MemoryStore Store = new();
        public readonly CredentialsStore Credentials;
        public readonly VaultProvisioningService Service;
        public bool Pending, FailWallet, LoseVaultResponse, SignatureChecked;
        public string? ActivationStatus;
        public string? ReactivationStatus;
        public bool LoseWalletResponse, DenyVault, WalletRequiresActivation;
        public bool DenyActivation, LoseActivationResponse, ActivationAlreadyReady;
        public readonly List<string> ActivationKeys = [];
        private string? _walletResponse;
        public readonly List<string> WalletKeys = [];
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
                    if (DenyVault) return new(HttpStatusCode.Forbidden);
                    if (LoseVaultResponse) throw new HttpRequestException("SECRET");
                    return Json("{\"id\":\"42\"}");
                }
                if (path.EndsWith("/activate"))
                {
                    ActivationKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                    if (DenyActivation) return new(HttpStatusCode.Forbidden);
                    if (LoseActivationResponse) throw new HttpRequestException("SECRET");
                    return Json(ActivationAlreadyReady ? "{\"status\":\"READY\",\"address\":\"" + Address + "\"}" : "{\"activationTxId\":\"activation-2\"}");
                }
                WalletKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                if (FailWallet) return new(HttpStatusCode.Forbidden) { Content = new StringContent("SECRET") };
                if (!_walletExists) WalletCreates++;
                _walletExists = true;
                _walletResponse ??= WalletRequiresActivation ? "{\"id\":\"USDC_SOL\",\"status\":\"PENDING_ACTIVATION\"}" : ActivationStatus is null ? "{\"id\":\"USDC_SOL\"}" : "{\"id\":\"USDC_SOL\",\"activationTxId\":\"activation-1\"}";
                if (LoseWalletResponse) throw new HttpRequestException("SECRET");
                return Json(_walletResponse);
            }
            if (path.StartsWith("/v1/transactions/")) return Json("{\"status\":\"" + (path.EndsWith("activation-2") ? ReactivationStatus : ActivationStatus) + "\"}");
            if (path.EndsWith("addresses_paginated")) return Json(Pending ? "{\"addresses\":[]}" : "{\"addresses\":[{\"address\":\"" + Address + "\"}]}");
            return _walletExists ? Json("{\"id\":\"USDC_SOL\"}") : new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class CredentialsStore(CoreApiSettings settings) : ICoreApiSettingsStore
    {
        public CoreApiSettings Settings = settings;
        public bool SwitchOnSecondRead;
        public int Reads;
        public Task<CoreApiSettings> Get(string storeId)
        {
            Reads++;
            if (SwitchOnSecondRead && Reads > 1) return Task.FromResult(new CoreApiSettings { Environment = "Sandbox", ApiKey = "other", PrivateKey = Settings.PrivateKey });
            return Task.FromResult(Settings);
        }
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
