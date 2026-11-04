using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Fireblocks.Configuration;

public sealed class CoreApiSettings
{
    public const string Sandbox = "Sandbox";
    public const string Us = "US";
    public string Environment { get; set; } = Sandbox;
    public string ApiKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public bool ShouldSerializeApiKey() => false;
    public bool ShouldSerializePrivateKey() => false;

    [JsonIgnore]
    public string BaseUrl => Environment switch
    {
        Sandbox => "https://sandbox-api.fireblocks.io/v1",
        Us => "https://api.fireblocks.io/v1",
        _ => throw new InvalidOperationException("Select Sandbox or US Mainnet/Testnet.")
    };

    public void PreserveSecrets(CoreApiSettings? previous)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) ApiKey = previous?.ApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(PrivateKey)) PrivateKey = previous?.PrivateKey ?? string.Empty;
    }

    public IReadOnlyList<FireblocksConfigurationError> Validate(bool required = false)
    {
        var errors = new List<FireblocksConfigurationError>();
        if (Environment is not Sandbox and not Us)
            errors.Add(new("CoreApi.Environment", "Select Sandbox or US Mainnet/Testnet."));
        if (!required && string.IsNullOrEmpty(ApiKey) && string.IsNullOrEmpty(PrivateKey)) return errors;
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length > 200 || ApiKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            errors.Add(new("CoreApi.ApiKey", "Enter the workspace API key."));
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PrivateKey ?? string.Empty);
            // A public-only PEM must not be accepted as a signing credential.
            rsa.ExportParameters(true);
            if (rsa.KeySize < 2048) throw new CryptographicException();
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            errors.Add(new("CoreApi.PrivateKey", "Enter an unencrypted RSA private key in PEM format (at least 2048 bits)."));
        }
        return errors;
    }

    public JObject ToStorageToken(JsonSerializer serializer)
    {
        var token = JObject.FromObject(this, serializer);
        token[nameof(ApiKey)] = ApiKey;
        token[nameof(PrivateKey)] = PrivateKey;
        return token;
    }
}
