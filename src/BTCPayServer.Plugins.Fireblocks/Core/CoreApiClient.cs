using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Core;

public sealed class CoreApiClient(HttpClient httpClient)
{
    public async Task<string> TestConnection(CoreApiSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Validate(required: true).Count != 0)
            return "Check the core API environment, workspace API key and RSA private key.";
        try
        {
            const string path = "/v1/vault/accounts_paged?limit=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl + "/vault/accounts_paged?limit=1");
            request.Headers.Add("X-API-Key", settings.ApiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Sign(settings, path));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Never read or propagate provider diagnostics (which may echo credentials).
            return response.StatusCode switch
            {
                HttpStatusCode.OK => "Connection successful. Workspace vault read access verified; no funds moved.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Authentication or permission denied. Check the workspace environment, API key, matching RSA key, API user read permissions, IP allowlist and server clock.",
                HttpStatusCode.TooManyRequests => "Fireblocks rate limit reached. Retry later.",
                _ => "Fireblocks could not verify the connection. Check workspace availability and retry."
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Connection timed out. Check outbound HTTPS access and retry.";
        }
        catch (Exception e) when (e is HttpRequestException or CryptographicException or ArgumentException)
        {
            return "Connection failed. Check outbound HTTPS access, workspace API key and RSA private key.";
        }
    }

    public async Task<JsonElement> Send(CoreApiSettings settings, HttpMethod method, string path,
        object? body = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        if (settings.Validate(required: true).Count != 0)
            throw new CoreApiException("Save valid core workspace credentials first.");
        try
        {
            var bytes = body is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body);
            using var request = new HttpRequestMessage(method, settings.BaseUrl + path);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            request.Headers.Add("X-API-Key", settings.ApiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Sign(settings, "/v1" + path, bytes));
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new CoreApiException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Workspace permission denied. Check environment, API user permissions and IP allowlist; then refresh."
                    : "Fireblocks request failed. Check workspace availability and asset activation in the console; then refresh.", response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            return json.RootElement.Clone();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or CryptographicException or ArgumentException or JsonException)
        {
            throw new CoreApiException("Fireblocks request could not be confirmed. Check connectivity and workspace credentials; then refresh.");
        }
    }

    private static string Sign(CoreApiSettings settings, string path, byte[]? body = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            uri = path,
            nonce = Guid.NewGuid().ToString(),
            iat = now,
            exp = now + 25,
            sub = settings.ApiKey,
            bodyHash = Convert.ToHexString(SHA256.HashData(body ?? [])).ToLowerInvariant()
        }));
        var unsigned = header + "." + payload;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(settings.PrivateKey);
        return unsigned + "." + Encode(rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
