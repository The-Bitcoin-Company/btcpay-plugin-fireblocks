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
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CheckoutExposesOnlyHostedLinkAndDoesNotChangeInvoiceState()
    {
        var (context, details) = CreateContext();

        new FireblocksCheckoutModelExtension(new FakeTimeProvider(Now)).ModifyCheckoutModel(context);

        var model = context.Model;
        Assert.Equal(FireblocksCheckoutModelExtension.CheckoutBodyComponentName, model.CheckoutBodyComponentName);
        Assert.Equal(details.PaymentUrl, model.Address);
        Assert.Equal(details.PaymentUrl, model.InvoiceBitcoinUrl);
        Assert.Equal(details.PaymentUrl, model.AdditionalData["fireblocksPaymentUrl"]?.Value<string>());
        Assert.Equal(InvoiceStatus.New, context.InvoiceEntity.Status);
        Assert.Equal(InvoiceExceptionStatus.None, context.InvoiceEntity.ExceptionStatus);
        Assert.Equal("New", model.Status);
        Assert.DoesNotContain(TestData.Credential, JObject.FromObject(model).ToString());
    }

    [Fact]
    public void CheckoutSuppressesExpiredLink()
    {
        var (context, _) = CreateContext();
        var promptDetails = context.Handler.ParsePaymentPromptDetails(context.Prompt.Details) as FireblocksPaymentPromptDetails;
        context.Prompt.Details = JObject.FromObject(new FireblocksPaymentPromptDetails
        {
            AttemptId = promptDetails!.AttemptId,
            FlowId = promptDetails.FlowId,
            PaymentUrl = promptDetails.PaymentUrl,
            RequestedUsd = promptDetails.RequestedUsd,
            ExpiresAt = Now.AddSeconds(-1).ToUnixTimeSeconds()
        }, ((FireblocksPaymentMethodHandler)context.Handler).Serializer);

        new FireblocksCheckoutModelExtension(new FakeTimeProvider(Now)).ModifyCheckoutModel(context);

        Assert.Equal("expired", context.Model.AdditionalData["fireblocksUnavailableReason"]?.Value<string>());
        Assert.Null(context.Model.AdditionalData["fireblocksPaymentUrl"]?.Value<string>());
        Assert.Equal(string.Empty, context.Model.InvoiceBitcoinUrl);
        Assert.Equal(InvoiceStatus.New, context.InvoiceEntity.Status);
    }

    [Fact]
    public void CheckoutSuppressesOriginalAmountAfterPartialPaymentElsewhere()
    {
        var (context, _) = CreateContext();
        context.InvoiceEntity.Price = 20m;
        context.InvoiceEntity.UpdateTotals();

        new FireblocksCheckoutModelExtension(new FakeTimeProvider(Now)).ModifyCheckoutModel(context);

        Assert.Equal("amount-changed", context.Model.AdditionalData["fireblocksUnavailableReason"]?.Value<string>());
        Assert.Null(context.Model.AdditionalData["fireblocksPaymentUrl"]?.Value<string>());
        Assert.Equal(20m, context.Prompt.Calculate().Due);
        Assert.Equal(InvoiceStatus.New, context.InvoiceEntity.Status);
    }

    private static (CheckoutModelContext Context, FireblocksPaymentPromptDetails Details) CreateContext()
    {
        var handler = new FireblocksPaymentMethodHandler(null!);
        var details = new FireblocksPaymentPromptDetails
        {
            AttemptId = "attempt",
            FlowId = "flow",
            PaymentUrl = "https://pay.dynamic.test/?flow=abc",
            RequestedUsd = 25m,
            ExpiresAt = Now.AddMinutes(10).ToUnixTimeSeconds()
        };
        var invoice = new InvoiceEntity
        {
            Id = "invoice",
            Status = InvoiceStatus.New,
            ExceptionStatus = InvoiceExceptionStatus.None,
            Currency = "USD",
            Price = 25m
        };
        invoice.AddRate(new BTCPayServer.Rating.CurrencyPair("USD", "USD"), 1m);
        invoice.UpdateTotals();
        var prompt = new PaymentPrompt
        {
            ParentEntity = invoice,
            PaymentMethodId = handler.PaymentMethodId,
            Currency = "USD",
            Divisibility = 2,
            RateDivisibility = 2,
            Details = JObject.FromObject(details, handler.Serializer)
        };
        var model = new CheckoutModel { Status = "New" };
        var context = new CheckoutModelContext(
            model, new BTCPayServer.Data.StoreData(), new StoreBlob(), invoice, null!, prompt, handler);
        return (context, details);
    }
}
