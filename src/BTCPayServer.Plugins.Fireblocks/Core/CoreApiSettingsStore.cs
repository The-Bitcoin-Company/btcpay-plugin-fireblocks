using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Services.Stores;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Fireblocks.Core;

public interface ICoreApiSettingsStore
{
    Task<CoreApiSettings> Get(string storeId);
    Task Save(string storeId, CoreApiSettings settings);
}

// Independent of the Dynamic payment-method token: disabling or deleting checkout
// configuration must not delete credentials used by future vault/sweep features.
public sealed class CoreApiSettingsStore(StoreRepository stores) : ICoreApiSettingsStore
{
    public const string SettingName = "Fireblocks.CoreApi";

    public async Task<CoreApiSettings> Get(string storeId)
    {
        var token = await stores.GetSettingAsync<JObject>(storeId, SettingName);
        return token?.ToObject<CoreApiSettings>() ?? new CoreApiSettings();
    }

    public Task Save(string storeId, CoreApiSettings settings) =>
        stores.UpdateSetting(storeId, SettingName, settings.ToStorageToken(JsonSerializer.CreateDefault()));
}
