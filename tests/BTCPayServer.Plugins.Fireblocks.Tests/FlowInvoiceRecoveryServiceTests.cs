using BTCPayServer.Events;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
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
