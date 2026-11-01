using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Plugins.Fireblocks.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public interface IFlowWebhookService
{
    Task ProcessAsync(
        string storeId,
        ReadOnlyMemory<byte> body,
        string? signature,
        CancellationToken cancellationToken = default);
}

public sealed class FlowWebhookException(int statusCode) : Exception("The webhook request was rejected.")
{
    public int StatusCode { get; } = statusCode;
}

public static class FlowWebhookSignature
{
    public static bool IsValid(ReadOnlySpan<byte> body, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(secret) || signature is null)
            return false;
        var value = signature.AsSpan();
        if (value.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            value = value[7..];
        if (value.Length != 64)
            return false;
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

public sealed class FlowWebhookService(
    IFlowStoreConfigurationProvider configurations,
    IFlowPaymentAttemptRepository repository,
    IFlowReconciliationService reconciliation,
    TimeProvider timeProvider,
    ILogger<FlowWebhookService> logger) : IFlowWebhookService
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "flow.execution.updated",
        "flow.settlement.updated",
        "flow.risk.updated"
    };

    public async Task ProcessAsync(
        string storeId,
        ReadOnlyMemory<byte> body,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        var config = await configurations.GetAsync(storeId, cancellationToken);
        if (config is null || !FlowWebhookSignature.IsValid(body.Span, signature, config.WebhookSecret))
            throw new FlowWebhookException(StatusCodes.Status401Unauthorized);

        FlowWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FlowWebhookEnvelope>(body.Span);
        }
        catch (JsonException)
        {
            throw new FlowWebhookException(StatusCodes.Status400BadRequest);
        }
        var eventId = envelope?.EventId ?? envelope?.Id;
        var flowId = envelope?.Data?.FlowId ?? envelope?.Data?.Flow?.Id;
        if (envelope?.Type is not { } eventType || !SupportedEvents.Contains(eventType) ||
            eventId is not { Length: > 0 and <= 128 } ||
            flowId is not { Length: > 0 and <= 128 })
            throw new FlowWebhookException(StatusCodes.Status400BadRequest);

        // This save is deliberately before provider reconciliation and before the
        // acknowledgement. Duplicate IDs still trigger an authoritative read so a
        // previous transient reconciliation failure can recover.
        await repository.AddWebhookEventAsync(new FlowWebhookEvent
        {
            Id = eventId,
            StoreId = storeId,
            EventType = eventType,
            FlowId = flowId,
            ReceivedAt = timeProvider.GetUtcNow(),
            Payload = Encoding.UTF8.GetString(body.Span)
        }, cancellationToken);

        try
        {
            await reconciliation.ReconcileAsync(storeId, flowId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The durable event and outstanding-attempt poller provide recovery.
            // Never log the signed payload, signature, configuration, or exception
            // text because provider failures can contain sensitive material.
            logger.LogWarning(
                "Flow webhook was persisted but immediate reconciliation failed for store {StoreId} and flow {FlowId}",
                storeId, flowId);
        }
    }
}

public sealed class FlowWebhookEnvelope
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("eventId")]
    public string? EventId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("data")]
    public FlowWebhookData? Data { get; init; }
}

public sealed class FlowWebhookData
{
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }

    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("flow")]
    public FlowWebhookFlow? Flow { get; init; }
}

public sealed class FlowWebhookFlow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
