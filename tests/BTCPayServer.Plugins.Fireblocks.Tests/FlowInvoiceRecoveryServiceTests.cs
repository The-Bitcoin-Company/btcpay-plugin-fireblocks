using BTCPayServer.Events;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowInvoiceRecoveryServiceTests
{
    [Fact]
    public async Task StartupRepairReevaluatesEverySettledInvoiceOnce()
    {
        var factory = TestDbContextFactory.Create();
        var repository = new FlowPaymentAttemptRepository(factory);
        await repository.AddAsync(Attempt("attempt-1", "invoice-1", 1));
        await repository.AddAsync(Attempt("attempt-2", "invoice-1", 2));
        await repository.AddAsync(Attempt("attempt-3", "invoice-2", 1));
        var events = new EventAggregator(new BTCPayServer.Logging.Logs());
        var repaired = new List<string>();
        using var subscription = events.Subscribe<InvoiceNeedUpdateEvent>(e => repaired.Add(e.InvoiceId));
        var service = new FlowInvoiceRecoveryService(repository, events);

        await service.RepairRecordedSettlementsAsync();

        Assert.Equal(2, repaired.Count);
        Assert.Contains("invoice-1", repaired);
        Assert.Contains("invoice-2", repaired);
    }

    [Fact]
    public async Task HostedServiceRetriesInvoiceRepairUntilFirstSuccess()
    {
        var recovery = new RetryRecovery();
        var hosted = new FlowReconciliationHostedService(
            new NoopReconciliation(), recovery, NullLogger<FlowReconciliationHostedService>.Instance,
            TimeSpan.FromMilliseconds(5));
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await recovery.SecondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(recovery.Calls >= 2);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RetryRecovery : IFlowInvoiceRecoveryService
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource SecondAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RepairRecordedSettlementsAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
                throw new InvalidOperationException("transient database failure");
            SecondAttempt.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class NoopReconciliation : IFlowReconciliationService
    {
        public Task ReconcileAsync(string storeId, string flowId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ReconcileOutstandingAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static FlowPaymentAttempt Attempt(string id, string invoiceId, int number) => new()
    {
        Id = id,
        StoreId = "store",
        InvoiceId = invoiceId,
        AttemptNumber = number,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        ApiEndpoint = "https://app.dynamic.test/api/v0",
        EnvironmentId = "env_test",
        RequestedUsd = 25m,
        CreationRequest = "{}",
        State = FlowAttemptState.Settled,
        FlowId = $"flow-{id}",
        SettlementTransactionId = $"tx-{id}",
        SettlementAmountAtomic = "25000000",
        SettlementAmount = 25m
    };
}
