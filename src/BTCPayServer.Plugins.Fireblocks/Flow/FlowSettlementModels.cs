using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public sealed class FlowStateResponse
{
    [JsonPropertyName("flow")]
    public FlowSnapshot? Flow { get; init; }
}

/// <summary>Authoritative server-side Flow state used for every notification and poll.</summary>
public sealed class FlowSnapshot
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("memo")]
    public FlowMemo? Memo { get; init; }

    [JsonPropertyName("settlementConfig")]
    public FlowSettlementConfig? SettlementConfig { get; init; }

    [JsonPropertyName("destinationConfig")]
    public FlowDestinationConfig? DestinationConfig { get; init; }

    [JsonPropertyName("execution")]
    public FlowExecutionStatus? Execution { get; init; }

    [JsonPropertyName("settlement")]
    public FlowSettlementStatus? Settlement { get; init; }

    [JsonPropertyName("risk")]
    public FlowRiskStatus? Risk { get; init; }
}

public sealed class FlowExecutionStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class FlowSettlementStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    // Decimal token units encoded as a JSON string, never a binary floating point number.
    [JsonPropertyName("amount")]
    public string? Amount { get; init; }

    // Optional provider base-unit evidence. When present it must exactly agree with Amount.
    [JsonPropertyName("amountAtomic")]
    public string? AmountAtomic { get; init; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; init; }

    [JsonPropertyName("destination")]
    public string? Destination { get; init; }

    [JsonPropertyName("chainName")]
    public string? ChainName { get; init; }

    [JsonPropertyName("chainId")]
    public string? ChainId { get; init; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("tokenAddress")]
    public string? TokenAddress { get; init; }

    [JsonPropertyName("tokenDecimals")]
    public int? TokenDecimals { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class FlowRiskStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record FlowSettlementEvidence(
    string TransactionId,
    string Amount,
    string AmountAtomic,
    decimal TokenAmount,
    DateTimeOffset ReceivedAt,
    string Destination,
    DateTimeOffset? CompletedAt);
