using BTCPayServer.Data;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Payments;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Fireblocks.Flow;

public interface IFlowStoreConfigurationProvider
{
    Task<FireblocksStoreConfig?> GetAsync(string storeId, CancellationToken cancellationToken = default);
}

public sealed class FlowStoreConfigurationProvider(
    StoreRepository storeRepository,
    FireblocksPaymentMethodHandler handler) : IFlowStoreConfigurationProvider
{
    public async Task<FireblocksStoreConfig?> GetAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        var store = await storeRepository.FindStore(storeId);
        var token = store?.GetPaymentMethodConfig(handler.PaymentMethodId);
        return token is null ? null : handler.ParsePaymentMethodConfig(token);
    }
}
