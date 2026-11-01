using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public sealed class FlowPaymentLinkRequest
{
    [JsonPropertyName("amount")]
    public required string Amount { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    [JsonPropertyName("settlementConfig")]
    public required FlowSettlementConfig SettlementConfig { get; init; }

    [JsonPropertyName("destinationConfig")]
    public required FlowDestinationConfig DestinationConfig { get; init; }

    [JsonPropertyName("memo")]
    public required FlowMemo Memo { get; init; }

    [JsonPropertyName("expiresIn")]
    public required int ExpiresIn { get; init; }

    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }
}

public sealed class FlowSettlementConfig
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = "preferred_order";

    [JsonPropertyName("settlements")]
    public required FlowSettlement[] Settlements { get; init; }
}

public sealed class FlowSettlement
{
    [JsonPropertyName("chainName")]
    public required string ChainName { get; init; }

    [JsonPropertyName("chainId")]
    public required string ChainId { get; init; }

    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("tokenAddress")]
    public required string TokenAddress { get; init; }

    [JsonPropertyName("tokenDecimals")]
    public required int TokenDecimals { get; init; }
}

public sealed class FlowDestinationConfig
{
    [JsonPropertyName("destinations")]
    public required FlowDestination[] Destinations { get; init; }
}

public sealed class FlowDestination
{
    [JsonPropertyName("chainName")]
    public required string ChainName { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "address";

    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }
}

public sealed class FlowMemo
{
    [JsonPropertyName("invoiceId")]
    public required string InvoiceId { get; init; }

    [JsonPropertyName("paymentAttemptId")]
    public required string PaymentAttemptId { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "btcpay-fireblocks";
}

public sealed class FlowPaymentLinkResponse
{
    [JsonPropertyName("flow")]
    public FlowResponse? Flow { get; init; }

    [JsonPropertyName("paymentUrl")]
    public string? PaymentUrl { get; init; }
}

public sealed class FlowResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record FlowPaymentLink(string FlowId, string PaymentUrl, DateTimeOffset? ProviderExpiresAt);
