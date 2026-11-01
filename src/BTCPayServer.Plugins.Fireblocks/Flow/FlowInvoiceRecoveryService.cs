using BTCPayServer.Events;
using BTCPayServer.Plugins.Fireblocks.Data;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public interface IFlowInvoiceRecoveryService
{
    Task RepairRecordedSettlementsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Replays only BTCPay's idempotent invoice-evaluation request. A crash can occur
/// after an attempt is settled but after the original in-memory notification was lost.
/// </summary>
public sealed class FlowInvoiceRecoveryService(
    IFlowPaymentAttemptRepository repository,
    EventAggregator eventAggregator) : IFlowInvoiceRecoveryService
{
    public async Task RepairRecordedSettlementsAsync(CancellationToken cancellationToken = default)
    {
        var invoiceIds = await repository.ListSettledInvoiceIdsAsync(cancellationToken);
        foreach (var invoiceId in invoiceIds)
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
    }
}
