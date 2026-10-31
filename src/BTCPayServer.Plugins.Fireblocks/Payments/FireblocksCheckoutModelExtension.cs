using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Fireblocks.Payments;

public sealed class FireblocksCheckoutModelExtension(TimeProvider? timeProvider = null) : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "FireblocksCheckoutBody";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PaymentMethodId PaymentMethodId => FireblocksPaymentMethodHandler.PaymentMethod;
    public string Image => "~/Resources/fireblocks.svg";
    public string Badge => "Flow";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler.ParsePaymentPromptDetails(context.Prompt.Details) is not FireblocksPaymentPromptDetails details)
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        context.Model.AdditionalData["fireblocksPaymentAttemptId"] = new JValue(details.AttemptId);
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var expired = details.ExpiresAt <= now || details.ProviderExpiresAt <= now;
        var amountChanged = context.Prompt.Calculate().Due != details.RequestedUsd;
        if (expired || amountChanged)
        {
            context.Model.Address = string.Empty;
            context.Model.InvoiceBitcoinUrl = string.Empty;
            context.Model.InvoiceBitcoinUrlQR = string.Empty;
            context.Model.AdditionalData["fireblocksPaymentUrl"] = JValue.CreateNull();
            context.Model.AdditionalData["fireblocksUnavailableReason"] =
                new JValue(expired ? "expired" : "amount-changed");
            return;
        }

        context.Model.Address = details.PaymentUrl;
        context.Model.InvoiceBitcoinUrl = details.PaymentUrl;
        context.Model.InvoiceBitcoinUrlQR = string.Empty;
        context.Model.AdditionalData["fireblocksPaymentUrl"] = new JValue(details.PaymentUrl);
    }
}
