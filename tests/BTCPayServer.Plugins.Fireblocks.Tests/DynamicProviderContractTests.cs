using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class DynamicProviderContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static string Fixture(string name) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dynamic", name + ".json"));

    [Fact]
    public async Task ServerReadMapsAtomicDeliveryNotUsdOrSourceTransaction()
    {
        using var http = new HttpClient(new Handler(Fixture("flow-read")));
        var snapshot = await new FlowApiClient(http).GetFlowAsync(TestData.Config(), "550e8400-e29b-41d4-a716-446655440000");

        Assert.Equal("env_test", snapshot.EnvironmentId);
        Assert.Equal("source_confirmed", snapshot.Execution?.Status);
        Assert.Equal("completed", snapshot.Settlement?.Status);
        Assert.Equal("cleared", snapshot.Risk?.Status);
        Assert.Equal("25", snapshot.Settlement?.Amount);
        Assert.Equal("25000000", snapshot.Settlement?.AmountAtomic);
        Assert.Equal("sanitized-settlement-signature", snapshot.Settlement?.TransactionId);
        Assert.Equal("101", snapshot.Settlement?.ChainId);
        Assert.Equal("SOL", snapshot.Settlement?.ChainName);
        Assert.Equal("USDC", snapshot.Settlement?.Symbol);
        Assert.Equal(6, snapshot.Settlement?.TokenDecimals);
        Assert.Equal("11111111111111111111111111111111", snapshot.Settlement?.Destination);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1e6")]
    [InlineData("99999999999999999999999999999")]
    public void InvalidOrMissingAtomicDeliveryCannotBecomeAnAmount(string? amount)
    {
        var response = new DynamicFlowResponse { Settlement = new DynamicSettlementData { Amount = amount } };
        Assert.Null(response.ToSnapshot("env").Settlement?.Amount);
    }

    [Fact]
    public void OneAtomicUnitIsNotOneToken()
    {
        var response = new DynamicFlowResponse { Settlement = new DynamicSettlementData { Amount = "1" } };
        Assert.Equal("0.000001", response.ToSnapshot("env").Settlement?.Amount);
    }

    [Fact]
    public void HostedLinkUsesCreateEnvelope()
    {
        var response = JsonSerializer.Deserialize<FlowPaymentLinkResponse>(Fixture("hosted-link"), Options);
        Assert.Equal("payment", response?.Flow?.Mode);
        Assert.StartsWith("https://pay.dynamicauth.com?flow=", response?.PaymentUrl);
    }

    [Theory]
    [InlineData("execution", "flow.execution.updated")]
    [InlineData("settlement", "flow.settlement.updated")]
    [InlineData("risk", "flow.risk.updated")]
    public void EventsUseEventNameAndEventIdNotDeliveryMessageId(string fixture, string expected)
    {
        var response = JsonSerializer.Deserialize<FlowWebhookEnvelope>(Fixture(fixture), Options);
        Assert.Equal(expected, response?.Type);
        Assert.NotNull(response?.EventId);
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", response?.EnvironmentId);
        Assert.Equal("7d2f1c8e-4b6a-4f0e-9a3d-5c1e2b8f6a90", response?.Data?.FlowId);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
