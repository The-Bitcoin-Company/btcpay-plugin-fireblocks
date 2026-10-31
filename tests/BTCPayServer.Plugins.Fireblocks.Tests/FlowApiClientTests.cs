using System.Diagnostics;
using System.Net;
using System.Text;
using BTCPayServer.Plugins.Fireblocks.Flow;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FlowApiClientTests
{
    [Fact]
    public async Task SendsCredentialOnlyAsBearerAndParsesHostedLink()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = Json("""{"flow":{"id":"flow-123","mode":"payment"},"paymentUrl":"https://pay.dynamic.test/?flow=abc"}""")
        });
        using var httpClient = new HttpClient(handler);
        var client = new FlowApiClient(httpClient);

        var link = await client.CreatePaymentLinkAsync(TestData.Config(), Request());

        Assert.Equal("flow-123", link.FlowId);
        Assert.Equal("https://pay.dynamic.test/?flow=abc", link.PaymentUrl);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://app.dynamic.test/api/v0/server/env_test/payment-links", request.Uri);
        Assert.Equal("Bearer", request.CredentialScheme);
        Assert.Equal(TestData.Credential, request.Credential);
        Assert.Contains("\"currency\":\"USD\"", request.Body);
    }

    [Fact]
    public async Task ProviderErrorIsClearAndCredentialIsRedacted()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json($$"""{"code":"invalid_request","error":"bad token {{TestData.Credential}} Bearer {{TestData.Credential}}"}""")
        });
        using var httpClient = new HttpClient(handler);
        var client = new FlowApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<FlowApiException>(() =>
            client.CreatePaymentLinkAsync(TestData.Config(), Request()));

        Assert.False(exception.Uncertain);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid_request", exception.Message);
        Assert.Contains("[redacted]", exception.Message);
        Assert.DoesNotContain(TestData.Credential, exception.Message);
    }

    [Fact]
    public async Task ServerFailureIsUncertain()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = Json("""{"error":"temporarily unavailable"}""")
        });
        using var httpClient = new HttpClient(handler);
        var client = new FlowApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<FlowApiException>(() =>
            client.CreatePaymentLinkAsync(TestData.Config(), Request()));

        Assert.True(exception.Uncertain);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task TimeoutIsBoundedAndMarkedUncertain()
    {
        var handler = new StubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });
        using var httpClient = new HttpClient(handler);
        var client = new FlowApiClient(httpClient, TimeSpan.FromMilliseconds(30));
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<FlowApiException>(() =>
            client.CreatePaymentLinkAsync(TestData.Config(), Request()));

        stopwatch.Stop();
        Assert.True(exception.Uncertain);
        Assert.Contains("did not respond", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task MalformedSuccessDoesNotInventABinding()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = Json("""{"flow":{"id":"flow-123"}}""")
        });
        using var httpClient = new HttpClient(handler);
        var client = new FlowApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<FlowApiException>(() =>
            client.CreatePaymentLinkAsync(TestData.Config(), Request()));

        Assert.True(exception.Uncertain);
        Assert.Contains("incomplete", exception.Message);
    }

    [Fact]
    public void RedactionCoversCredentialAndAuthorizationText()
    {
        var redacted = FlowApiClient.Redact(
            $"token={TestData.Credential}; Authorization: Bearer another-secret", TestData.Credential);

        Assert.Equal("token=[redacted]; Authorization: Bearer [redacted]", redacted);
    }

    private static FlowPaymentLinkRequest Request() =>
        FlowPaymentRequestFactory.Create(TestData.Config(), "invoice", "attempt", 5m, 300);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string? CredentialScheme,
        string? Credential,
        string Body);

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request)))
        {
        }

        public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));
            return await _response(request, cancellationToken);
        }
    }
}
