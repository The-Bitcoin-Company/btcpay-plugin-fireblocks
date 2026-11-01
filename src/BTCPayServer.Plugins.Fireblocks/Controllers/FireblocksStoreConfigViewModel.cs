using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Fireblocks.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Controllers;

public sealed class FireblocksStoreConfigViewModel
{
    public bool Enabled { get; set; } = true;

    [Display(Name = "Dynamic environment ID")]
    public string EnvironmentId { get; set; } = string.Empty;

    [Display(Name = "Flow API endpoint")]
    public string ApiEndpoint { get; set; } = FireblocksStoreConfig.DefaultApiEndpoint;

    [Display(Name = "Server API token")]
    public string ApiCredential { get; set; } = string.Empty;

    public bool HasApiCredential { get; set; }

    [Display(Name = "Dynamic webhook signing secret")]
    public string WebhookSecret { get; set; } = string.Empty;

    public bool HasWebhookSecret { get; set; }

    [Display(Name = "Acceptable settlement risk states (comma-separated)")]
    public string AcceptedRiskStates { get; set; } = "approved";

    [Display(Name = "Hosted payment base URL")]
    public string HostedPaymentBaseUrl { get; set; } = FireblocksStoreConfig.DefaultHostedPaymentBaseUrl;

    [Display(Name = "Solana USDC destination address")]
    public string SolanaDestinationAddress { get; set; } = string.Empty;

    [Display(Name = "Maximum payment lifetime (minutes)")]
    public int PaymentExpiryMinutes { get; set; } = 15;
}
