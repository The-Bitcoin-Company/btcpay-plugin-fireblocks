using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Payments;

public enum FlowPaymentRecordResult
{
    Recorded,
    AlreadyRecorded,
    Conflict
}

public interface IFlowPaymentRecorder
{
    Task<FlowPaymentRecordResult> RecordAsync(
        FlowPaymentAttempt attempt,
        FlowSettlementEvidence evidence,
        CancellationToken cancellationToken = default);
}

public sealed class BtcPayFlowPaymentRecorder(
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    FireblocksPaymentMethodHandler handler,
    EventAggregator eventAggregator) : IFlowPaymentRecorder
{
    public async Task<FlowPaymentRecordResult> RecordAsync(
        FlowPaymentAttempt attempt,
        FlowSettlementEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var invoice = await invoiceRepository.GetInvoice(attempt.InvoiceId);
        if (invoice is null || invoice.StoreId != attempt.StoreId ||
            !string.Equals(invoice.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            return FlowPaymentRecordResult.Conflict;
        var prompt = invoice.GetPaymentPrompt(handler.PaymentMethodId);
        if (prompt is null)
            return FlowPaymentRecordResult.Conflict;

        // USDC is accounted at the integration's fixed 1:1 USD policy, not via an
        // exchange conversion. Persist the rate for prompts created by older versions.
        if (invoice.TryGetRate(FireblocksStoreConfig.UsdcSymbol, out var rate) && rate != 1m)
            return FlowPaymentRecordResult.Conflict;
        if (rate == 0m)
        {
            if (!await EnsureUsdcRateAsync(invoice.Id))
                return FlowPaymentRecordResult.Conflict;
            invoice = await invoiceRepository.GetInvoice(attempt.InvoiceId);
            prompt = invoice?.GetPaymentPrompt(handler.PaymentMethodId);
            if (invoice is null || prompt is null)
                return FlowPaymentRecordResult.Conflict;
        }

        var paymentData = CreatePaymentData(invoice, attempt, evidence, handler);
        var payment = await paymentService.AddPayment(paymentData, [evidence.TransactionId, attempt.FlowId!]);
        if (payment is null)
        {
            var refreshed = await invoiceRepository.GetInvoice(attempt.InvoiceId);
            var existing = refreshed?.GetPayments(false).SingleOrDefault(p =>
                p.Id == evidence.TransactionId && p.PaymentMethodId == handler.PaymentMethodId);
            if (existing is null)
                return FlowPaymentRecordResult.Conflict;
            // AddPayment may have committed before a crash prevented ReceivedPayment.
            // Wake the watcher idempotently so on-time accounting is reevaluated now.
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(refreshed!.Id));
            return FlowPaymentRecordResult.AlreadyRecorded;
        }

        var updated = await invoiceRepository.GetInvoice(attempt.InvoiceId);
        if (updated is not null)
            eventAggregator.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return FlowPaymentRecordResult.Recorded;
    }

    private async Task<bool> EnsureUsdcRateAsync(string invoiceId)
    {
        // UpdatePrompt intentionally persists only prompt details. Rates live in the
        // invoice blob, so update that blob directly with the host's concurrency token.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = invoiceRepository.DbContextFactory.CreateContext();
            var data = await context.Invoices.FindAsync(invoiceId);
            if (data is null)
                return false;
            var persisted = data.GetBlob();
            if (persisted.TryGetRate(FireblocksStoreConfig.UsdcSymbol, out var existing))
                return existing == 1m;
            persisted.AddRate(new CurrencyPair(FireblocksStoreConfig.UsdcSymbol, "USD"), 1m);
            data.SetBlob(persisted);
            try
            {
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Retry against the current blob; another writer may have added it.
            }
        }
        return false;
    }

    public static PaymentData CreatePaymentData(
        InvoiceEntity invoice,
        FlowPaymentAttempt attempt,
        FlowSettlementEvidence evidence,
        FireblocksPaymentMethodHandler handler)
    {
        var details = new FireblocksPaymentDetails
        {
            AttemptId = attempt.Id,
            FlowId = attempt.FlowId!,
            TransactionId = evidence.TransactionId,
            Asset = FireblocksStoreConfig.UsdcSymbol,
            Network = FireblocksStoreConfig.SolanaChainName,
            ChainId = FireblocksStoreConfig.SolanaChainId,
            TokenAddress = FireblocksStoreConfig.UsdcMint,
            TokenDecimals = FireblocksStoreConfig.UsdcDecimals,
            Amount = evidence.Amount,
            AmountAtomic = evidence.AmountAtomic,
            Destination = evidence.Destination,
            RequestedUsd = attempt.RequestedUsd,
            SettlementCompletedAt = evidence.CompletedAt?.ToUnixTimeSeconds()
        };
        var blob = new PaymentBlob
        {
            Destination = evidence.Destination,
            PaymentMethodFee = 0m,
            Divisibility = FireblocksStoreConfig.UsdcDecimals
        }.SetDetails(handler, details);
        return new PaymentData
        {
            Id = evidence.TransactionId,
            InvoiceDataId = invoice.Id,
            Created = evidence.ReceivedAt,
            Status = PaymentStatus.Settled,
            Currency = FireblocksStoreConfig.UsdcSymbol,
            Amount = evidence.TokenAmount
        }.SetBlob(handler.PaymentMethodId, blob);
    }
}
