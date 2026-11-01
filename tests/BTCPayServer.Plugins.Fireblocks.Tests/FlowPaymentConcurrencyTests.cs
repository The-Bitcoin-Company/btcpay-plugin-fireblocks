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

    private sealed class ConcurrentFlowClient : IFlowApiClient
    {
        private int _callCount;
        public int CallCount => _callCount;

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
