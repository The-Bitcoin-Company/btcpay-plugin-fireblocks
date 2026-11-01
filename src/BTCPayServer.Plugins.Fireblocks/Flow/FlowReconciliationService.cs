using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Payments;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public sealed class FlowReconciliationService(
    IFlowPaymentAttemptRepository repository,
    IFlowStoreConfigurationProvider configurations,
    IFlowApiClient client,
    IFlowPaymentRecorder paymentRecorder,
    TimeProvider timeProvider,
    ILogger<FlowReconciliationService> logger) : IFlowReconciliationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _attemptLocks = new(StringComparer.Ordinal);

    public async Task ReconcileAsync(string storeId, string flowId, CancellationToken cancellationToken = default)
    {
        var attempt = await repository.GetByFlowIdAsync(storeId, flowId, cancellationToken);
        if (attempt is null || attempt.State != FlowAttemptState.Created)
            return;
        var attemptLock = _attemptLocks.GetOrAdd(attempt.Id, _ => new SemaphoreSlim(1, 1));
        await attemptLock.WaitAsync(cancellationToken);
        try
        {
            // Re-read after taking the local lock; another notification may have
            // completed this attempt while this caller was waiting.
            attempt = await repository.GetByFlowIdAsync(storeId, flowId, cancellationToken);
            if (attempt is null || attempt.State != FlowAttemptState.Created)
                return;
            await ReconcileAttemptAsync(attempt, cancellationToken);
        }
        finally
        {
            attemptLock.Release();
        }
    }

    public async Task ReconcileOutstandingAsync(CancellationToken cancellationToken = default)
    {
        var attempts = await repository.ListOutstandingAsync(500, cancellationToken);
        foreach (var attempt in attempts)
        {
            try
            {
                await ReconcileAsync(attempt.StoreId, attempt.FlowId!, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Do not expose provider response/exception text in application logs.
                // The attempt's redacted diagnostic is available in the plugin schema.
                logger.LogWarning(
                    "Flow reconciliation failed for store {StoreId}, invoice {InvoiceId}, and flow {FlowId}; it will be retried",
                    attempt.StoreId, attempt.InvoiceId, attempt.FlowId);
            }
        }
    }

    private async Task ReconcileAttemptAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken)
    {
        var config = await configurations.GetAsync(attempt.StoreId, cancellationToken);
        if (config is null || string.IsNullOrWhiteSpace(config.ApiCredential))
        {
            await SaveDiagnosticAsync(attempt, "Flow store configuration is unavailable; settlement remains unaccounted.", cancellationToken);
            return;
        }

        // Credentials come from current server-side store configuration, but the
        // endpoint/environment always come from the immutable attempt binding.
        var apiConfig = new FireblocksStoreConfig
        {
            ApiEndpoint = attempt.ApiEndpoint,
            EnvironmentId = attempt.EnvironmentId,
            ApiCredential = config.ApiCredential
        };
        FlowSnapshot snapshot;
        try
        {
            snapshot = await client.GetFlowAsync(apiConfig, attempt.FlowId!, cancellationToken);
        }
        catch (FlowApiException exception)
        {
            await SaveDiagnosticAsync(attempt, exception.Message, cancellationToken);
            return;
        }

        attempt.LastReconciledAt = timeProvider.GetUtcNow();
        attempt.ProviderStatus = SafeStatus(snapshot.Status, config);
        attempt.RiskStatus = SafeStatus(snapshot.Risk?.Status, config);
        attempt.Error = null;

        var bindingError = ValidateBinding(attempt, snapshot);
        if (bindingError is not null)
        {
            attempt.Error = bindingError;
            await repository.UpdateAsync(attempt, cancellationToken);
            return;
        }
        if (string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(snapshot.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            attempt.State = FlowAttemptState.Cancelled;
            attempt.Error = "Flow is cancelled; no settlement is accounted.";
            await repository.UpdateAsync(attempt, cancellationToken);
            return;
        }
        if (string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            attempt.State = FlowAttemptState.Failed;
            attempt.Error = "Flow failed; no settlement is accounted.";
            await repository.UpdateAsync(attempt, cancellationToken);
            return;
        }

        var (evidence, error) = ValidateSettlement(attempt, snapshot, config);
        if (error is not null || evidence is null)
        {
            attempt.Error = error;
            await repository.UpdateAsync(attempt, cancellationToken);
            return;
        }

        // Reserve one transaction and amount for this Flow before calling BTCPay.
        // A crash after AddPayment is safe: the same evidence may be retried, while
        // changed evidence or a second transaction can never be credited to this attempt.
        attempt.SettlementTransactionId = evidence.TransactionId;
        attempt.SettlementAmountAtomic = evidence.AmountAtomic;
        attempt.SettlementAmount = evidence.TokenAmount;
        attempt.SettlementCompletedAt = evidence.CompletedAt;
        if (!await repository.ReserveSettlementAsync(attempt, cancellationToken))
            return;

        var result = await paymentRecorder.RecordAsync(attempt, evidence, cancellationToken);
        if (result is FlowPaymentRecordResult.Recorded or FlowPaymentRecordResult.AlreadyRecorded)
        {
            attempt.State = FlowAttemptState.Settled;
            attempt.Error = null;
        }
        else
        {
            attempt.Error = "BTCPay rejected or could not bind the settlement payment; no invoice credit was confirmed.";
        }
        await repository.UpdateAsync(attempt, CancellationToken.None);
    }

    private async Task SaveDiagnosticAsync(
        FlowPaymentAttempt attempt,
        string error,
        CancellationToken cancellationToken)
    {
        attempt.LastReconciledAt = timeProvider.GetUtcNow();
        attempt.Error = FlowApiClient.Redact(error, string.Empty);
        await repository.UpdateAsync(attempt, cancellationToken);
    }

    private static string? ValidateBinding(FlowPaymentAttempt attempt, FlowSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Id, attempt.FlowId, StringComparison.Ordinal))
            return "Authoritative Flow ID does not match the stored attempt; settlement remains unaccounted.";
        if (!string.Equals(snapshot.EnvironmentId, attempt.EnvironmentId, StringComparison.Ordinal))
            return "Flow environment does not match the stored attempt; settlement remains unaccounted.";
        if (!string.Equals(snapshot.Mode, "payment", StringComparison.OrdinalIgnoreCase))
            return "Authoritative Flow is not in payment mode; settlement remains unaccounted.";
        if (snapshot.Memo is null ||
            !string.Equals(snapshot.Memo.InvoiceId, attempt.InvoiceId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Memo.PaymentAttemptId, attempt.Id, StringComparison.Ordinal))
            return "Flow invoice/attempt correlation does not match the stored binding; settlement remains unaccounted.";
        return null;
    }

    private static (FlowSettlementEvidence? Evidence, string? Error) ValidateSettlement(
        FlowPaymentAttempt attempt,
        FlowSnapshot snapshot,
        FireblocksStoreConfig config)
    {
        if (!TryReadCreationRequest(attempt, out var request) ||
            !SettlementConfigEquals(request.SettlementConfig, snapshot.SettlementConfig) ||
            !DestinationConfigEquals(request.DestinationConfig, snapshot.DestinationConfig))
            return (null, "Flow settlement or destination configuration differs from the immutable creation request.");
        if (!string.Equals(snapshot.Settlement?.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return (null, null); // Execution progress alone is never merchant settlement.
        if (snapshot.Risk?.Status is not { Length: > 0 } risk || !config.GetAcceptedRiskStates().Contains(risk))
            return (null, "Flow risk is missing, held, under review, or not accepted by store policy.");

        var settlement = snapshot.Settlement!;
        var expectedSettlement = request.SettlementConfig.Settlements[0];
        var expectedDestination = request.DestinationConfig.Destinations[0];
        if (!string.Equals(settlement.Destination, expectedDestination.Identifier, StringComparison.Ordinal) ||
            !string.Equals(settlement.ChainName, FireblocksStoreConfig.SolanaChainName, StringComparison.Ordinal) ||
            !string.Equals(settlement.ChainId, FireblocksStoreConfig.SolanaChainId, StringComparison.Ordinal) ||
            !string.Equals(settlement.Symbol, FireblocksStoreConfig.UsdcSymbol, StringComparison.Ordinal) ||
            !string.Equals(settlement.TokenAddress, FireblocksStoreConfig.UsdcMint, StringComparison.Ordinal) ||
            settlement.TokenDecimals != FireblocksStoreConfig.UsdcDecimals ||
            expectedSettlement.TokenAddress != FireblocksStoreConfig.UsdcMint ||
            expectedSettlement.ChainName != FireblocksStoreConfig.SolanaChainName ||
            expectedSettlement.ChainId != FireblocksStoreConfig.SolanaChainId ||
            expectedSettlement.Symbol != FireblocksStoreConfig.UsdcSymbol ||
            expectedSettlement.TokenDecimals != FireblocksStoreConfig.UsdcDecimals)
            return (null, "Settlement asset, network, mint, precision, or destination does not match USDC/Solana policy.");
        if (settlement.TransactionId is not { Length: > 0 and <= 128 } transactionId ||
            transactionId.Any(char.IsControl))
            return (null, "Completed settlement has no usable transaction evidence.");
        if (!TryParseAmount(settlement.Amount, settlement.AmountAtomic, out var tokenAmount, out var amountAtomic))
            return (null, "Completed settlement amount is missing, inconsistent, non-positive, or has more than six decimals.");

        return (new FlowSettlementEvidence(
            transactionId,
            settlement.Amount!,
            amountAtomic,
            tokenAmount,
            settlement.CompletedAt ?? attempt.LastReconciledAt ?? DateTimeOffset.UtcNow,
            settlement.Destination!,
            settlement.CompletedAt), null);
    }

    private static bool TryReadCreationRequest(FlowPaymentAttempt attempt, out FlowPaymentLinkRequest request)
    {
        try
        {
            request = JsonSerializer.Deserialize<FlowPaymentLinkRequest>(attempt.CreationRequest, JsonOptions)!;
            return request?.SettlementConfig?.Settlements is { Length: 1 } &&
                request?.DestinationConfig?.Destinations is { Length: 1 };
        }
        catch (JsonException)
        {
            request = null!;
            return false;
        }
    }

    private static bool SettlementConfigEquals(FlowSettlementConfig? expected, FlowSettlementConfig? actual) =>
        expected?.Strategy == actual?.Strategy &&
        expected?.Settlements is [{ } left] && actual?.Settlements is [{ } right] &&
        left.ChainName == right.ChainName && left.ChainId == right.ChainId &&
        left.Symbol == right.Symbol && left.TokenAddress == right.TokenAddress &&
        left.TokenDecimals == right.TokenDecimals;

    private static bool DestinationConfigEquals(FlowDestinationConfig? expected, FlowDestinationConfig? actual) =>
        expected?.Destinations is [{ } left] && actual?.Destinations is [{ } right] &&
        left.ChainName == right.ChainName && left.Type == right.Type && left.Identifier == right.Identifier;

    private static bool TryParseAmount(
        string? rawAmount,
        string? rawAtomic,
        out decimal tokenAmount,
        out string atomic)
    {
        tokenAmount = 0m;
        atomic = string.Empty;
        if (rawAmount is not { Length: > 0 and <= 64 } ||
            !decimal.TryParse(rawAmount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out tokenAmount) || tokenAmount <= 0m)
            return false;
        var scale = (decimal.GetBits(tokenAmount)[3] & 0x00FF0000) >> 16;
        if (scale > FireblocksStoreConfig.UsdcDecimals)
            return false;
        try
        {
            var atomicDecimal = tokenAmount * 1_000_000m;
            if (decimal.Truncate(atomicDecimal) != atomicDecimal)
                return false;
            atomic = atomicDecimal.ToString("0", CultureInfo.InvariantCulture);
            if (rawAtomic is not null &&
                (!BigInteger.TryParse(rawAtomic, NumberStyles.None, CultureInfo.InvariantCulture, out var suppliedAtomic) ||
                 suppliedAtomic != BigInteger.Parse(atomic, CultureInfo.InvariantCulture)))
                return false;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string? SafeStatus(string? status, FireblocksStoreConfig config)
    {
        if (status is null)
            return null;
        var safe = FlowApiClient.Redact(FlowApiClient.Redact(status, config.ApiCredential), config.WebhookSecret);
        return safe.Length <= 64 ? safe : safe[..64];
    }
}
