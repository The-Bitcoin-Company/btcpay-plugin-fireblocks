using System.Collections.Concurrent;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public sealed class FlowPaymentCreationException(string message, bool uncertain) : Exception(message)
{
    public bool Uncertain { get; } = uncertain;
}

public interface IFlowPaymentService
{
    Task<FlowPaymentAttempt> CreateOrGetPaymentLinkAsync(
        string storeId,
        string invoiceId,
        decimal requestedUsd,
        DateTimeOffset invoiceExpiresAt,
        FireblocksStoreConfig config,
        CancellationToken cancellationToken = default);
}

public sealed class FlowPaymentService(
    IFlowPaymentAttemptRepository repository,
    IFlowApiClient client,
    TimeProvider timeProvider) : IFlowPaymentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _invoiceLocks = new(StringComparer.Ordinal);

    public async Task<FlowPaymentAttempt> CreateOrGetPaymentLinkAsync(
        string storeId,
        string invoiceId,
        decimal requestedUsd,
        DateTimeOffset invoiceExpiresAt,
        FireblocksStoreConfig config,
        CancellationToken cancellationToken = default)
    {
        var invoiceLock = _invoiceLocks.GetOrAdd($"{storeId}\n{invoiceId}", _ => new SemaphoreSlim(1, 1));
        await invoiceLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var previous = await repository.GetLatestAsync(storeId, invoiceId, cancellationToken);
            if (previous is { State: FlowAttemptState.Created })
                return previous;
            if (previous is { State: FlowAttemptState.Creating or FlowAttemptState.Uncertain })
                throw new FlowPaymentCreationException(
                    "A previous Flow creation has an uncertain outcome. It remains recorded and will not be retried without explicit reconciliation.",
                    uncertain: true);

            var expiresAt = invoiceExpiresAt <= now.AddMinutes(config.PaymentExpiryMinutes)
                ? invoiceExpiresAt
                : now.AddMinutes(config.PaymentExpiryMinutes);
            var expiresIn = (int)Math.Ceiling((expiresAt - now).TotalSeconds);
            if (expiresIn <= 0)
                throw new FlowPaymentCreationException("The BTCPay payment prompt has already expired.", uncertain: false);

            var attempt = new FlowPaymentAttempt
            {
                Id = Guid.NewGuid().ToString("N"),
                StoreId = storeId,
                InvoiceId = invoiceId,
                AttemptNumber = (previous?.AttemptNumber ?? 0) + 1,
                CreatedAt = now,
                ExpiresAt = expiresAt,
                ApiEndpoint = config.ApiEndpoint,
                EnvironmentId = config.EnvironmentId,
                RequestedUsd = requestedUsd,
                State = FlowAttemptState.Creating,
                CreationRequest = string.Empty
            };
            var request = FlowPaymentRequestFactory.Create(
                config, invoiceId, attempt.Id, requestedUsd, expiresIn);
            attempt.CreationRequest = JsonSerializer.Serialize(request, JsonOptions);

            // Persist the immutable request before any network I/O. A crash after this
            // point leaves a Creating record, which blocks an unsafe automatic retry.
            // The database's per-invoice attempt-number uniqueness closes races between
            // independent BTCPay processes, not just within this service instance.
            try
            {
                await repository.AddAsync(attempt, cancellationToken);
            }
            catch (DbUpdateException)
            {
                var concurrent = await repository.GetLatestAsync(storeId, invoiceId, CancellationToken.None);
                if (concurrent?.AttemptNumber != attempt.AttemptNumber)
                    throw;
                if (concurrent.State == FlowAttemptState.Created)
                    return concurrent;
                if (concurrent.State is FlowAttemptState.Creating or FlowAttemptState.Uncertain)
                    throw new FlowPaymentCreationException(
                        "A concurrent Flow creation has an uncertain outcome and will not be duplicated.",
                        uncertain: true);
                throw new FlowPaymentCreationException(
                    "A concurrent Flow creation already failed. Reactivate the payment prompt before retrying.",
                    uncertain: false);
            }
            try
            {
                var link = await client.CreatePaymentLinkAsync(config, request, cancellationToken);
                attempt.State = FlowAttemptState.Created;
                attempt.FlowId = link.FlowId;
                attempt.PaymentUrl = link.PaymentUrl;
                attempt.ProviderExpiresAt = link.ProviderExpiresAt;
                attempt.Error = null;
                await repository.UpdateAsync(attempt, cancellationToken);
                return attempt;
            }
            catch (FlowApiException exception)
            {
                attempt.State = exception.Uncertain ? FlowAttemptState.Uncertain : FlowAttemptState.Failed;
                attempt.Error = exception.Message;
                await repository.UpdateAsync(attempt, CancellationToken.None);
                throw new FlowPaymentCreationException(exception.Message, exception.Uncertain);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                attempt.State = FlowAttemptState.Uncertain;
                attempt.Error = "Flow creation failed unexpectedly. The creation outcome is uncertain; do not retry blindly.";
                await repository.UpdateAsync(attempt, CancellationToken.None);
                throw new FlowPaymentCreationException(attempt.Error, uncertain: true);
            }
        }
        finally
        {
            invoiceLock.Release();
        }
    }

}
