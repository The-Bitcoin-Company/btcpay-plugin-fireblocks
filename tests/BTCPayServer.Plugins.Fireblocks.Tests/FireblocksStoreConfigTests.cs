using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Payments;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FireblocksStoreConfigTests
{
    [Fact]
    public void ValidConfigurationUsesFixedUsdcSolanaSettlement()
    {
        var config = TestData.Config();

        Assert.Empty(config.Validate());
        Assert.Equal("SOL", FireblocksStoreConfig.SolanaChainName);
        Assert.Equal("101", FireblocksStoreConfig.SolanaChainId);
        Assert.Equal("USDC", FireblocksStoreConfig.UsdcSymbol);
        Assert.Equal(6, FireblocksStoreConfig.UsdcDecimals);
        Assert.True(FireblocksStoreConfig.IsValidSolanaAddress(FireblocksStoreConfig.UsdcMint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913")]
    [InlineData("EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1O")]
    public void InvalidSolanaDestinationIsRejected(string destination)
    {
        var config = TestData.Config();
        config.SolanaDestinationAddress = destination;

        Assert.Contains(config.Validate(), e => e.Field == nameof(config.SolanaDestinationAddress));
    }

    [Theory]
    [InlineData("http://example.com/api", "ApiEndpoint")]
    [InlineData("https://user:secret@example.com/api", "ApiEndpoint")]
    [InlineData("ftp://example.com/api", "ApiEndpoint")]
    [InlineData("http://example.com/pay", "HostedPaymentBaseUrl")]
    public void UnsafeEndpointsAreRejected(string endpoint, string field)
    {
        var config = TestData.Config();
        if (field == nameof(config.ApiEndpoint))
            config.ApiEndpoint = endpoint;
        else
            config.HostedPaymentBaseUrl = endpoint;

        Assert.Contains(config.Validate(), e => e.Field == field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(525601)]
    public void InvalidExpiryPolicyIsRejected(int minutes)
    {
        var config = TestData.Config();
        config.PaymentExpiryMinutes = minutes;

        Assert.Contains(config.Validate(), e => e.Field == nameof(config.PaymentExpiryMinutes));
    }

    [Fact]
    public void MissingCredentialAndEnvironmentAreRejected()
    {
        var config = TestData.Config();
        config.EnvironmentId = " ";
        config.ApiCredential = "";

        Assert.Contains(config.Validate(), e => e.Field == nameof(config.EnvironmentId));
        Assert.Contains(config.Validate(), e => e.Field == nameof(config.ApiCredential));
    }

    [Fact]
    public void BlankUpdatePreservesCredentialButResponsesAlwaysRedactIt()
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var previous = TestData.Config();
        var update = TestData.Config();
        update.ApiCredential = "";

        update.PreserveCredential(previous);
        Assert.Equal(TestData.Credential, update.ApiCredential);
        Assert.Empty(update.Validate());

        var storage = update.ToStorageToken(handler.Serializer);
        Assert.Equal(TestData.Credential, storage[nameof(update.ApiCredential)]?.Value<string>());
        var parsed = FireblocksStoreConfig.Parse(storage, handler.Serializer);
        var apiJson = JObject.FromObject(parsed, handler.Serializer.ForAPI()).ToString();
        Assert.DoesNotContain(TestData.Credential, apiJson);
        Assert.DoesNotContain("apiCredential", apiJson);
    }

    [Fact]
    public void LocalFakeEndpointsAreAllowedWithoutWeakeningProductionTls()
    {
        var config = TestData.Config();
        config.ApiEndpoint = "http://flow-fake:8080/api/v0";
        config.HostedPaymentBaseUrl = "http://localhost:14143/pay";

        Assert.Empty(config.Validate());
    }
}
