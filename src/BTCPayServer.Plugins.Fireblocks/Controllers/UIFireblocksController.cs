using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Fireblocks.Controllers;

[Area(Plugin.Area)]
[Route("stores/{storeId}/fireblocks")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public sealed class UIFireblocksController(
    StoreRepository storeRepository,
    FireblocksPaymentMethodHandler handler) : Controller
{
    private StoreData CurrentStore => HttpContext.GetStoreData();

    [HttpGet]
    public IActionResult Configure()
    {
        var stored = CurrentStore.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = stored is null ? new FireblocksStoreConfig() : handler.ParsePaymentMethodConfig(stored);
        return View(ToViewModel(config, CurrentStore));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Configure(FireblocksStoreConfigViewModel model)
    {
        var stored = CurrentStore.GetPaymentMethodConfig(handler.PaymentMethodId);
        var previous = stored is null ? null : handler.ParsePaymentMethodConfig(stored);
        var config = new FireblocksStoreConfig
        {
            EnvironmentId = model.EnvironmentId,
            ApiEndpoint = model.ApiEndpoint,
            ApiCredential = model.ApiCredential,
            HostedPaymentBaseUrl = model.HostedPaymentBaseUrl,
            SolanaDestinationAddress = model.SolanaDestinationAddress,
            PaymentExpiryMinutes = model.PaymentExpiryMinutes
        };
        config.PreserveCredential(previous);
        foreach (var error in config.Validate())
            ModelState.AddModelError(error.Field, error.Message);

        if (!ModelState.IsValid)
        {
            model.ApiCredential = string.Empty;
            model.HasApiCredential = !string.IsNullOrEmpty(config.ApiCredential);
            return View(model);
        }

        CurrentStore.SetPaymentMethodConfig(handler.PaymentMethodId, config.ToStorageToken(handler.Serializer));
        var storeBlob = CurrentStore.GetStoreBlob();
        storeBlob.SetExcluded(handler.PaymentMethodId, !model.Enabled);
        CurrentStore.SetStoreBlob(storeBlob);
        await storeRepository.UpdateStore(CurrentStore);
        TempData[WellKnownTempData.SuccessMessage] = "Fireblocks Flow settings saved.";
        return RedirectToAction(nameof(Configure), new { storeId = CurrentStore.Id });
    }

    private static FireblocksStoreConfigViewModel ToViewModel(FireblocksStoreConfig config, StoreData store) => new()
    {
        Enabled = store.GetPaymentMethodConfig(FireblocksPaymentMethodHandler.PaymentMethod, onlyEnabled: true) is not null,
        EnvironmentId = config.EnvironmentId,
        ApiEndpoint = config.ApiEndpoint,
        ApiCredential = string.Empty,
        HasApiCredential = !string.IsNullOrEmpty(config.ApiCredential),
        HostedPaymentBaseUrl = config.HostedPaymentBaseUrl,
        SolanaDestinationAddress = config.SolanaDestinationAddress,
        PaymentExpiryMinutes = config.PaymentExpiryMinutes
    };
}
