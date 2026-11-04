using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Controllers;
using BTCPayServer.Plugins.Fireblocks.Core;
using BTCPayServer.Plugins.Fireblocks.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class CoreApiTests
{
    private static CoreApiSettings Credentials(string environment = CoreApiSettings.Sandbox)
    {
        using var rsa = RSA.Create(2048);
        return new() { Environment = environment, ApiKey = "workspace-api-key", PrivateKey = rsa.ExportPkcs8PrivateKeyPem() };
    }

    [Theory]
    [InlineData(CoreApiSettings.Sandbox, "sandbox-api.fireblocks.io")]
    [InlineData(CoreApiSettings.Us, "api.fireblocks.io")]
    public async Task SignsReadOnlyRequestForFixedWorkspace(string environment, string host)
    {
        var settings = Credentials(environment);
        var nonces = new HashSet<string>();
        using var handler = new FakeHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal(host, request.RequestUri.Host);
            Assert.Equal("/v1/vault/accounts_paged?limit=1", request.RequestUri.PathAndQuery);
            Assert.Equal(settings.ApiKey, request.Headers.GetValues("X-API-Key").Single());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var jwt = request.Headers.Authorization.Parameter!.Split('.');
            using var header = JsonDocument.Parse(Decode(jwt[0]));
            Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
            using var payload = JsonDocument.Parse(Decode(jwt[1]));
            var claims = payload.RootElement;
            Assert.Equal(request.RequestUri.PathAndQuery, claims.GetProperty("uri").GetString());
            Assert.Equal(settings.ApiKey, claims.GetProperty("sub").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), claims.GetProperty("bodyHash").GetString());
            Assert.True(nonces.Add(claims.GetProperty("nonce").GetString()!));
            var iat = claims.GetProperty("iat").GetInt64();
            Assert.InRange(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - iat, 0, 5);
            Assert.InRange(claims.GetProperty("exp").GetInt64() - iat, 1, 29);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(settings.PrivateKey);
            Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(jwt[0] + "." + jwt[1]), Decode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return new(HttpStatusCode.OK) { Content = new StringContent(settings.PrivateKey) };
        });
        using var http = new HttpClient(handler);
        var client = new CoreApiClient(http);
        Assert.StartsWith("Connection successful.", await client.TestConnection(settings));
        Assert.StartsWith("Connection successful.", await client.TestConnection(settings));
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Authentication or permission denied.")]
    [InlineData(HttpStatusCode.Forbidden, "Authentication or permission denied.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Fireblocks rate limit reached.")]
    [InlineData(HttpStatusCode.InternalServerError, "Fireblocks could not verify")]
    [InlineData(HttpStatusCode.Redirect, "Fireblocks could not verify")]
    public async Task ProviderFailuresNeverExposeDiagnostics(HttpStatusCode status, string expected)
    {
        var settings = Credentials();
        using var handler = new FakeHandler(_ => new(status) { Content = new StringContent(settings.PrivateKey + settings.ApiKey) });
        using var http = new HttpClient(handler);
        var result = await new CoreApiClient(http).TestConnection(settings);
        Assert.StartsWith(expected, result);
        Assert.DoesNotContain(settings.PrivateKey, result);
        Assert.DoesNotContain(settings.ApiKey, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailuresAndTimeoutsAreSafe(bool timeout)
    {
        var settings = Credentials();
        using var handler = new FakeHandler(_ => throw (timeout
            ? (Exception)new TaskCanceledException(settings.PrivateKey)
            : new HttpRequestException(settings.PrivateKey)));
        using var http = new HttpClient(handler);
        var result = await new CoreApiClient(http).TestConnection(settings);
        Assert.StartsWith(timeout ? "Connection timed out." : "Connection failed.", result);
        Assert.DoesNotContain(settings.PrivateKey, result);
    }

    [Fact]
    public async Task InvalidEnvironmentOrKeyNeverSendsRequest()
    {
        using var handler = new FakeHandler(_ => throw new Exception("Must not send"));
        using var http = new HttpClient(handler);
        var settings = Credentials();
        settings.Environment = "https://attacker.invalid";
        Assert.Throws<InvalidOperationException>(() => settings.BaseUrl);
        Assert.StartsWith("Check the core API", await new CoreApiClient(http).TestConnection(settings));
        settings.Environment = CoreApiSettings.Sandbox;
        settings.PrivateKey = "sensitive-invalid-private-key";
        Assert.StartsWith("Check the core API", await new CoreApiClient(http).TestConnection(settings));
        using var rsa = RSA.Create(2048);
        settings.PrivateKey = rsa.ExportSubjectPublicKeyInfoPem();
        Assert.NotEmpty(settings.Validate(true));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void StoragePreservesIndependentSecretsButGreenfieldRedactsThem()
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var previous = TestData.Config();
        previous.CoreApi = Credentials(CoreApiSettings.Us);
        var update = TestData.Config();
        update.CoreApi = new() { Environment = CoreApiSettings.Us, ApiKey = " ", PrivateKey = "\n" };
        update.PreserveCredential(previous);
        Assert.Equal(previous.CoreApi.ApiKey, update.CoreApi.ApiKey);
        Assert.Equal(previous.CoreApi.PrivateKey, update.CoreApi.PrivateKey);
        Assert.Equal(TestData.Credential, update.ApiCredential);
        var parsed = FireblocksStoreConfig.Parse(update.ToStorageToken(handler.Serializer), handler.Serializer);
        Assert.Equal(previous.CoreApi.PrivateKey, parsed.CoreApi.PrivateKey);
        Assert.Equal(CoreApiSettings.Us, parsed.CoreApi.Environment);
        var json = JObject.FromObject(parsed, handler.Serializer.ForAPI()).ToString();
        Assert.DoesNotContain(previous.CoreApi.PrivateKey, json);
        Assert.DoesNotContain(previous.CoreApi.ApiKey, json);
        Assert.DoesNotContain("privateKey", json);
        Assert.DoesNotContain("apiKey", json);
        Assert.DoesNotContain(TestData.Credential, json);
        Assert.Empty(new FireblocksStoreConfig().CoreApi.Validate());
    }

    [Fact]
    public async Task UiGetAndValidationFailureNeverReturnSecretsOrAttemptedValues()
    {
        var paymentHandler = new FireblocksPaymentMethodHandler(null!);
        var config = TestData.Config();
        config.CoreApi = Credentials();
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(paymentHandler.PaymentMethodId, config.ToStorageToken(paymentHandler.Serializer));
        var context = new DefaultHttpContext();
        context.SetStoreData(store);
        using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Must not send")));
        var controller = new UIFireblocksController(null!, paymentHandler, new ConfigurationBuilder().Build(), new CoreApiClient(http))
        { ControllerContext = new ControllerContext { HttpContext = context } };
        var get = Assert.IsType<FireblocksStoreConfigViewModel>(Assert.IsType<ViewResult>(controller.Configure()).Model);
        Assert.Empty(get.CoreApi.PrivateKey);
        Assert.Empty(get.CoreApi.ApiKey);
        Assert.True(get.HasCorePrivateKey);
        controller.ModelState.SetModelValue("CoreApi.PrivateKey", config.CoreApi.PrivateKey, config.CoreApi.PrivateKey);
        var post = new FireblocksStoreConfigViewModel { CoreApi = new() { ApiKey = config.CoreApi.ApiKey, PrivateKey = "bad-key" } };
        var result = Assert.IsType<ViewResult>(await controller.ConfigureCore(post, "test"));
        var model = Assert.IsType<FireblocksStoreConfigViewModel>(result.Model);
        Assert.Empty(model.CoreApi.PrivateKey);
        Assert.Empty(model.CoreApi.ApiKey);
        Assert.All(controller.ModelState.Values, value => Assert.Null(value.AttemptedValue));
        Assert.Equal(config.CoreApi.PrivateKey, paymentHandler.ParsePaymentMethodConfig(store.GetPaymentMethodConfig(paymentHandler.PaymentMethodId)!).CoreApi.PrivateKey);
    }

    [Fact]
    public async Task DynamicOnlyGreenfieldUpdateKeepsCoreWorkspaceAndCredentials()
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var previous = TestData.Config();
        previous.CoreApi = Credentials(CoreApiSettings.Us);
        var update = (JObject)TestData.Config().ToStorageToken(handler.Serializer);
        update.Remove(nameof(FireblocksStoreConfig.CoreApi));
        update.Remove("coreApi");
        var context = new BTCPayServer.Payments.PaymentMethodConfigValidationContext(null!,
            new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary(), update,
            new System.Security.Claims.ClaimsPrincipal(), previous.ToStorageToken(handler.Serializer), new StoreData());
        await handler.ValidatePaymentMethodConfig(context);
        Assert.True(context.ModelState.IsValid);
        var saved = handler.ParsePaymentMethodConfig(context.Config);
        Assert.Equal(CoreApiSettings.Us, saved.CoreApi.Environment);
        Assert.Equal(previous.CoreApi.PrivateKey, saved.CoreApi.PrivateKey);
        Assert.Equal(previous.CoreApi.ApiKey, saved.CoreApi.ApiKey);
    }

    [Fact]
    public async Task TestActionUsesSavedSecretsWithoutSavingOrEnablingFlow()
    {
        var paymentHandler = new FireblocksPaymentMethodHandler(null!);
        var config = new FireblocksStoreConfig { CoreApi = Credentials() };
        var store = new StoreData { Id = "store" };
        var stored = config.ToStorageToken(paymentHandler.Serializer);
        store.SetPaymentMethodConfig(paymentHandler.PaymentMethodId, stored);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(paymentHandler.PaymentMethodId, true);
        store.SetStoreBlob(blob);
        var context = new DefaultHttpContext();
        context.SetStoreData(store);
        using var fake = new FakeHandler(request =>
        {
            Assert.Equal("api.fireblocks.io", request.RequestUri!.Host);
            return new(HttpStatusCode.OK);
        });
        using var http = new HttpClient(fake);
        var controller = new UIFireblocksController(null!, paymentHandler, new ConfigurationBuilder().Build(), new CoreApiClient(http))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new EmptyTempDataProvider())
        };
        var result = await controller.ConfigureCore(new() { CoreApi = new() { Environment = CoreApiSettings.Us } }, "test");
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(1, fake.Calls);
        Assert.True(JToken.DeepEquals(stored, store.GetPaymentMethodConfig(paymentHandler.PaymentMethodId)));
        Assert.Null(store.GetPaymentMethodConfig(paymentHandler.PaymentMethodId, onlyEnabled: true));
    }

    private sealed class EmptyTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(send(request));
        }
    }
}
