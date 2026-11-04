using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Core;
using BTCPayServer.Plugins.Fireblocks.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Controllers;

[Area(Plugin.Area)]
[Route("stores/{storeId}/fireblocks")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public sealed class UIFireblocksController(
    StoreRepository storeRepository,
    FireblocksPaymentMethodHandler handler,
    IConfiguration configuration,
    CoreApiClient coreApiClient,
    ICoreApiSettingsStore coreSettings) : Controller
{
    private StoreData CurrentStore => HttpContext.GetStoreData();

    [HttpGet]
    public async Task<IActionResult> Configure()
    {
        var stored = CurrentStore.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = stored is null ? new FireblocksStoreConfig() : handler.ParsePaymentMethodConfig(stored);
        return View(ToViewModel(config, CurrentStore, await coreSettings.Get(CurrentStore.Id)));
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
            WebhookSecret = model.WebhookSecret,
            AcceptedRiskStates = model.AcceptedRiskStates,
            HostedPaymentBaseUrl = model.HostedPaymentBaseUrl,
            SolanaDestinationAddress = model.SolanaDestinationAddress,
            PaymentExpiryMinutes = model.PaymentExpiryMinutes
        };
        config.PreserveCredential(previous);
        foreach (var error in config.Validate(FlowEndpointSecurity.AllowLocalEndpoints(configuration)))
            ModelState.AddModelError(error.Field, error.Message);

        if (!ModelState.IsValid)
        {
            ModelState.Remove("CoreApi.ApiKey");
            ModelState.Remove("CoreApi.PrivateKey");
            ModelState.Remove(nameof(model.ApiCredential));
            ModelState.Remove(nameof(model.WebhookSecret));
            var core = await coreSettings.Get(CurrentStore.Id);
            model.CoreApi = new CoreApiSettings { Environment = core.Environment };
            model.HasCoreApiKey = !string.IsNullOrEmpty(core.ApiKey);
            model.HasCorePrivateKey = !string.IsNullOrEmpty(core.PrivateKey);
            model.ApiCredential = string.Empty;
            model.HasApiCredential = !string.IsNullOrEmpty(config.ApiCredential);
            model.WebhookSecret = string.Empty;
            model.HasWebhookSecret = !string.IsNullOrEmpty(config.WebhookSecret);
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

    [HttpPost("core")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureCore(FireblocksStoreConfigViewModel model, string command)
    {
        var stored = CurrentStore.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = stored is null ? new FireblocksStoreConfig() : handler.ParsePaymentMethodConfig(stored);
        var previous = await coreSettings.Get(CurrentStore.Id);
        model.CoreApi.PreserveSecrets(previous);
        var errors = model.CoreApi.Validate(required: true);
        // Never return posted credentials or model-binding attempted values to the browser.
        ModelState.Clear();
        if (errors.Count != 0)
        {
            foreach (var error in errors) ModelState.AddModelError(error.Field, error.Message);
            var safe = ToViewModel(config, CurrentStore, previous);
            safe.CoreApi.Environment = model.CoreApi.Environment;
            return View("Configure", safe);
        }
        if (command == "test")
        {
            var message = await coreApiClient.TestConnection(model.CoreApi, HttpContext.RequestAborted);
            TempData[message.StartsWith("Connection successful.", StringComparison.Ordinal)
                ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = message;
        }
        else
        {
            await coreSettings.Save(CurrentStore.Id, model.CoreApi);
            TempData[WellKnownTempData.SuccessMessage] = "Core Fireblocks API settings saved.";
        }
        return RedirectToAction(nameof(Configure), new { storeId = CurrentStore.Id });
    }

    private static FireblocksStoreConfigViewModel ToViewModel(FireblocksStoreConfig config, StoreData store, CoreApiSettings core) => new()
    {
        CoreApi = new CoreApiSettings { Environment = core.Environment },
        HasCoreApiKey = !string.IsNullOrEmpty(core.ApiKey),
        HasCorePrivateKey = !string.IsNullOrEmpty(core.PrivateKey),
        Enabled = store.GetPaymentMethodConfig(FireblocksPaymentMethodHandler.PaymentMethod, onlyEnabled: true) is not null,
        EnvironmentId = config.EnvironmentId,
        ApiEndpoint = config.ApiEndpoint,
        ApiCredential = string.Empty,
        HasApiCredential = !string.IsNullOrEmpty(config.ApiCredential),
        WebhookSecret = string.Empty,
        HasWebhookSecret = !string.IsNullOrEmpty(config.WebhookSecret),
        AcceptedRiskStates = config.AcceptedRiskStates,
        HostedPaymentBaseUrl = config.HostedPaymentBaseUrl,
        SolanaDestinationAddress = config.SolanaDestinationAddress,
        PaymentExpiryMinutes = config.PaymentExpiryMinutes
    };
}
