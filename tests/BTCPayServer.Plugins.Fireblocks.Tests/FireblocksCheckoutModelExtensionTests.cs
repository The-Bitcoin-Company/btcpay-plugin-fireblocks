using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Payments;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FireblocksCheckoutModelExtensionTests
{
    [Fact]
    public void CheckoutExposesOnlyHostedLinkAndDoesNotChangeInvoiceState()
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var details = new FireblocksPaymentPromptDetails
        {
            AttemptId = "attempt",
            FlowId = "flow",
            PaymentUrl = "https://pay.dynamic.test/?flow=abc",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        };
        var invoice = new InvoiceEntity
        {
            Id = "invoice",
            Status = InvoiceStatus.New,
            ExceptionStatus = InvoiceExceptionStatus.None
        };
        var prompt = new PaymentPrompt
        {
            ParentEntity = invoice,
            PaymentMethodId = handler.PaymentMethodId,
            Details = JObject.FromObject(details, handler.Serializer)
        };
        var model = new CheckoutModel { Status = "New" };
        var context = new CheckoutModelContext(
            model, new BTCPayServer.Data.StoreData(), new StoreBlob(), invoice, null!, prompt, handler);

        new FireblocksCheckoutModelExtension().ModifyCheckoutModel(context);

        Assert.Equal(FireblocksCheckoutModelExtension.CheckoutBodyComponentName, model.CheckoutBodyComponentName);
        Assert.Equal(details.PaymentUrl, model.Address);
        Assert.Equal(details.PaymentUrl, model.InvoiceBitcoinUrl);
        Assert.Equal(details.PaymentUrl, model.AdditionalData["fireblocksPaymentUrl"]?.Value<string>());
        Assert.Equal(InvoiceStatus.New, invoice.Status);
        Assert.Equal(InvoiceExceptionStatus.None, invoice.ExceptionStatus);
        Assert.Equal("New", model.Status);
        Assert.DoesNotContain(TestData.Credential, model.ToString());
    }
}
