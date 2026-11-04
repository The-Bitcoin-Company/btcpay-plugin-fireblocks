using System.Globalization;
using System.Numerics;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

/// <summary>Dynamic v0 server read contract, distinct from our accounting model.</summary>
public sealed class DynamicFlowResponse
{
    public string? Id { get; init; }
    public string? Mode { get; init; }
    public string? ExecutionState { get; init; }
    public string? SettlementState { get; init; }
    public string? RiskState { get; init; }
    public string? SettlementTxHash { get; init; }
    public string? ToChainName { get; init; }
    public FlowMemo? Memo { get; init; }
    public FlowSettlementConfig? SettlementConfig { get; init; }
    public FlowDestinationConfig? DestinationConfig { get; init; }
    public DynamicSettlementData? Settlement { get; init; }

    // Environment is bound by the authenticated server URL, not a response field.
    public FlowSnapshot ToSnapshot(string environmentId) => new()
    {
        Id = Id,
        EnvironmentId = environmentId,
        Mode = Mode,
        Status = ExecutionState,
        Memo = Memo,
        SettlementConfig = SettlementConfig,
        DestinationConfig = DestinationConfig,
        Execution = new FlowExecutionStatus { Status = ExecutionState },
        Risk = new FlowRiskStatus { Status = RiskState },
        Settlement = new FlowSettlementStatus
        {
            Status = SettlementState,
            AmountAtomic = Settlement?.Amount,
            Amount = TokenAmount(Settlement?.Amount),
            TransactionId = SettlementTxHash,
            Destination = Settlement?.ToAddress,
            ChainName = ToChainName,
            ChainId = Settlement?.ToChainId,
            TokenAddress = Settlement?.ToToken,
            Symbol = Settlement?.ToTokenInfo?.Symbol,
            TokenDecimals = Settlement?.ToTokenInfo?.Decimals,
            CompletedAt = Settlement?.CompletedAt
        }
    };

    private static string? TokenAmount(string? atomic)
    {
        // Strict unsigned integer input; never round, use USD value, or infer from a quote.
        if (atomic is not { Length: > 0 and <= 28 } ||
            !BigInteger.TryParse(atomic, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return null;
        return ((decimal)value / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
    }
}

public sealed class DynamicSettlementData
{
    public string? Amount { get; init; }
    public string? ToChainId { get; init; }
    public string? ToToken { get; init; }
    public string? ToAddress { get; init; }
    public DynamicTokenInfo? ToTokenInfo { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class DynamicTokenInfo
{
    public string? Symbol { get; init; }
    public int? Decimals { get; init; }
}
