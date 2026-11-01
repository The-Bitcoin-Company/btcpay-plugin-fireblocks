using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Fireblocks.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public interface IFlowApiClient
{
    Task<FlowPaymentLink> CreatePaymentLinkAsync(
        FireblocksStoreConfig config,
        FlowPaymentLinkRequest request,
        CancellationToken cancellationToken = default);

    Task<FlowSnapshot> GetFlowAsync(
        FireblocksStoreConfig config,
        string flowId,
        CancellationToken cancellationToken = default);
}

public class FlowApiException : Exception
{
    public FlowApiException(string message, bool uncertain, HttpStatusCode? statusCode = null)
        : base(message)
    {
        Uncertain = uncertain;
        StatusCode = statusCode;
    }

    public bool Uncertain { get; }
    public HttpStatusCode? StatusCode { get; }
}

public sealed partial class FlowApiClient(
    HttpClient httpClient,
    TimeSpan? timeout = null,
    bool allowLocalEndpoints = false) : IFlowApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);
    private readonly bool _allowLocalEndpoints = allowLocalEndpoints;

    public async Task<FlowPaymentLink> CreatePaymentLinkAsync(
        FireblocksStoreConfig config,
        FlowPaymentLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        var uri = $"{config.ApiEndpoint.TrimEnd('/')}/server/{Uri.EscapeDataString(config.EnvironmentId)}/payment-links";
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiCredential);
        message.Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        string body;
        HttpStatusCode statusCode;
        try
        {
            using var response = await httpClient.SendAsync(message, timeoutSource.Token);
            statusCode = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlowApiException(
                $"The Flow provider did not respond within {_timeout.TotalSeconds:0.###} seconds. The creation outcome is uncertain; do not retry blindly.",
                uncertain: true);
        }
        catch (HttpRequestException exception)
        {
            throw new FlowApiException(
                Redact($"Could not reach the Flow provider: {exception.Message}", config.ApiCredential),
                uncertain: true);
        }

        if (statusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            var detail = ReadProviderError(body, config.ApiCredential);
            var uncertain = (int)statusCode >= 500;
            throw new FlowApiException(
                Redact($"Flow provider returned HTTP {(int)statusCode}{detail}.", config.ApiCredential),
                uncertain,
                statusCode);
        }

        if (!string.IsNullOrEmpty(config.ApiCredential) &&
            body.Contains(config.ApiCredential, StringComparison.Ordinal))
            throw new FlowApiException(
                "Flow provider response contained credential material. It was rejected and will not be persisted. The creation outcome is uncertain.",
                true);

        try
        {
            var result = JsonSerializer.Deserialize<FlowPaymentLinkResponse>(body, JsonOptions);
            if (ContainsCredential(result?.Flow?.Id, config.ApiCredential) ||
                ContainsCredential(result?.PaymentUrl, config.ApiCredential))
                throw new FlowApiException(
                    "Flow provider response contained credential material. It was rejected and will not be persisted. The creation outcome is uncertain.",
                    true);
            if (result?.Flow?.Id is not { Length: > 0 } flowId ||
                !Uri.TryCreate(result.PaymentUrl, UriKind.Absolute, out var paymentUrl) ||
                (paymentUrl.Scheme != Uri.UriSchemeHttps && paymentUrl.Scheme != Uri.UriSchemeHttp))
                throw new FlowApiException("Flow provider returned an incomplete payment-link response. The creation outcome is uncertain.", true);
            if (paymentUrl.Scheme == Uri.UriSchemeHttp &&
                (!_allowLocalEndpoints || (!paymentUrl.IsLoopback && !string.Equals(paymentUrl.Host, "flow-fake", StringComparison.OrdinalIgnoreCase))))
                throw new FlowApiException("Flow provider returned a non-HTTPS payment link outside local-fake mode. The creation outcome is uncertain.", true);
            if (result.Flow.Mode is not null && result.Flow.Mode != "payment")
                throw new FlowApiException("Flow provider returned a flow that is not in payment mode. The creation outcome is uncertain.", true);
            return new FlowPaymentLink(flowId, paymentUrl.AbsoluteUri, result.Flow.ExpiresAt);
        }
        catch (JsonException)
        {
            throw new FlowApiException("Flow provider returned invalid JSON. The creation outcome is uncertain.", true);
        }
    }

    public async Task<FlowSnapshot> GetFlowAsync(
        FireblocksStoreConfig config,
        string flowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        var uri = $"{config.ApiEndpoint.TrimEnd('/')}/server/{Uri.EscapeDataString(config.EnvironmentId)}/flows/{Uri.EscapeDataString(flowId)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiCredential);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        string body;
        HttpStatusCode statusCode;
        try
        {
            using var response = await httpClient.SendAsync(message, timeoutSource.Token);
            statusCode = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FlowApiException("The Flow provider did not respond to the reconciliation request in time.", uncertain: true);
        }
        catch (HttpRequestException exception)
        {
            throw new FlowApiException(
                Redact($"Could not reach the Flow provider: {exception.Message}", config.ApiCredential),
                uncertain: true);
        }

        if (statusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            throw new FlowApiException(
                RedactSecrets($"Flow provider returned HTTP {(int)statusCode}{ReadProviderError(body, config.ApiCredential, config.WebhookSecret)}.",
                    config.ApiCredential, config.WebhookSecret),
                uncertain: true,
                statusCode);
        if (!string.IsNullOrEmpty(config.ApiCredential) &&
            body.Contains(config.ApiCredential, StringComparison.Ordinal))
            throw new FlowApiException("Flow provider response contained credential material and was rejected.", true);

        try
        {
            using var json = JsonDocument.Parse(body);
            // Inspect each decoded string directly. Re-serialization could escape a
            // credential again (for example '<' becoming '\\u003C').
            if (ContainsCredential(json.RootElement, config.ApiCredential) ||
                ContainsCredential(json.RootElement, config.WebhookSecret))
                throw new FlowApiException("Flow provider response contained credential material and was rejected.", true);
            var snapshot = json.RootElement.Deserialize<FlowStateResponse>(JsonOptions)?.Flow;
            if (snapshot?.Id is not { Length: > 0 })
                throw new FlowApiException("Flow provider returned an incomplete reconciliation response.", true);
            return snapshot;
        }
        catch (JsonException)
        {
            throw new FlowApiException("Flow provider returned invalid reconciliation JSON.", true);
        }
    }

    private static string ReadProviderError(string body, params string?[] credentials)
    {
        try
        {
            // Parse the complete bounded response before truncating anything. JSON
            // unescaping can otherwise recover a credential from an invalid excerpt.
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return " (unrecognized provider error response)";
            var code = json.RootElement.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : null;
            var error = json.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : null;
            code = RedactSecrets(code, credentials);
            error = RedactSecrets(error, credentials);
            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(error))
                return $" ({string.Join(": ", new[] { code, error }.Where(v => !string.IsNullOrWhiteSpace(v)))})";
        }
        catch (JsonException)
        {
            // Never echo malformed/non-JSON responses: no truncation or encoding
            // strategy can make arbitrary provider bytes safe for logs.
        }

        return " (unrecognized provider error response)";
    }

    private static bool ContainsCredential(JsonElement element, string credential)
    {
        if (string.IsNullOrEmpty(credential))
            return false;
        return element.ValueKind switch
        {
            JsonValueKind.String => ContainsCredential(element.GetString(), credential),
            JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsCredential(item, credential)),
            JsonValueKind.Object => element.EnumerateObject().Any(property =>
                ContainsCredential(property.Name, credential) || ContainsCredential(property.Value, credential)),
            _ => false
        };
    }

    public static string RedactSecrets(string? value, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        // Match every secret against the original detail before any replacement.
        // If one secret prefixes another, replacement order must never expose a suffix.
        if (secrets.Any(secret => ContainsCredential(value, secret ?? string.Empty)))
            return "[redacted]";
        var redacted = RedactCore(value, string.Empty, truncate: false);
        return redacted.Length <= 512 ? redacted : redacted[..512];
    }

    private static bool ContainsCredential(string? value, string credential)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(credential))
            return false;
        if (value.Contains(credential, StringComparison.Ordinal))
            return true;
        try
        {
            // Handle partially or fully percent-encoded credentials, including a
            // second encoding layer, before any value can reach persistence/UI.
            var decoded = Uri.UnescapeDataString(Uri.UnescapeDataString(value));
            return decoded.Contains(credential, StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    public static string Redact(string? value, string credential) =>
        RedactCore(value, credential, truncate: true);

    private static string RedactCore(string? value, string credential, bool truncate)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var redacted = value;
        if (!string.IsNullOrEmpty(credential))
            redacted = redacted.Replace(credential, "[redacted]", StringComparison.Ordinal);
        redacted = BearerCredentialRegex().Replace(redacted, "$1[redacted]");
        // Partially encoded secrets do not have a safe literal replacement. If any
        // remain after ordinary redaction, suppress the complete detail instead.
        if (ContainsCredential(redacted, credential))
            return "[redacted]";
        return truncate && redacted.Length > 512 ? redacted[..512] : redacted;
    }

    [GeneratedRegex(@"(?i)(Bearer\s+)[^\s,;]+")]
    private static partial Regex BearerCredentialRegex();
}
