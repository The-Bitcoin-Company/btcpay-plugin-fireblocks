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
            // Redact before taking any excerpt so truncation cannot leave a
            // recognizable credential prefix behind.
            var detail = ReadProviderError(Redact(body, config.ApiCredential));
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

    private static string ReadProviderError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return string.Empty;
            var code = json.RootElement.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : null;
            var error = json.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(error))
                return $" ({string.Join(": ", new[] { code, error }.Where(v => !string.IsNullOrWhiteSpace(v)))})";
        }
        catch (JsonException)
        {
            // A short, redacted excerpt is more useful than a secondary parse error.
        }

        var excerpt = body.Length <= 256 ? body : body[..256];
        return string.IsNullOrWhiteSpace(excerpt) ? string.Empty : $" ({excerpt})";
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

    public static string Redact(string? value, string credential)
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
        return redacted.Length <= 512 ? redacted : redacted[..512];
    }

    [GeneratedRegex(@"(?i)(Bearer\s+)[^\s,;]+")]
    private static partial Regex BearerCredentialRegex();
}
