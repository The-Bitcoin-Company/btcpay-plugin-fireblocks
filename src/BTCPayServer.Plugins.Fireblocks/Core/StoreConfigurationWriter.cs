using BTCPayServer.Data;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Fireblocks.Core;

public interface IStoreConfigurationWriter
{
    Task Update(StoreData store);
}

public sealed class StoreConfigurationWriter(StoreRepository stores) : IStoreConfigurationWriter
{
    public Task Update(StoreData store) => stores.UpdateStore(store);
}
