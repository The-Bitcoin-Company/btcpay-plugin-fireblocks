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
    [InlineData("TIMEOUT", "Failed")]
    [InlineData("CANCELLING", "Pending")]
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
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyProviderTransactionIdUsesExternalIdentity(string transactionId)
    {
        var h = new Harness { CreateTransactionId = transactionId };
        var draft = await h.Draft();
        await h.Service.Confirm("store", draft.Id, "1");
        Assert.Null((await h.Service.Get("store")).Records.Single().TransactionId);
        // Also tolerate old or corrupted records that already contain blank IDs.
        var saved = await h.Store.Get("store");
        saved.Records.Single().TransactionId = transactionId;
        await h.Store.Save("store", saved);
        await h.Service.Refresh("store");
        Assert.Equal("/v1/transactions/external_tx_id/" + draft.Id, h.LastLookup);
        Assert.Equal("Completed", (await h.Service.Get("store")).Records.Single().State);
        Assert.Equal(1, h.Posts);
    }

    [Fact]
    public async Task BrokenStoreDoesNotStarveFollowingStores()
    {
        var h = new Harness();
        var draft = await h.Draft();
        await h.Service.Confirm("store", draft.Id, "1");
        h.Store.IncludeBrokenStore = true;
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        Assert.Equal("Completed", (await h.Service.Get("store")).Records.Single().State);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.ReconcileOutstanding(cancellation.Token));
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
        if (change is "old-tab" or "destination") Assert.Equal("Expired", (await h.Service.Confirm("store", draft.Id, "1")).State);
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

    [Fact]
    public async Task AutomationIsOffByDefaultEvenWithSavedDestination()
    {
        var h = new Harness();
        await h.Service.SaveDestination("store", Destination);
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        Assert.False((await h.Service.Get("store")).AutomationEnabled);
        Assert.Null((await h.Service.Get("store")).LastCheckAt);
        Assert.Equal(0, h.BalanceReads);
        Assert.Equal(0, h.Posts);
    }

    [Theory]
    [InlineData("9", 0)]
    [InlineData("10", 1)]
    [InlineData("10.123456789", 1)]
    public async Task AutomaticThresholdUsesAvailableBalanceAndSharedSubmission(string balance, int posts)
    {
        var h = new Harness { Balance = balance };
        await h.Enable();
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        var saved = await h.Service.Get("store");
        Assert.Equal(h.Clock.Now, saved.LastCheckAt);
        Assert.Equal(posts, h.Posts);
        if (posts == 0) Assert.Contains("below", saved.CheckDiagnostic);
        else
        {
            var record = Assert.Single(saved.Records);
            Assert.True(record.Automatic);
            Assert.Equal(h.Clock.Now, record.SubmittedAt);
            Assert.Equal(balance, record.Amount);
            Assert.Equal(Destination, record.Destination);
            Assert.Equal("Pending", record.State);
            Assert.Equal("Pending", h.StateAtSubmission);
            using var body = JsonDocument.Parse(h.Body!);
            Assert.Equal(record.Id, body.RootElement.GetProperty("externalTxId").GetString());
            Assert.Equal(record.Id, h.Key);
            Assert.Equal("TRANSFER", body.RootElement.GetProperty("operation").GetString());
            Assert.Equal(Destination, body.RootElement.GetProperty("destination").GetProperty("oneTimeAddress").GetProperty("address").GetString());
        }
    }

    [Fact]
    public async Task IntervalBoundaryPersistsAcrossRestartAndMissedChecksDoNotCatchUp()
    {
        var h = new Harness { Balance = "9" };
        await h.Enable();
        await h.Service.CheckAutomation("store");
        var first = h.Clock.Now;
        h.Service = h.NewService();
        h.Clock.Now = first.AddMinutes(60).AddTicks(-1);
        await h.Service.CheckAutomation("store");
        Assert.Equal(1, h.BalanceReads);
        h.Clock.Now = first.AddMinutes(60);
        await h.Service.CheckAutomation("store");
        Assert.Equal(2, h.BalanceReads);
        h.Clock.Now = first.AddDays(3);
        h.Balance = "10";
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        Assert.Equal(3, h.BalanceReads);
        Assert.Equal(1, h.Posts);
        Assert.Equal(h.Clock.Now, (await h.Service.Get("store")).LastCheckAt);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("SECRET")]
    public async Task UnavailableOrFullyLockedAvailableBalanceNeverUsesTotal(string balance)
    {
        var h = new Harness { Balance = balance };
        await h.Enable();
        await h.Service.CheckAutomation("store");
        var saved = await h.Service.Get("store");
        Assert.Equal(0, h.Posts);
        Assert.Empty(saved.Records);
        Assert.Contains("Check failed", saved.CheckDiagnostic);
        Assert.DoesNotContain("SECRET", saved.CheckDiagnostic);
        Assert.NotNull(saved.LastCheckAt);
    }

    [Fact]
    public async Task OverlappingServicesCreateOneSweepAndRestartCannotRepeatUncertainPost()
    {
        var h = new Harness { LoseResponse = true, NotFound = true };
        await h.Enable();
        var services = Enumerable.Range(0, 8).Select(_ => h.NewService()).ToArray();
        await Task.WhenAll(services.Select(s => s.CheckAutomation("store")));
        Assert.Equal(1, h.Posts);
        Assert.Single((await h.Service.Get("store")).Records);
        h.Service = h.NewService();
        h.Clock.Now = h.Clock.Now.AddDays(2);
        await h.Service.ReconcileOutstanding(CancellationToken.None);
        Assert.Equal(1, h.Posts);
        Assert.Contains("unresolved", (await h.Service.Get("store")).CheckDiagnostic);
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
    }

    [Fact]
    public async Task ManualPendingBlocksAutomationAndAutomaticSubmissionExpiresOldConfirmations()
    {
        var h = new Harness();
        var draft = await h.Draft();
        await h.Enable();
        await h.Service.Confirm("store", draft.Id, "1");
        await h.Service.CheckAutomation("store");
        Assert.Equal(1, h.Posts);
        Assert.Contains("unresolved", (await h.Service.Get("store")).CheckDiagnostic);
        await h.Service.Refresh("store");
        var old = await h.Service.Prepare("store");
        h.Clock.Now = h.Clock.Now.AddMinutes(60);
        await h.Service.CheckAutomation("store");
        Assert.Equal(2, h.Posts);
        Assert.Equal("Expired", (await h.Service.Confirm("store", old.Id, "1")).State);
        Assert.Equal(2, h.Posts);
    }

    [Theory]
    [InlineData(0, "10")]
    [InlineData(525601, "10")]
    [InlineData(60, "")]
    [InlineData(60, "0")]
    [InlineData(60, "-1")]
    [InlineData(60, "NaN")]
    [InlineData(60, "1e2")]
    [InlineData(60, "0.0000000001")]
    public async Task InvalidAutomationInputsAndCorruptSavedSettingsFailClosed(int interval, string minimum)
    {
        var h = new Harness();
        await h.Service.SaveDestination("store", Destination);
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.SaveAutomation("store", true, interval, minimum));
        Assert.False((await h.Service.Get("store")).AutomationEnabled);
        var saved = await h.Service.Get("store");
        saved.AutomationEnabled = true;
        saved.CheckIntervalMinutes = interval;
        saved.MinimumAvailableBalance = minimum;
        await h.Store.Save("store", saved);
        await h.Service.CheckAutomation("store");
        Assert.Equal(0, h.Posts);
        Assert.Contains("Check failed", (await h.Service.Get("store")).CheckDiagnostic);
        await h.Service.SaveAutomation("store", false, interval, minimum);
        Assert.False((await h.Service.Get("store")).AutomationEnabled);
    }

    [Fact]
    public async Task DestinationAndWorkspaceSafetyGatesApplyWithoutManualConfirmation()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.SaveAutomation("store", true, 60, "10"));
        await h.Enable();
        await h.Service.SaveDestination("store", "So11111111111111111111111111111111111111112");
        Assert.False((await h.Service.Get("store")).AutomationEnabled);
        await h.Service.CheckAutomation("store");
        Assert.Equal(0, h.Posts);
        await h.Service.SaveAutomation("store", true, 60, "10");
        h.Credentials.Settings.ApiKey = "other";
        await h.Service.CheckAutomation("store");
        Assert.Equal(0, h.Posts);
        await h.Service.SaveAutomation("store", false, 0, null);
        Assert.False((await h.Service.Get("store")).AutomationEnabled);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task DefaultConfigurationCannotPrepareOrSubmit()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
        Assert.Empty((await h.Service.Get("store")).Records);
        Assert.Equal(0, h.Posts);
    }

    [Fact]
    public async Task ExchangeDiscoverySelectionAndTransferReuseSweepSafety()
    {
        var h = new Harness();
        Assert.Equal(new SweepExchangeAccount("exchange", "Connected account"), Assert.Single(await h.Service.ExchangeAccounts("store")));
        await h.Controller().Sweep("save", "ignored", null, null, destinationType: "Exchange", exchangeAccountId: "exchange");
        var draft = await h.Service.Prepare("store");
        Assert.Equal("Exchange", draft.DestinationType);
        Assert.Equal("exchange", draft.Destination);
        Assert.Equal(0, h.Posts);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => h.Service.Confirm("store", draft.Id, "1")));
        Assert.Equal(1, h.Posts);
        using var body = JsonDocument.Parse(h.Body!);
        var destination = body.RootElement.GetProperty("destination");
        Assert.Equal("EXCHANGE_ACCOUNT", destination.GetProperty("type").GetString());
        Assert.Equal("exchange", destination.GetProperty("id").GetString());
        Assert.False(destination.TryGetProperty("oneTimeAddress", out _));
        Assert.Equal("TRANSFER", body.RootElement.GetProperty("operation").GetString());
        Assert.Equal(draft.Id, body.RootElement.GetProperty("externalTxId").GetString());
        Assert.Equal(draft.Id, h.Key);
        Assert.Equal("Pending", h.StateAtSubmission);
        await h.Service.SaveDestination("store", Destination);
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
        h.Service = h.NewService();
        await h.Service.Refresh("store");
        Assert.Equal("Completed", (await h.Service.Get("store")).Records.Single().State);
        await h.Service.Confirm("store", draft.Id, "1");
        Assert.Equal(1, h.Posts);
    }

    [Theory]
    [InlineData("USDC_ETH", "APPROVED")]
    [InlineData("USDC_SOL", "APPROVED")]
    [InlineData("", "APPROVED")]
    [InlineData("SOL_TEST", "DISCONNECTED")]
    [InlineData("SOL_TEST", "")]
    public async Task IncompatibleOrUnknownExchangeNeverSubmits(string asset, string status)
    {
        var h = new Harness { ExchangeAsset = asset, ExchangeStatus = status };
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.SaveDestination("store", "exchange", "Exchange"));
        Assert.Equal(0, h.Posts);
        Assert.Equal("", (await h.Service.Get("store")).Destination);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("status")]
    [InlineData("missing")]
    public async Task ExchangeIsRevalidatedBeforePrepareAndBeforeConfirm(string change)
    {
        var h = new Harness();
        await h.Service.SaveDestination("store", "exchange", "Exchange");
        var draft = await h.Service.Prepare("store");
        if (change == "asset") h.ExchangeAsset = "USDC_ETH";
        if (change == "status") h.ExchangeStatus = "DISCONNECTED";
        if (change == "missing") h.ExchangeUnavailable = true;
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Prepare("store"));
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.Confirm("store", draft.Id, "1"));
        Assert.Equal(0, h.Posts);
    }

    [Fact]
    public async Task SwitchingDestinationsInvalidatesOldConfirmationsEvenWhenSwitchingBack()
    {
        var h = new Harness();
        var wallet = await h.Draft();
        await h.Service.SaveDestination("store", "exchange", "Exchange");
        await h.Service.SaveDestination("store", Destination);
        Assert.Equal("Expired", (await h.Service.Confirm("store", wallet.Id, "1")).State);
        await h.Service.SaveDestination("store", "exchange", "Exchange");
        var exchange = await h.Service.Prepare("store");
        await h.Service.SaveDestination("store", Destination);
        Assert.Equal("Expired", (await h.Service.Confirm("store", exchange.Id, "1")).State);
        Assert.Equal(0, h.Posts);
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.SaveDestination("store", Destination, "unknown"));
    }

    [Fact]
    public async Task DiscoveryPagesSafelyAndUnavailableAccountsAreNotOffered()
    {
        var h = new Harness { PagedExchanges = true };
        Assert.Equal(2, (await h.Service.ExchangeAccounts("store")).Count);
        h.ExchangeStatus = "DISCONNECTED";
        Assert.Empty(await h.Service.ExchangeAccounts("store"));
        h.ExchangeUnavailable = true;
        await Assert.ThrowsAsync<CoreApiException>(() => h.Service.ExchangeAccounts("store"));
        Assert.Equal(0, h.Posts);
    }

    [Fact]
    public void LegacyWalletSettingsAndRecordsRemainWalletDestinations()
    {
        Assert.Equal("Wallet", JsonSerializer.Deserialize<VaultSweepSettings>("{\"Destination\":\"wallet\"}")!.DestinationType);
        Assert.Equal("Wallet", JsonSerializer.Deserialize<SweepRecord>("{\"Destination\":\"wallet\"}")!.DestinationType);
    }

    private sealed class Harness : HttpMessageHandler
    {
        public readonly MemoryStore Store = new();
        public readonly Credentials Credentials;
        public VaultSweepService Service;
        public string Balance = "10.123456789", Status = "COMPLETED", CreateTransactionId = "tx";
        public int Posts, BalanceReads;
        public readonly Clock Clock = new();
        public string ExchangeAsset = "SOL_TEST", ExchangeStatus = "APPROVED";
        public bool ExchangeUnavailable, PagedExchanges;
        public async Task Enable()
        {
            await Service.SaveDestination("store", Destination);
            await Service.SaveAutomation("store", true, 60, "10");
        }
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
        public VaultSweepService NewService() => new(new CoreApiClient(new HttpClient(this)), Credentials, Store, Store, timeProvider: Clock);
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
                return Json(new { id = CreateTransactionId, status = "COMPLETED" });
            }
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/transactions/"))
            {
                LastLookup = request.RequestUri.AbsolutePath;
                if (NotFound) return new(HttpStatusCode.NotFound) { Content = new StringContent("SECRET") };
                return Json(new { id = "tx", externalTxId = WrongIdentity ? "other" : (await Store.Get("store")).Records.Last(r => r.Unresolved).Id, assetId = "SOL_TEST", status = Status, subStatus = "SECRET" });
            }
            if (request.RequestUri.AbsolutePath.StartsWith("/v1/exchange_accounts"))
            {
                if (ExchangeUnavailable) return new(HttpStatusCode.NotFound) { Content = new StringContent("SECRET") };
                if (request.RequestUri.AbsolutePath.EndsWith("/paged"))
                {
                    var second = request.RequestUri.Query.Contains("after=");
                    return Json(new { exchanges = new[] { new { id = second ? "second" : "exchange", name = "Connected account", status = ExchangeStatus } }, paging = new { after = PagedExchanges && !second ? "page2" : "" } });
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/exchange"))
                    return Json(new { id = "exchange", status = ExchangeStatus });
                return Json(new { id = ExchangeAsset });
            }
            BalanceReads++;
            return Json(new { available = Balance, total = "1000", lockedAmount = "1000" });
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
        public bool IncludeBrokenStore;
        private string _json = "{}";
        private readonly SemaphoreSlim _lock = new(1);
        public Task<VaultSweepSettings> Get(string storeId) => storeId == "broken"
            ? throw new JsonException("SECRET")
            : Task.FromResult(JsonSerializer.Deserialize<VaultSweepSettings>(_json)!);
        Task<VaultBinding?> IVaultProvisioningStore.Get(string storeId) => Task.FromResult<VaultBinding?>(Vault);
        public Task Save(string storeId, VaultBinding binding) => throw new NotSupportedException();
        public Task Save(string storeId, VaultSweepSettings settings) { _json = JsonSerializer.Serialize(settings); return Task.CompletedTask; }
        public Task<string[]> StoreIds() => Task.FromResult(IncludeBrokenStore ? new[] { "broken", "store" } : new[] { "store" });
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
