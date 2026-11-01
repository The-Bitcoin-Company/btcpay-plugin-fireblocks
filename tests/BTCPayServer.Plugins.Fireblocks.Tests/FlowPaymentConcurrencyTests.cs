using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowPaymentConcurrencyTests
{
    [Fact]
    public async Task RelationalAttemptNumberPreventsDuplicateCreationAcrossServiceInstances()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FireblocksDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new FireblocksDbContext(options))
            await context.Database.EnsureCreatedAsync();
        var factory = new TestDbContextFactory(options);
        var client = new ConcurrentFlowClient();
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var first = new FlowPaymentService(new FlowPaymentAttemptRepository(factory), client, new FakeTimeProvider(now));
        var second = new FlowPaymentService(new FlowPaymentAttemptRepository(factory), client, new FakeTimeProvider(now));

        var tasks = new[]
        {
            first.CreateOrGetPaymentLinkAsync("store", "invoice", 5m, now.AddMinutes(15), TestData.Config()),
            second.CreateOrGetPaymentLinkAsync("store", "invoice", 5m, now.AddMinutes(15), TestData.Config())
        };
        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        Assert.Equal(1, client.CallCount);
        var completed = Assert.Single(tasks, task => task.Status == TaskStatus.RanToCompletion);
        Assert.NotNull(await completed);
        Assert.IsType<FlowPaymentCreationException>(exception);
        Assert.Single(await new FlowPaymentAttemptRepository(factory).ListAsync("store", "invoice"));
    }

    [Fact]
    public async Task RelationalReservationAllowsOnlyOneTransactionAcrossRepositories()
    {
        var factory = await RelationalFactory();
        var repository = new FlowPaymentAttemptRepository(factory);
        await repository.AddAsync(Attempt());
        var first = await repository.GetByFlowIdAsync("store", "flow-1");
        var second = await repository.GetByFlowIdAsync("store", "flow-1");
        first!.SettlementTransactionId = "tx-first";
        first.SettlementAmountAtomic = "1000000";
        first.SettlementAmount = 1m;
        second!.SettlementTransactionId = "tx-second";
        second.SettlementAmountAtomic = "2000000";
        second.SettlementAmount = 2m;

        var results = await Task.WhenAll(
            repository.ReserveSettlementAsync(first),
            new FlowPaymentAttemptRepository(factory).ReserveSettlementAsync(second));

        Assert.Single(results, result => result);
        var persisted = await repository.GetByFlowIdAsync("store", "flow-1");
        Assert.Equal(results[0] ? "tx-first" : "tx-second", persisted!.SettlementTransactionId);
    }

    [Fact]
    public async Task StaleUpdatesCannotEraseSettlementOrRegressTerminalState()
    {
        var factory = await RelationalFactory();
        var repository = new FlowPaymentAttemptRepository(factory);
        await repository.AddAsync(Attempt());
        var stale = await repository.GetByFlowIdAsync("store", "flow-1");
        var settled = await repository.GetByFlowIdAsync("store", "flow-1");
        settled!.SettlementTransactionId = "tx-settled";
        settled.SettlementAmountAtomic = "25000000";
        settled.SettlementAmount = 25m;
        Assert.True(await repository.ReserveSettlementAsync(settled));
        settled.State = FlowAttemptState.Settled;
        await repository.FinalizeSettlementAsync(settled);

        stale!.State = FlowAttemptState.Created;
        stale.Error = "delayed diagnostic";
        await repository.UpdateReconciliationDiagnosticAsync(stale);
        await repository.UpdateAsync(stale); // even a misplaced legacy caller is guarded
        stale.SettlementTransactionId = "tx-other";
        stale.SettlementAmountAtomic = "1";
        stale.SettlementAmount = 0.000001m;
        Assert.False(await new FlowPaymentAttemptRepository(factory).ReserveSettlementAsync(stale));

        var persisted = await repository.GetByFlowIdAsync("store", "flow-1");
        Assert.Equal(FlowAttemptState.Settled, persisted!.State);
        Assert.Equal("tx-settled", persisted.SettlementTransactionId);
        Assert.Equal("25000000", persisted.SettlementAmountAtomic);
        Assert.Equal(25m, persisted.SettlementAmount);
        Assert.Null(persisted.Error);
    }

    [Fact]
    public async Task StaleTerminalUpdateCannotStrandReservedSettlement()
    {
        var factory = await RelationalFactory();
        var repository = new FlowPaymentAttemptRepository(factory);
        await repository.AddAsync(Attempt());
        var reserved = await repository.GetByFlowIdAsync("store", "flow-1");
        reserved!.SettlementTransactionId = "tx-settled";
        reserved.SettlementAmountAtomic = "25000000";
        reserved.SettlementAmount = 25m;
        Assert.True(await repository.ReserveSettlementAsync(reserved));

        var staleTerminal = await repository.GetByFlowIdAsync("store", "flow-1");
        staleTerminal!.State = FlowAttemptState.Cancelled;
        staleTerminal.Error = "stale cancellation";
        await new FlowPaymentAttemptRepository(factory).UpdateReconciliationTerminalAsync(staleTerminal);
        reserved.State = FlowAttemptState.Settled;
        reserved.Error = null;
        await repository.FinalizeSettlementAsync(reserved);

        var persisted = await repository.GetByFlowIdAsync("store", "flow-1");
        Assert.Equal(FlowAttemptState.Settled, persisted!.State);
        Assert.Equal("tx-settled", persisted.SettlementTransactionId);
        Assert.Null(persisted.Error);
        Assert.Empty(await repository.ListOutstandingAsync(10));
    }

    private static async Task<TestDbContextFactory> RelationalFactory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FireblocksDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new FireblocksDbContext(options))
            await context.Database.EnsureCreatedAsync();
        return new TestDbContextFactory(options);
    }

    private static FlowPaymentAttempt Attempt() => new()
    {
        Id = "attempt-1",
        StoreId = "store",
        InvoiceId = "invoice",
        AttemptNumber = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        ApiEndpoint = "https://app.dynamic.test/api/v0",
        EnvironmentId = "env_test",
        RequestedUsd = 25m,
        CreationRequest = "{}",
        State = FlowAttemptState.Created,
        FlowId = "flow-1"
    };

    private sealed class ConcurrentFlowClient : IFlowApiClient
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<FlowSnapshot> GetFlowAsync(
            FireblocksStoreConfig config,
            string flowId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<FlowPaymentLink> CreatePaymentLinkAsync(
            FireblocksStoreConfig config,
            FlowPaymentLinkRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(100, cancellationToken);
            return new FlowPaymentLink("flow-only", "https://pay.dynamic.test/only", null);
        }
    }
}
