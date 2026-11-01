using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

/// <summary>Runs immediately at startup and then recovers outstanding attempts every minute.</summary>
public sealed class FlowReconciliationHostedService(
    IFlowReconciliationService reconciliation,
    ILogger<FlowReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
