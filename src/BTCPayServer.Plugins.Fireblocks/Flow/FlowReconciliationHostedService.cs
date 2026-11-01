using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

/// <summary>Runs immediately at startup and then recovers outstanding attempts every minute.</summary>
public sealed class FlowReconciliationHostedService(
    IFlowReconciliationService reconciliation,
    IFlowInvoiceRecoveryService invoiceRecovery,
    ILogger<FlowReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await invoiceRecovery.RepairRecordedSettlementsAsync(stoppingToken);
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Startup invoice repair for recorded Flow settlements failed; ordinary reconciliation will continue");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
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
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
