using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Fireblocks.Payments;

public interface IFlowInvoiceExpiryService
{
    Task ShortenAsync(string invoiceId, DateTimeOffset deadline);
}

public sealed class FlowInvoiceExpiryService(
    InvoiceRepository invoiceRepository,
    TimeProvider timeProvider) : IFlowInvoiceExpiryService
{
    public async Task ShortenAsync(string invoiceId, DateTimeOffset deadline)
    {
        // Initial invoice creation has no database row yet; its in-memory expiration is
        // saved by the normal creation path. Lazy activation must use the host API so
        // the shortened deadline and monitoring schedule survive the prompt reload.
        var persisted = await invoiceRepository.GetInvoice(invoiceId);
        if (persisted is null || persisted.ExpirationTime <= deadline)
            return;
        await invoiceRepository.UpdateInvoiceExpiry(invoiceId, deadline - timeProvider.GetUtcNow());
    }
}
