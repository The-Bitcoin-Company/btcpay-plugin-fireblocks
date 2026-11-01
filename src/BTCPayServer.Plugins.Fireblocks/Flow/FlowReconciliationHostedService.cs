using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

/// <summary>Runs immediately at startup and then recovers outstanding attempts every minute.</summary>
public sealed class FlowReconciliationHostedService(
    IFlowReconciliationService reconciliation,
    IFlowInvoiceRecoveryService invoiceRecovery,
    ILogger<FlowReconciliationHostedService> logger,
    TimeSpan? pollInterval = null) : BackgroundService
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var invoiceRepairCompleted = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!invoiceRepairCompleted)
            {
                try
                {
                    await invoiceRecovery.RepairRecordedSettlementsAsync(stoppingToken);
                    invoiceRepairCompleted = true;
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Invoice repair for recorded Flow settlements failed and will be retried; ordinary reconciliation will continue");
                }
            }
            try
            {
                await reconciliation.ReconcileOutstandingAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("The periodic Flow reconciliation pass failed; the next pass will retry");
            }
            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
