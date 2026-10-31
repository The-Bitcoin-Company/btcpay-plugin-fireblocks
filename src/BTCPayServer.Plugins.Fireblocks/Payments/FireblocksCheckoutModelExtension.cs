using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Fireblocks.Payments;

public sealed class FireblocksCheckoutModelExtension : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "FireblocksCheckoutBody";

    public PaymentMethodId PaymentMethodId => FireblocksPaymentMethodHandler.PaymentMethod;
    public string Image => "~/Resources/fireblocks.svg";
    public string Badge => "Flow";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler.ParsePaymentPromptDetails(context.Prompt.Details) is not FireblocksPaymentPromptDetails details)
            return;

        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        context.Model.Address = details.PaymentUrl;
        context.Model.InvoiceBitcoinUrl = details.PaymentUrl;
        context.Model.InvoiceBitcoinUrlQR = string.Empty;
        context.Model.AdditionalData["fireblocksPaymentAttemptId"] = new JValue(details.AttemptId);
        context.Model.AdditionalData["fireblocksPaymentUrl"] = new JValue(details.PaymentUrl);
    }
}
