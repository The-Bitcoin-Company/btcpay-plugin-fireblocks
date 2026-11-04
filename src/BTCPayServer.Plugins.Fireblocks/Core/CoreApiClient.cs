using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Fireblocks.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Core;

// Intentionally read-only: no vault or transaction creation endpoints.
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

    private static string Sign(CoreApiSettings settings, string path)
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
            bodyHash = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant()
        }));
        var unsigned = header + "." + payload;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(settings.PrivateKey);
        return unsigned + "." + Encode(rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
