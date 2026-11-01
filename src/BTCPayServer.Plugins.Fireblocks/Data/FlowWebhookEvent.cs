namespace BTCPayServer.Plugins.Fireblocks.Data;

/// <summary>
/// The authenticated provider notification is retained before reconciliation is
/// attempted. The Dynamic event ID is globally unique and is the deduplication key.
/// </summary>
public sealed class FlowWebhookEvent
{
    public required string Id { get; set; }
    public required string StoreId { get; set; }
    public required string EventType { get; set; }
    public required string FlowId { get; set; }
    public required DateTimeOffset ReceivedAt { get; set; }
    public required string Payload { get; set; }
}
