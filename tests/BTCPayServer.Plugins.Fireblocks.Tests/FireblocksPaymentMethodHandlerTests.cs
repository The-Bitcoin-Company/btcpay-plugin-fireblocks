using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using BTCPayServer.Plugins.Fireblocks.Payments;
using BTCPayServer.Services.Invoices;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class FireblocksPaymentMethodHandlerTests
{
    [Fact]
    public async Task ExpiryPolicyShortensBtcpayPromptAlongsideFlowLink()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var config = TestData.Config();
        config.PaymentExpiryMinutes = 1;
        var service = new StubFlowPaymentService(now);
        var handler = new FireblocksPaymentMethodHandler(service, timeProvider: new FakeTimeProvider(now));
        var invoice = new InvoiceEntity
        {
            Id = "invoice",
            Currency = "USD",
            Price = 25m,
            ExpirationTime = now.AddMinutes(15)
        };
        invoice.AddRate(new BTCPayServer.Rating.CurrencyPair("USD", "USD"), 1m);
        invoice.UpdateTotals();
        var store = new StoreData { Id = "store" };
        var context = new PaymentMethodContext(
            store,
            new StoreBlob(),
            config.ToStorageToken(handler.Serializer),
            handler,
            invoice,
            new InvoiceLogs(),
            null!);
        await handler.BeforeFetchingRates(context);

        await handler.ConfigurePrompt(context);

        Assert.Equal(now.AddMinutes(1), invoice.ExpirationTime);
        Assert.Equal(now.AddMinutes(1), service.InvoiceExpiresAt);
        var details = handler.ParsePaymentPromptDetails(context.Prompt.Details);
        Assert.Equal(25m, details.RequestedUsd);
        Assert.Equal(invoice.ExpirationTime.ToUnixTimeSeconds(), details.ExpiresAt);
    }

    private sealed class StubFlowPaymentService(DateTimeOffset now) : IFlowPaymentService
    {
        public DateTimeOffset InvoiceExpiresAt { get; private set; }

        public Task<FlowPaymentAttempt> CreateOrGetPaymentLinkAsync(
            string storeId,
            string invoiceId,
            decimal requestedUsd,
            DateTimeOffset invoiceExpiresAt,
            FireblocksStoreConfig config,
            CancellationToken cancellationToken = default)
        {
            InvoiceExpiresAt = invoiceExpiresAt;
            return Task.FromResult(new FlowPaymentAttempt
            {
                Id = "attempt",
                StoreId = storeId,
                InvoiceId = invoiceId,
                AttemptNumber = 1,
                CreatedAt = now,
                ExpiresAt = invoiceExpiresAt,
                ApiEndpoint = config.ApiEndpoint,
                EnvironmentId = config.EnvironmentId,
                RequestedUsd = requestedUsd,
                CreationRequest = "{}",
                State = FlowAttemptState.Created,
                FlowId = "flow",
                PaymentUrl = "https://pay.dynamic.test/"
            });
        }
    }
}
