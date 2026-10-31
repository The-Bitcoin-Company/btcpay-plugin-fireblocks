using System.Globalization;
using System.Numerics;
using BTCPayServer.Payments;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Fireblocks.Configuration;

public sealed record FireblocksConfigurationError(string Field, string Message);

public sealed class FireblocksStoreConfig
{
    public const string DefaultApiEndpoint = "https://app.dynamic.xyz/api/v0";
    public const string DefaultHostedPaymentBaseUrl = "https://pay.dynamicauth.com";
    public const string SolanaChainName = "SOL";
    public const string SolanaChainId = "101";
    public const string UsdcSymbol = "USDC";
    public const string UsdcMint = "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v";
    public const int UsdcDecimals = 6;

    public string EnvironmentId { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = DefaultApiEndpoint;

    // Greenfield serializes this same object for includeConfig responses. Newtonsoft's
    // ShouldSerialize hook keeps the credential out of responses while still allowing it
    // to be read from the store's server-side configuration token.
    public string ApiCredential { get; set; } = string.Empty;
    public bool ShouldSerializeApiCredential() => false;

    public string HostedPaymentBaseUrl { get; set; } = DefaultHostedPaymentBaseUrl;
    public string SolanaDestinationAddress { get; set; } = string.Empty;
    public int PaymentExpiryMinutes { get; set; } = 15;

    public static FireblocksStoreConfig Parse(JToken token, JsonSerializer serializer) =>
        token.ToObject<FireblocksStoreConfig>(serializer)
        ?? throw new FormatException("Invalid Fireblocks configuration");

    public JToken ToStorageToken(JsonSerializer serializer)
    {
        var token = JObject.FromObject(this, serializer);
        token[nameof(ApiCredential)] = ApiCredential;
        return token;
    }

    public void PreserveCredential(FireblocksStoreConfig? previous)
    {
        if (string.IsNullOrWhiteSpace(ApiCredential) && previous is not null)
            ApiCredential = previous.ApiCredential;
    }

    public IReadOnlyList<FireblocksConfigurationError> Validate()
    {
        var errors = new List<FireblocksConfigurationError>();
        EnvironmentId = EnvironmentId.Trim();
        ApiEndpoint = ApiEndpoint.Trim();
        HostedPaymentBaseUrl = HostedPaymentBaseUrl.Trim();
        SolanaDestinationAddress = SolanaDestinationAddress.Trim();

        if (EnvironmentId.Length is 0 or > 200 || EnvironmentId.Any(char.IsWhiteSpace))
            errors.Add(new(nameof(EnvironmentId), "Enter a valid Dynamic environment ID."));
        if (string.IsNullOrWhiteSpace(ApiCredential))
            errors.Add(new(nameof(ApiCredential), "Enter a server API token with the flow.write scope."));
        ValidateEndpoint(ApiEndpoint, nameof(ApiEndpoint), "API endpoint", errors);
        ValidateEndpoint(HostedPaymentBaseUrl, nameof(HostedPaymentBaseUrl), "Hosted payment base URL", errors);
        if (!IsValidSolanaAddress(SolanaDestinationAddress))
            errors.Add(new(nameof(SolanaDestinationAddress), "Enter a valid base58 Solana address (32 bytes)."));
        if (PaymentExpiryMinutes is < 1 or > 525_600)
            errors.Add(new(nameof(PaymentExpiryMinutes), "Payment expiry must be between 1 minute and 365 days."));
        return errors;
    }

    private static void ValidateEndpoint(
        string value,
        string field,
        string displayName,
        ICollection<FireblocksConfigurationError> errors)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add(new(field, $"{displayName} must be an absolute HTTP(S) URL without credentials, query, or fragment."));
            return;
        }

        // Plain HTTP is intended only for deterministic local fakes. Real provider
        // credentials must always use TLS.
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback &&
            !string.Equals(uri.Host, "flow-fake", StringComparison.OrdinalIgnoreCase))
            errors.Add(new(field, $"{displayName} must use HTTPS except for a local test provider."));
    }

    public static bool IsValidSolanaAddress(string value)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        if (value.Length is < 32 or > 44)
            return false;

        var number = BigInteger.Zero;
        foreach (var character in value)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0)
                return false;
            number = (number * 58) + digit;
        }

        var bytes = number.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeroes = value.TakeWhile(c => c == '1').Count();
        return bytes.Length + leadingZeroes == 32;
    }

    public static string FormatUsd(decimal amount)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Fireblocks payment links require a positive USD amount with at most two decimals.", nameof(amount));
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
