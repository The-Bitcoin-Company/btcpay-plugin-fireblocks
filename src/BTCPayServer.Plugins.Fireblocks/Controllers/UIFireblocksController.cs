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
    IStoreConfigurationWriter storeWriter,
    FireblocksPaymentMethodHandler handler,
    IConfiguration configuration,
    CoreApiClient coreApiClient,
    ICoreApiSettingsStore coreSettings,
    VaultProvisioningService? vaults = null) : Controller
{
    private StoreData CurrentStore => HttpContext.GetStoreData();

    [HttpGet]
    public async Task<IActionResult> Configure()
    {
        var stored = CurrentStore.GetPaymentMethodConfig(handler.PaymentMethodId);
        var config = stored is null ? new FireblocksStoreConfig() : handler.ParsePaymentMethodConfig(stored);
        var model = ToViewModel(config, CurrentStore, await coreSettings.Get(CurrentStore.Id));
        await PopulateVault(model);
        return View(model);
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
        if (vaults is not null && await vaults.Get(CurrentStore.Id) is { ProductionCompatible: false, Address: not null } testVault &&
            config.SolanaDestinationAddress == testVault.Address)
            ModelState.AddModelError(nameof(model.SolanaDestinationAddress), "This is a non-production vault address; Dynamic checkout requires mainnet USDC.");

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
            await PopulateVault(model);
            return View(model);
        }

        CurrentStore.SetPaymentMethodConfig(handler.PaymentMethodId, config.ToStorageToken(handler.Serializer));
        var storeBlob = CurrentStore.GetStoreBlob();
        storeBlob.SetExcluded(handler.PaymentMethodId, !model.Enabled);
        CurrentStore.SetStoreBlob(storeBlob);
        await storeWriter.Update(CurrentStore);
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
            await PopulateVault(safe);
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

    [HttpPost("vault")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureVault(string? assetId, string command)
    {
        if (vaults is null) return NotFound();
        try
        {
            if (command == "use")
            {
                var binding = await vaults.Get(CurrentStore.Id);
                var core = await coreSettings.Get(CurrentStore.Id);
                if (binding is not { State: "Success", ProductionCompatible: true, Address: not null } ||
                    binding.WorkspaceFingerprint != VaultProvisioningService.Fingerprint(core) ||
                    !FireblocksStoreConfig.IsValidSolanaAddress(binding.Address))
                    throw new CoreApiException("Only a ready mainnet USDC vault from the saved workspace can be used for checkout.");
                var stored = CurrentStore.GetPaymentMethodConfig(handler.PaymentMethodId);
                var config = stored is null ? new FireblocksStoreConfig() : handler.ParsePaymentMethodConfig(stored);
                config.SolanaDestinationAddress = binding.Address;
                CurrentStore.SetPaymentMethodConfig(handler.PaymentMethodId, config.ToStorageToken(handler.Serializer));
                // A newly created Dynamic config must remain disabled until its other
                // required settings have been explicitly saved and validated.
                if (stored is null)
                {
                    var blob = CurrentStore.GetStoreBlob();
                    blob.SetExcluded(handler.PaymentMethodId, true);
                    CurrentStore.SetStoreBlob(blob);
                }
                await storeWriter.Update(CurrentStore);
                TempData[WellKnownTempData.SuccessMessage] = "Collection vault address saved as the Solana destination. Checkout enablement is unchanged.";
            }
            else
            {
                var binding = await vaults.Provision(CurrentStore.Id, assetId, command == "activate");
                TempData[binding.State == "Error" ? WellKnownTempData.ErrorMessage : WellKnownTempData.SuccessMessage] = binding.Diagnostic;
            }
        }
        catch (CoreApiException e) { TempData[WellKnownTempData.ErrorMessage] = e.Message; }
        return RedirectToAction(nameof(Configure), new { storeId = CurrentStore.Id });
    }

    private async Task PopulateVault(FireblocksStoreConfigViewModel model)
    {
        if (vaults is null) return;
        model.Vault = await vaults.Get(CurrentStore.Id);
        if (model.Vault is not null) return;
        try { model.VaultAssets = await vaults.Assets(CurrentStore.Id); }
        catch (CoreApiException e) { model.VaultMessage = e.Message; }
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
