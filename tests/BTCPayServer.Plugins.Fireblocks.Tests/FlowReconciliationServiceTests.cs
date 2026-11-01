using System.Globalization;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using BTCPayServer.Plugins.Fireblocks.Payments;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecutionAndRiskReviewNeverSettleUntilAuthoritativeStateAllowsIt()
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var snapshot = Snapshot(attempt, settlementStatus: "processing", risk: "approved");
        var client = new FakeFlowApiClient { FlowHandler = _ => Task.FromResult(snapshot) };
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, client, recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);
        Assert.Empty(recorder.Payments);

        snapshot = Snapshot(attempt, settlementStatus: "completed", risk: "review");
        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);
        Assert.Empty(recorder.Payments);

        snapshot = Snapshot(attempt, settlementStatus: "completed", risk: "approved");
        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);
        var payment = Assert.Single(recorder.Payments);
        Assert.Equal("25.000000", payment.Evidence.Amount);
        Assert.Equal("25000000", payment.Evidence.AmountAtomic);

        // A repeated notification and a new service after restart cannot add it twice.
        var restarted = Service(factory, client, recorder);
        await restarted.ReconcileAsync(attempt.StoreId, attempt.FlowId!);
        Assert.Single(recorder.Payments);
        Assert.Equal(3, client.FlowRequests.Count);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("environment")]
    [InlineData("memo")]
    [InlineData("config")]
    [InlineData("chain")]
    [InlineData("mint")]
    [InlineData("destination")]
    public async Task WrongBindingAssetNetworkOrDestinationCannotCredit(string wrongField)
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var snapshot = Snapshot(attempt, wrongField: wrongField);
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, Client(snapshot), recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        Assert.Empty(recorder.Payments);
        var persisted = await new FlowPaymentAttemptRepository(factory).GetByFlowIdAsync("store", "flow-1");
        Assert.Equal(FlowAttemptState.Created, persisted!.State);
        Assert.NotNull(persisted.Error);
    }

    [Theory]
    [InlineData("4.999999", "4999999")]
    [InlineData("25.000000", "25000000")]
    [InlineData("30.500000", "30500000")]
    public async Task ExactUnderFullAndOverpaymentAmountsArePreserved(string amount, string atomic)
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, Client(Snapshot(attempt, amount: amount, atomic: atomic)), recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        var payment = Assert.Single(recorder.Payments);
        Assert.Equal(amount, payment.Evidence.Amount);
        Assert.Equal(atomic, payment.Evidence.AmountAtomic);
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), payment.Evidence.TokenAmount);
        Assert.Equal(25m, payment.Attempt.RequestedUsd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0000001")]
    [InlineData("1e2")]
    public async Task MissingInvalidOrExcessPrecisionAmountCannotCredit(string? amount)
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, Client(Snapshot(attempt, amount: amount, atomic: null)), recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        Assert.Empty(recorder.Payments);
    }

    [Fact]
    public async Task AtomicAmountMustAgreeWithDecimalEvidence()
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, Client(Snapshot(attempt, atomic: "25000001")), recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        Assert.Empty(recorder.Payments);
    }

    [Fact]
    public async Task LateSettlementAfterLocalAndProviderExpiryCanBeRecovered()
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        attempt.ExpiresAt = Now.AddMinutes(-20);
        attempt.ProviderExpiresAt = Now.AddMinutes(-15);
        await new FlowPaymentAttemptRepository(factory).UpdateAsync(attempt);
        var completedAt = Now.AddMinutes(-10);
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, Client(Snapshot(attempt, flowStatus: "expired", completedAt: completedAt)), recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        var payment = Assert.Single(recorder.Payments);
        Assert.Equal(completedAt, payment.Evidence.ReceivedAt);
        // BTCPay, not this service, decides its normal PaidLate transition.
        Assert.Equal(FlowAttemptState.Settled, payment.Attempt.State);
    }

    [Fact]
    public async Task OutstandingPassRecoversMultipleAttemptsAfterMissedNotificationsAndRestart()
    {
        var factory = TestDbContextFactory.Create();
        var first = await SeedAttempt(factory, "attempt-1", "flow-1");
        var second = await SeedAttempt(factory, "attempt-2", "flow-2");
        var snapshots = new[] { first, second }.ToDictionary(a => a.FlowId!, a => Snapshot(a));
        var client = new FakeFlowApiClient { FlowHandler = id => Task.FromResult(snapshots[id]) };
        var recorder = new FakePaymentRecorder();
        var restarted = Service(factory, client, recorder);

        await restarted.ReconcileOutstandingAsync();

        Assert.Equal(2, recorder.Payments.Count);
        Assert.Contains(recorder.Payments, p => p.Attempt.Id == first.Id);
        Assert.Contains(recorder.Payments, p => p.Attempt.Id == second.Id);
    }

    [Theory]
    [InlineData("failed", FlowAttemptState.Failed)]
    [InlineData("cancelled", FlowAttemptState.Cancelled)]
    public async Task FailedOrCancelledFlowDoesNotCreditEvenWithCompletedSettlement(
        string status,
        FlowAttemptState expected)
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var recorder = new FakePaymentRecorder();
        var service = Service(factory, Client(Snapshot(attempt, flowStatus: status)), recorder);

        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        Assert.Empty(recorder.Payments);
        var persisted = await new FlowPaymentAttemptRepository(factory).GetByFlowIdAsync("store", "flow-1");
        Assert.Equal(expected, persisted!.State);
    }

    [Fact]
    public async Task ChangedTransactionOrAmountAfterReservationCannotDoubleCreditAttempt()
    {
        var factory = TestDbContextFactory.Create();
        var attempt = await SeedAttempt(factory);
        var snapshot = Snapshot(attempt);
        var client = new FakeFlowApiClient { FlowHandler = _ => Task.FromResult(snapshot) };
        var recorder = new FakePaymentRecorder { Result = FlowPaymentRecordResult.Conflict };
        var service = Service(factory, client, recorder);
        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        snapshot = Snapshot(attempt, transactionId: "tx-other");
        recorder.Result = FlowPaymentRecordResult.Recorded;
        await service.ReconcileAsync(attempt.StoreId, attempt.FlowId!);

        Assert.Single(recorder.Payments);
    }

    [Fact]
    public void BtcPayPaymentRecordRetainsAssetEvidenceAndExactPrecision()
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var invoice = new InvoiceEntity { Id = "invoice", StoreId = "store", Currency = "USD", Price = 25m };
        invoice.AddRate(new CurrencyPair("USDC", "USD"), 1m);
        invoice.SetPaymentPrompt(handler.PaymentMethodId, new PaymentPrompt
        {
            ParentEntity = invoice,
            PaymentMethodId = handler.PaymentMethodId,
            Currency = "USD",
            Destination = "https://pay.dynamic.test/flow",
            Divisibility = 2
        });
        var attempt = Attempt("attempt-1", "flow-1");
        var evidence = new FlowSettlementEvidence(
            "solana-transaction", "25.000001", "25000001", 25.000001m, Now,
            FireblocksStoreConfig.UsdcMint, Now.AddMinutes(-1));

        var data = BtcPayFlowPaymentRecorder.CreatePaymentData(invoice, attempt, evidence, handler);

        Assert.Equal("solana-transaction", data.Id);
        Assert.Equal("invoice", data.InvoiceDataId);
        Assert.Equal("USDC", data.Currency);
        Assert.Equal(25.000001m, data.Amount);
        Assert.Equal(PaymentStatus.Settled, data.Status);
        var payment = data.GetBlob();
        Assert.Equal(FireblocksStoreConfig.UsdcMint, payment.Destination);
        Assert.Equal(6, payment.Divisibility);
        var details = payment.GetDetails<FireblocksPaymentDetails>(handler);
        Assert.Equal("25.000001", details.Amount);
        Assert.Equal("25000001", details.AmountAtomic);
        Assert.Equal(25m, details.RequestedUsd);
        Assert.Equal("SOL", details.Network);
        Assert.Equal("101", details.ChainId);
        Assert.Equal(FireblocksStoreConfig.UsdcMint, details.TokenAddress);
        Assert.Equal(attempt.Id, details.AttemptId);
        Assert.Equal(attempt.FlowId, details.FlowId);
    }

    private static FlowReconciliationService Service(
        TestDbContextFactory factory,
        FakeFlowApiClient client,
        FakePaymentRecorder recorder) =>
        new(new FlowPaymentAttemptRepository(factory), new FakeConfigurations(TestData.Config()), client, recorder,
            new FakeTimeProvider(Now), NullLogger<FlowReconciliationService>.Instance);

    private static FakeFlowApiClient Client(FlowSnapshot snapshot) =>
        new() { FlowHandler = _ => Task.FromResult(snapshot) };

    private static async Task<FlowPaymentAttempt> SeedAttempt(
        TestDbContextFactory factory,
        string id = "attempt-1",
        string flowId = "flow-1")
    {
        var attempt = Attempt(id, flowId);
        await new FlowPaymentAttemptRepository(factory).AddAsync(attempt);
        return attempt;
    }

    private static FlowPaymentAttempt Attempt(string id, string flowId)
    {
        var config = TestData.Config();
        return new FlowPaymentAttempt
        {
            Id = id,
            StoreId = "store",
            InvoiceId = "invoice",
            AttemptNumber = int.Parse(id[^1..]),
            CreatedAt = Now.AddMinutes(-15),
            ExpiresAt = Now.AddMinutes(-1),
            ApiEndpoint = config.ApiEndpoint,
            EnvironmentId = config.EnvironmentId,
            RequestedUsd = 25m,
            CreationRequest = System.Text.Json.JsonSerializer.Serialize(
                FlowPaymentRequestFactory.Create(config, "invoice", id, 25m, 900)),
            State = FlowAttemptState.Created,
            FlowId = flowId,
            PaymentUrl = "https://pay.dynamic.test/flow"
        };
    }

    private static FlowSnapshot Snapshot(
        FlowPaymentAttempt attempt,
        string? wrongField = null,
        string settlementStatus = "completed",
        string risk = "approved",
        string flowStatus = "pending",
        string? amount = "25.000000",
        string? atomic = "25000000",
        string transactionId = "tx-1",
        DateTimeOffset? completedAt = null)
    {
        var config = TestData.Config();
        var request = FlowPaymentRequestFactory.Create(config, attempt.InvoiceId, attempt.Id, 25m, 900);
        var snapshot = new FlowSnapshot
        {
            Id = wrongField == "flow" ? "flow-other" : attempt.FlowId,
            EnvironmentId = wrongField == "environment" ? "env_other" : attempt.EnvironmentId,
            Mode = "payment",
            Status = flowStatus,
            Memo = new FlowMemo
            {
                InvoiceId = attempt.InvoiceId,
                PaymentAttemptId = wrongField == "memo" ? "attempt-other" : attempt.Id
            },
            SettlementConfig = wrongField == "config"
                ? new FlowSettlementConfig
                {
                    Settlements =
                    [
                        new FlowSettlement
                        {
                            ChainName = "SOL", ChainId = "101", Symbol = "USDC",
                            TokenAddress = "different-mint", TokenDecimals = 6
                        }
                    ]
                }
                : request.SettlementConfig,
            DestinationConfig = request.DestinationConfig,
            Execution = new FlowExecutionStatus { Status = "source_confirmed" },
            Settlement = new FlowSettlementStatus
            {
                Status = settlementStatus,
                Amount = amount,
                AmountAtomic = atomic,
                TransactionId = transactionId,
                Destination = wrongField == "destination" ? "wrong-destination" : config.SolanaDestinationAddress,
                ChainName = wrongField == "chain" ? "ETH" : "SOL",
                ChainId = wrongField == "chain" ? "1" : "101",
                Symbol = "USDC",
                TokenAddress = wrongField == "mint" ? "wrong-mint" : FireblocksStoreConfig.UsdcMint,
                TokenDecimals = 6,
                CompletedAt = completedAt
            },
            Risk = new FlowRiskStatus { Status = risk }
        };
        return snapshot;
    }

    private sealed class FakeConfigurations(FireblocksStoreConfig config) : IFlowStoreConfigurationProvider
    {
        public Task<FireblocksStoreConfig?> GetAsync(string storeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FireblocksStoreConfig?>(config);
    }

    private sealed class FakePaymentRecorder : IFlowPaymentRecorder
    {
        public List<(FlowPaymentAttempt Attempt, FlowSettlementEvidence Evidence)> Payments { get; } = [];
        public FlowPaymentRecordResult Result { get; set; } = FlowPaymentRecordResult.Recorded;

        public Task<FlowPaymentRecordResult> RecordAsync(
            FlowPaymentAttempt attempt,
            FlowSettlementEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            Payments.Add((attempt, evidence));
            return Task.FromResult(Result);
        }
    }
}
