using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

internal static class TestData
{
    public const string Credential = "test-flow-secret";

    public static FireblocksStoreConfig Config() => new()
    {
        EnvironmentId = "env_test",
        ApiEndpoint = "https://app.dynamic.test/api/v0",
        ApiCredential = Credential,
        WebhookSecret = "test-webhook-secret",
        HostedPaymentBaseUrl = "https://pay.dynamic.test",
        SolanaDestinationAddress = FireblocksStoreConfig.UsdcMint,
        PaymentExpiryMinutes = 15
    };
}

internal sealed class FakeFlowApiClient : IFlowApiClient
{
    private readonly Queue<Func<FlowPaymentLinkRequest, Task<FlowPaymentLink>>> _responses = new();

    public List<FlowPaymentLinkRequest> Requests { get; } = new();

    public void Returns(FlowPaymentLink response) => _responses.Enqueue(_ => Task.FromResult(response));

    public void Throws(FlowApiException exception) => _responses.Enqueue(_ => Task.FromException<FlowPaymentLink>(exception));

    public Func<string, Task<FlowSnapshot>>? FlowHandler { get; set; }

    public Task<FlowSnapshot> GetFlowAsync(
        FireblocksStoreConfig config,
        string flowId,
        CancellationToken cancellationToken = default) =>
        FlowHandler is null
            ? throw new InvalidOperationException("No fake Flow reconciliation response configured.")
            : FlowHandler(flowId);

    public Task<FlowPaymentLink> CreatePaymentLinkAsync(
        FireblocksStoreConfig config,
        FlowPaymentLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
            throw new InvalidOperationException("No fake Flow response configured.");
        return _responses.Dequeue()(request);
    }
}

internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now += duration;
}

internal sealed class TestDbContextFactory(DbContextOptions<FireblocksDbContext> options)
    : IDbContextFactory<FireblocksDbContext>
{
    public FireblocksDbContext CreateDbContext() => new(options);

    public static TestDbContextFactory Create()
    {
        var options = new DbContextOptionsBuilder<FireblocksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestDbContextFactory(options);
    }
}
