using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowWebhookServiceTests
{
    [Fact]
    public void SignatureCoversExactBytesAndUsesSha256Prefix()
    {
        var body = "{\"type\":\"flow.risk.updated\"}"u8.ToArray();
        var signature = Sign(body);

        Assert.True(FlowWebhookSignature.IsValid(body, signature, TestData.Config().WebhookSecret));
        Assert.True(FlowWebhookSignature.IsValid(body, signature["sha256=".Length..], TestData.Config().WebhookSecret));
        body[2] = (byte)'x';
        Assert.False(FlowWebhookSignature.IsValid(body, signature, TestData.Config().WebhookSecret));
        Assert.False(FlowWebhookSignature.IsValid(body, "sha256=00", TestData.Config().WebhookSecret));
    }

    [Fact]
    public async Task InvalidSignatureIsRejectedBeforeAnyPersistenceOrReconciliation()
    {
        var factory = TestDbContextFactory.Create();
        var reconciliation = new FakeReconciliation();
        var service = CreateService(factory, reconciliation);
        var body = Body("event-1");

        await AssertWebhookRejected(() => service.ProcessAsync("store", body, "sha256=" + new string('0', 64)));

        Assert.Empty(await Events(factory));
        Assert.Equal(0, reconciliation.Count);
    }

    [Fact]
    public async Task ValidEventIsPersistedBeforeReconciliationAndDuplicateIsIdempotent()
    {
        var factory = TestDbContextFactory.Create();
        var reconciliation = new FakeReconciliation
        {
            Handler = async (_, _) =>
            {
                // The receipt must already be durable when reconciliation starts.
                Assert.Single(await Events(factory));
            }
        };
        var service = CreateService(factory, reconciliation);
        var body = Body("event-1");
        var signature = Sign(body);

        await service.ProcessAsync("store", body, signature);
        await service.ProcessAsync("store", body, signature);

        var stored = Assert.Single(await Events(factory));
        Assert.Equal("event-1", stored.Id);
        Assert.Equal("flow.settlement.updated", stored.EventType);
        Assert.Equal(Encoding.UTF8.GetString(body), stored.Payload);
        Assert.Equal(2, reconciliation.Count);
    }

    [Fact]
    public async Task ReconciliationFailureDoesNotLoseAcknowledgedEvent()
    {
        var factory = TestDbContextFactory.Create();
        var reconciliation = new FakeReconciliation
        {
            Handler = (_, _) => throw new InvalidOperationException("sensitive provider detail")
        };
        var service = CreateService(factory, reconciliation);
        var body = Body("event-1");

        await service.ProcessAsync("store", body, Sign(body));

        Assert.Single(await Events(factory));
    }

    private static FlowWebhookService CreateService(TestDbContextFactory factory, FakeReconciliation reconciliation) =>
        new(new FakeConfigurations(TestData.Config()), new FlowPaymentAttemptRepository(factory), reconciliation,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<FlowWebhookService>.Instance);

    private static async Task<FlowWebhookEvent[]> Events(TestDbContextFactory factory)
    {
        await using var context = factory.CreateDbContext();
        return await context.WebhookEvents.ToArrayAsync();
    }

    private static byte[] Body(string eventId) => Encoding.UTF8.GetBytes(
        $$$"""{"id":"{{{eventId}}}","type":"flow.settlement.updated","data":{"flowId":"flow-1","environmentId":"env_test"}}""");

    private static string Sign(ReadOnlySpan<byte> body) => "sha256=" + Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestData.Config().WebhookSecret), body)).ToLowerInvariant();

    private static async Task AssertWebhookRejected(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<FlowWebhookException>(action);
        Assert.Equal(401, exception.StatusCode);
        Assert.DoesNotContain(TestData.Config().WebhookSecret, exception.Message);
    }

    private sealed class FakeConfigurations(FireblocksStoreConfig config) : IFlowStoreConfigurationProvider
    {
        public Task<FireblocksStoreConfig?> GetAsync(string storeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FireblocksStoreConfig?>(config);
    }

    private sealed class FakeReconciliation : IFlowReconciliationService
    {
        public int Count { get; private set; }
        public Func<string, string, Task>? Handler { get; init; }

        public Task ReconcileAsync(string storeId, string flowId, CancellationToken cancellationToken = default)
        {
            Count++;
            return Handler?.Invoke(storeId, flowId) ?? Task.CompletedTask;
        }

        public Task ReconcileOutstandingAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
