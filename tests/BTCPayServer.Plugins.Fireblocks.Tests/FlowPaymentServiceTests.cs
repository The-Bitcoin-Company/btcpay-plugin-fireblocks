using System.Net;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowPaymentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessPersistsImmutableBindingAcrossContextsAndRestart()
    {
        var factory = TestDbContextFactory.Create();
        var client = new FakeFlowApiClient();
        client.Returns(new FlowPaymentLink("flow-123", "https://pay.dynamic.test/?flow=abc", null));
        var time = new FakeTimeProvider(Now);
        var service = CreateService(factory, client, time);

        var created = await service.CreateOrGetPaymentLinkAsync(
            "store", "invoice", 25m, Now.AddMinutes(20), TestData.Config());

        Assert.Equal(FlowAttemptState.Created, created.State);
        Assert.Equal("flow-123", created.FlowId);
        Assert.Equal(Now.AddMinutes(15), created.ExpiresAt);
        var request = Assert.Single(client.Requests);
        Assert.Equal("invoice", request.Memo.InvoiceId);
        Assert.Equal(created.Id, request.Memo.PaymentAttemptId);
        Assert.Equal(900, request.ExpiresIn);

        // A new repository, context, and service simulates a process restart.
        var restartedRepository = new FlowPaymentAttemptRepository(factory);
        var persisted = await restartedRepository.GetLatestAsync("store", "invoice");
        Assert.NotNull(persisted);
        Assert.Equal(created.Id, persisted.Id);
        Assert.Equal(created.CreationRequest, persisted.CreationRequest);
        Assert.Equal(25m, persisted.RequestedUsd);
        Assert.DoesNotContain(TestData.Credential, persisted.CreationRequest);

        var restarted = new FlowPaymentService(restartedRepository, client, time);
        var reused = await restarted.CreateOrGetPaymentLinkAsync(
            "store", "invoice", 25m, Now.AddMinutes(20), TestData.Config());
        Assert.Equal(created.Id, reused.Id);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task UncertainCreationIsNotBlindlyRetriedAndCanExpireIntoNewAttempt()
    {
        var factory = TestDbContextFactory.Create();
        var client = new FakeFlowApiClient();
        client.Throws(new FlowApiException("timeout", uncertain: true));
        client.Returns(new FlowPaymentLink("flow-second", "https://pay.dynamic.test/second", null));
        var time = new FakeTimeProvider(Now);
        var service = CreateService(factory, client, time);

        var exception = await Assert.ThrowsAsync<FlowPaymentCreationException>(() =>
            service.CreateOrGetPaymentLinkAsync("store", "invoice", 5m, Now.AddMinutes(20), TestData.Config()));
        Assert.True(exception.Uncertain);

        await Assert.ThrowsAsync<FlowPaymentCreationException>(() =>
            service.CreateOrGetPaymentLinkAsync("store", "invoice", 5m, Now.AddMinutes(20), TestData.Config()));
        Assert.Single(client.Requests);
        var uncertain = Assert.Single(await new FlowPaymentAttemptRepository(factory).ListAsync("store", "invoice"));
        Assert.Equal(FlowAttemptState.Uncertain, uncertain.State);
        Assert.Null(uncertain.FlowId);

        time.Advance(TimeSpan.FromMinutes(16));
        var second = await service.CreateOrGetPaymentLinkAsync(
            "store", "invoice", 5m, time.Now.AddMinutes(20), TestData.Config());
        Assert.Equal("flow-second", second.FlowId);
        Assert.NotEqual(uncertain.Id, second.Id);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(2, (await new FlowPaymentAttemptRepository(factory).ListAsync("store", "invoice")).Count);
    }

    [Fact]
    public async Task DefiniteProviderFailureCanCreateNewAttemptWithoutOverwritingHistory()
    {
        var factory = TestDbContextFactory.Create();
        var client = new FakeFlowApiClient();
        client.Throws(new FlowApiException("invalid destination", uncertain: false, HttpStatusCode.UnprocessableEntity));
        client.Returns(new FlowPaymentLink("flow-second", "https://pay.dynamic.test/second", null));
        var service = CreateService(factory, client, new FakeTimeProvider(Now));

        await Assert.ThrowsAsync<FlowPaymentCreationException>(() =>
            service.CreateOrGetPaymentLinkAsync("store", "invoice", 5m, Now.AddMinutes(20), TestData.Config()));
        var second = await service.CreateOrGetPaymentLinkAsync(
            "store", "invoice", 5m, Now.AddMinutes(20), TestData.Config());

        Assert.Equal("flow-second", second.FlowId);
        var attempts = await new FlowPaymentAttemptRepository(factory).ListAsync("store", "invoice");
        Assert.Equal(2, attempts.Count);
        var failed = Assert.Single(attempts, a => a.State == FlowAttemptState.Failed);
        Assert.Equal("invalid destination", failed.Error);
        Assert.Single(attempts, a => a.State == FlowAttemptState.Created);
        Assert.NotEqual(attempts[0].CreationRequest, attempts[1].CreationRequest);
    }

    [Fact]
    public async Task LinkExpiryNeverExtendsBtcpayPromptExpiry()
    {
        var factory = TestDbContextFactory.Create();
        var client = new FakeFlowApiClient();
        client.Returns(new FlowPaymentLink("flow", "https://pay.dynamic.test/", null));
        var service = CreateService(factory, client, new FakeTimeProvider(Now));
        var config = TestData.Config();
        config.PaymentExpiryMinutes = 60;

        var attempt = await service.CreateOrGetPaymentLinkAsync(
            "store", "invoice", 5m, Now.AddMinutes(5), config);

        Assert.Equal(Now.AddMinutes(5), attempt.ExpiresAt);
        Assert.Equal(300, Assert.Single(client.Requests).ExpiresIn);
        var storedRequest = JsonDocument.Parse(attempt.CreationRequest);
        Assert.Equal(300, storedRequest.RootElement.GetProperty("expiresIn").GetInt32());
    }

    private static FlowPaymentService CreateService(
        TestDbContextFactory factory,
        FakeFlowApiClient client,
        FakeTimeProvider time) =>
        new(new FlowPaymentAttemptRepository(factory), client, time);
}
