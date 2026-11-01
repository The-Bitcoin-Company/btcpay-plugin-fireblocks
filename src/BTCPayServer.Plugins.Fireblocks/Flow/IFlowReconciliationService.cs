namespace BTCPayServer.Plugins.Fireblocks.Flow;

public interface IFlowReconciliationService
{
    Task ReconcileAsync(string storeId, string flowId, CancellationToken cancellationToken = default);
    Task ReconcileOutstandingAsync(CancellationToken cancellationToken = default);
}
