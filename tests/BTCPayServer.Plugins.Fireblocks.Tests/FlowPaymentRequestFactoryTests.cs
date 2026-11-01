using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowPaymentRequestFactoryTests
{
    [Fact]
    public void ConstructsHostedPaymentModeRequestWithOnlyUsdcSolanaSettlement()
    {
        var config = TestData.Config();

        var request = FlowPaymentRequestFactory.Create(config, "invoice-123", "attempt-456", 25m, 900);

        Assert.Equal("25.00", request.Amount);
        Assert.Equal("USD", request.Currency);
        Assert.Equal(900, request.ExpiresIn);
        Assert.Equal(config.HostedPaymentBaseUrl, request.BaseUrl);
        var settlement = Assert.Single(request.SettlementConfig.Settlements);
        Assert.Equal("preferred_order", request.SettlementConfig.Strategy);
        Assert.Equal("SOL", settlement.ChainName);
        Assert.Equal("101", settlement.ChainId);
        Assert.Equal("USDC", settlement.Symbol);
        Assert.Equal(FireblocksStoreConfig.UsdcMint, settlement.TokenAddress);
        Assert.Equal(6, settlement.TokenDecimals);
        var destination = Assert.Single(request.DestinationConfig.Destinations);
        Assert.Equal("SOL", destination.ChainName);
        Assert.Equal("address", destination.Type);
        Assert.Equal(config.SolanaDestinationAddress, destination.Identifier);
        Assert.Equal("invoice-123", request.Memo.InvoiceId);
        Assert.Equal("attempt-456", request.Memo.PaymentAttemptId);

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(TestData.Credential, json);
        Assert.DoesNotContain("SETTLEMENT_TOKEN", json);
        Assert.DoesNotContain("settledAmount", json);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.001)]
    public void RejectsAmountThatCannotRepresentUsdPayment(decimal amount)
    {
        Assert.Throws<ArgumentException>(() =>
            FlowPaymentRequestFactory.Create(TestData.Config(), "invoice", "attempt", amount, 60));
    }
}
