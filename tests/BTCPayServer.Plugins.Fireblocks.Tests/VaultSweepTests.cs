using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Controllers;
using BTCPayServer.Plugins.Fireblocks.Core;
using BTCPayServer.Plugins.Fireblocks.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class VaultSweepTests
{
    private const string Destination = "11111111111111111111111111111111";

    [Fact]
    public async Task ConfirmationUsesSavedSnapshotAndDoesNotMoveFunds()
    {
        var h = new Harness();
        await h.Service.SaveDestination("store", " " + Destination + " ");
        var controller = h.Controller();
        var view = Assert.IsType<ViewResult>(await controller.Sweep("prepare", "unsaved", null, "999"));
        Assert.Equal("Sweep", view.ViewName);
        var draft = Assert.IsType<SweepRecord>(view.Model);
        Assert.Equal("42", draft.VaultId);
        Assert.Equal("SOL_TEST", draft.AssetId);
        Assert.Equal("Sandbox", draft.Environment);
        Assert.Equal(Destination, draft.Destination);
        Assert.Equal("10.123456789", draft.Amount);
        Assert.Equal(0, h.Posts);
        Assert.IsType<RedirectToActionResult>(await controller.Sweep("confirm", "forged", draft.Id, "2.5"));
        Assert.Equal(1, h.Posts);
        var saved = (await h.Service.Get("store")).Records.Single();
        Assert.Equal("Pending", saved.State);
        Assert.Equal("tx", saved.TransactionId);
        using var body = JsonDocument.Parse(h.Body!);
        Assert.Equal("TRANSFER", body.RootElement.GetProperty("operation").GetString());
        Assert.Equal("SOL_TEST", body.RootElement.GetProperty("assetId").GetString());
        Assert.Equal("42", body.RootElement.GetProperty("source").GetProperty("id").GetString());
        Assert.Equal(Destination, body.RootElement.GetProperty("destination").GetProperty("oneTimeAddress").GetProperty("address").GetString());
        Assert.Equal("2.5", body.RootElement.GetProperty("amount").GetString());
        Assert.Equal(draft.Id, body.RootElement.GetProperty("externalTxId").GetString());
        Assert.Equal(draft.Id, h.Key);
        Assert.Equal("Pending", h.StateAtSubmission);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("NaN")]
    [InlineData("1e0")]
    [InlineData("1,2")]
    [InlineData("0.0000000001")]
    [InlineData("1.000000000000000000000000000000001")]
    [InlineData("")]
    public async Task InvalidAmountsNeverSubmit(string amount)
    {
        var h = new Harness();
        var draft = await h.Draft();
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Confirm("store", draft.Id, amount));
        Assert.Equal(0, h.Posts);
        Assert.Equal("Confirmation", (await h.Service.Get("store")).Records.Single().State);
    }

    [Fact]
    public void UsdcPrecisionIsSixDecimals()
    {
        Assert.Equal(0.000001m, VaultSweepService.ValidateAmount("0.000001", "USDC_SOL_TEST", 1));
        Assert.Throws<CoreApiException>(() => VaultSweepService.ValidateAmount("0.0000001", "USDC_SOL", 1));
    }

    [Fact]
    public async Task ConcurrentRepeatedFormsAndLaterReplayNeverResubmit()
    {
        var h = new Harness();
        var draft = await h.Draft();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.Service.Confirm("store", draft.Id, "1")));
        Assert.Equal(1, h.Posts);
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
        h.Status = "COMPLETED";
        await h.Service.Refresh("store");
        await h.Service.Confirm("store", draft.Id, "1");
        Assert.Equal(1, h.Posts);
        Assert.Equal("Completed", (await h.Service.Get("store")).Records.Single().State);
    }

    [Fact]
    public async Task LostResponseRecoversAfterRestartWithoutWebhookOrAnotherPost()
    {
        var h = new Harness { LoseResponse = true };
        var draft = await h.Draft();
        Assert.Equal("Pending", (await h.Service.Confirm("store", draft.Id, "1")).State);
        h.Service = h.NewService();
        h.Status = "PENDING_AUTHORIZATION";
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        Assert.Equal("/v1/transactions/external_tx_id/" + draft.Id, h.LastLookup);
        var recovered = (await h.Service.Get("store")).Records.Single();
        Assert.Equal("tx", recovered.TransactionId);
        Assert.Equal("PENDING_AUTHORIZATION", recovered.ProviderStatus);
        await h.Service.Confirm("store", draft.Id, "1");
        Assert.Equal(1, h.Posts);
        h.Status = "COMPLETED";
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        Assert.Equal("/v1/transactions/tx", h.LastLookup);
        Assert.Equal("Completed", (await h.Service.Get("store")).Records.Single().State);
    }

    [Fact]
    public async Task CrashBeforePostOrProvider404RemainsBlockedIndefinitely()
    {
        var h = new Harness { NotFound = true };
        var draft = await h.Draft();
        var saved = await h.Store.Get("store");
        saved.Records.Single().State = "Pending";
        saved.Records.Single().CreatedAt = DateTimeOffset.UtcNow.AddDays(-7);
        await h.Store.Save("store", saved);
        h.Service = h.NewService();
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        await h.Service.Confirm("store", draft.Id, "1");
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
        Assert.Equal(0, h.Posts);
        Assert.Equal("Pending", (await h.Store.Get("store")).Records.Single().State);
    }

    [Theory]
    [InlineData("SUBMITTED", "Pending")]
    [InlineData("PENDING_SIGNATURE", "Pending")]
    [InlineData("PENDING_AUTHORIZATION", "Pending")]
    [InlineData("CONFIRMING", "Pending")]
    [InlineData("TIMEOUT", "Pending")]
    [InlineData("PARTIALLY_COMPLETED", "Pending")]
    [InlineData("SECRET", "Pending")]
    [InlineData("COMPLETED", "Completed")]
    [InlineData("FAILED", "Failed")]
    [InlineData("REJECTED", "Failed")]
    [InlineData("CANCELLED", "Failed")]
    [InlineData("BLOCKED", "Failed")]
    public async Task OnlyVerifiedTerminalStatusesReleaseVault(string providerStatus, string state)
    {
        var h = new Harness();
        var draft = await h.Draft();
        await h.Service.Confirm("store", draft.Id, "1");
        h.Status = providerStatus;
        await h.Service.Refresh("store");
        var saved = (await h.Store.Get("store")).Records.Single();
        Assert.Equal(state, saved.State);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(saved));
        if (state == "Pending") await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
        else Assert.Equal("Confirmation", (await h.Service.Prepare("store")).State);
    }

    [Theory]
    [InlineData(400, "Failed")]
    [InlineData(403, "Failed")]
    [InlineData(422, "Failed")]
    [InlineData(409, "Pending")]
    [InlineData(429, "Pending")]
    [InlineData(500, "Pending")]
    public async Task SubmissionFailuresAreRedactedAndDoNotAffectCheckout(int code, string state)
    {
        var h = new Harness { Reject = (HttpStatusCode)code };
        var controller = h.Controller();
        var before = JsonSerializer.Serialize(controller.HttpContext.GetStoreData());
        var draft = await h.Draft();
        await controller.Sweep("confirm", null, draft.Id, "1");
        var record = (await h.Service.Get("store")).Records.Single();
        Assert.Equal(state, record.State);
        Assert.DoesNotContain("SECRET", record.Diagnostic);
        Assert.Equal(before, JsonSerializer.Serialize(controller.HttpContext.GetStoreData()));
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("expired")]
    [InlineData("workspace")]
    [InlineData("balance")]
    [InlineData("zero")]
    [InlineData("old-tab")]
    public async Task ChangedOrStaleConfirmationCannotSubmit(string change)
    {
        var h = new Harness();
        var draft = await h.Draft();
        if (change == "destination") await h.Service.SaveDestination("store", "So11111111111111111111111111111111111111112");
        if (change == "expired")
        {
            var saved = await h.Store.Get("store"); saved.Records.Single().CreatedAt = DateTimeOffset.UtcNow.AddHours(-1); await h.Store.Save("store", saved);
        }
        if (change == "workspace") h.Credentials.Settings.ApiKey = "other";
        if (change == "balance") h.Balance = "0.1";
        if (change == "zero") h.Balance = "0";
        if (change == "old-tab") await h.Service.Prepare("store");
        if (change == "old-tab") Assert.Equal("Expired", (await h.Service.Confirm("store", draft.Id, "1")).State);
        else await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Confirm("store", draft.Id, "1"));
        Assert.Equal(0, h.Posts);
    }

    [Fact]
    public async Task WrongProviderIdentityOrWorkspaceCannotCompleteSweep()
    {
        var h = new Harness();
        var draft = await h.Draft();
        await h.Service.Confirm("store", draft.Id, "1");
        h.Status = "COMPLETED";
        h.WrongIdentity = true;
        await h.Service.Refresh("store");
        Assert.Equal("Pending", (await h.Service.Get("store")).Records.Single().State);
        h.WrongIdentity = false;
        h.Credentials.Settings.ApiKey = "other";
        await h.Service.Refresh("store");
        Assert.Equal("Pending", (await h.Service.Get("store")).Records.Single().State);
    }

    private sealed class Harness : HttpMessageHandler
    {
        public readonly MemoryStore Store = new();
        public readonly Credentials Credentials;
        public VaultSweepService Service;
        public string Balance = "10.123456789", Status = "COMPLETED";
        public int Posts;
        public bool LoseResponse, NotFound, WrongIdentity;
        public HttpStatusCode? Reject;
        public string? Body, Key, StateAtSubmission, LastLookup;
        public Harness()
        {
            using var rsa = RSA.Create(2048);
            Credentials = new(new() { Environment = "Sandbox", ApiKey = "key", PrivateKey = rsa.ExportPkcs8PrivateKeyPem() });
            Store.Vault = new() { State = "Success", VaultAccountId = "42", AssetId = "SOL_TEST", Environment = "Sandbox", WorkspaceFingerprint = VaultProvisioningService.Fingerprint(Credentials.Settings) };
            Service = NewService();
        }
        public VaultSweepService NewService() => new(new CoreApiClient(new HttpClient(this)), Credentials, Store, Store);
        public async Task<SweepRecord> Draft() { await Service.SaveDestination("store", Destination); return await Service.Prepare("store"); }
        public UIFireblocksController Controller()
        {
            var context = new DefaultHttpContext();
            context.SetStoreData(new StoreData { Id = "store" });
            return new(null!, new FireblocksPaymentMethodHandler(null!), new ConfigurationBuilder().Build(), new CoreApiClient(new HttpClient(this)), Credentials, sweeps: Service)
            {
                ControllerContext = new() { HttpContext = context }, TempData = new TempDataDictionary(context, new TempProvider())
            };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/transactions", request.RequestUri!.AbsolutePath);
                Posts++;
                StateAtSubmission = (await Store.Get("store")).Records.Last().State;
                Body = await request.Content!.ReadAsStringAsync(token);
                Key = request.Headers.GetValues("Idempotency-Key").Single();
                if (Reject is not null) return new(Reject.Value) { Content = new StringContent("SECRET") };
                if (LoseResponse) throw new HttpRequestException("SECRET");
                return Json(new { id = "tx", status = "COMPLETED" });
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/transactions/"))
            {
                LastLookup = request.RequestUri.AbsolutePath;
                if (NotFound) return new(HttpStatusCode.NotFound) { Content = new StringContent("SECRET") };
                return Json(new { id = "tx", externalTxId = WrongIdentity ? "other" : (await Store.Get("store")).Records.Last(r => r.Unresolved).Id, assetId = "SOL_TEST", status = Status, subStatus = "SECRET" });
            }
            return Json(new { available = Balance });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
    private sealed class Credentials(CoreApiSettings settings) : ICoreApiSettingsStore
    {
        public CoreApiSettings Settings = settings;
        public Task<CoreApiSettings> Get(string storeId) => Task.FromResult(Settings);
        public Task Save(string storeId, CoreApiSettings settings) => throw new NotSupportedException();
    }
    private sealed class MemoryStore : IVaultProvisioningStore, IVaultSweepStore
    {
        public VaultBinding Vault = new();
        private string _json = "{}";
        private readonly SemaphoreSlim _lock = new(1);
        public Task<VaultSweepSettings> Get(string storeId) => Task.FromResult(JsonSerializer.Deserialize<VaultSweepSettings>(_json)!);
        Task<VaultBinding?> IVaultProvisioningStore.Get(string storeId) => Task.FromResult<VaultBinding?>(Vault);
        public Task Save(string storeId, VaultBinding binding) => throw new NotSupportedException();
        public Task Save(string storeId, VaultSweepSettings settings) { _json = JsonSerializer.Serialize(settings); return Task.CompletedTask; }
        public Task<string[]> StoreIds() => Task.FromResult(new[] { "store" });
        public async Task<IAsyncDisposable> Lock(string storeId) { await _lock.WaitAsync(); return new Lease(_lock); }
        private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class TempProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
