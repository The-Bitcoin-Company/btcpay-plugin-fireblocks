using BTCPayServer.Plugins.Fireblocks.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public static class FlowPaymentRequestFactory
{
    public static FlowPaymentLinkRequest Create(
        FireblocksStoreConfig config,
        string invoiceId,
        string paymentAttemptId,
        decimal requestedUsd,
        int expiresInSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentAttemptId);
        if (expiresInSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiresInSeconds));

        return new FlowPaymentLinkRequest
        {
            Amount = FireblocksStoreConfig.FormatUsd(requestedUsd),
            Currency = "USD",
            SettlementConfig = new FlowSettlementConfig
            {
                Strategy = "preferred_order",
                Settlements =
                [
                    new FlowSettlement
                    {
                        ChainName = FireblocksStoreConfig.SolanaChainName,
                        ChainId = FireblocksStoreConfig.SolanaChainId,
                        Symbol = FireblocksStoreConfig.UsdcSymbol,
                        TokenAddress = FireblocksStoreConfig.UsdcMint,
                        TokenDecimals = FireblocksStoreConfig.UsdcDecimals
                    }
                ]
            },
            DestinationConfig = new FlowDestinationConfig
            {
                Destinations =
                [
                    new FlowDestination
                    {
                        ChainName = FireblocksStoreConfig.SolanaChainName,
                        Type = "address",
                        Identifier = config.SolanaDestinationAddress
                    }
                ]
            },
            Memo = new FlowMemo
            {
                InvoiceId = invoiceId,
                PaymentAttemptId = paymentAttemptId
            },
            ExpiresIn = expiresInSeconds,
            BaseUrl = config.HostedPaymentBaseUrl
        };
    }
}
