namespace BTCPayServer.Plugins.Fireblocks.Data;

public enum FlowAttemptState
{
    Creating = 0,
    Created = 1,
    Uncertain = 2,
    Failed = 3
}

public sealed class FlowPaymentAttempt
{
    public required string Id { get; set; }
    public required string StoreId { get; set; }
    public required string InvoiceId { get; set; }
    public required int AttemptNumber { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ProviderExpiresAt { get; set; }
    public required string ApiEndpoint { get; set; }
    public required string EnvironmentId { get; set; }
    public required decimal RequestedUsd { get; set; }
    public required string CreationRequest { get; set; }
    public required FlowAttemptState State { get; set; }
    public string? FlowId { get; set; }
    public string? PaymentUrl { get; set; }
    public string? Error { get; set; }
}
