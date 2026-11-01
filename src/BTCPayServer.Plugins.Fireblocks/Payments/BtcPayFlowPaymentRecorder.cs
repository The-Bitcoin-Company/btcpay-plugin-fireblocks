using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Configuration;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;

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
            invoice.AddRate(new CurrencyPair(FireblocksStoreConfig.UsdcSymbol, "USD"), 1m);
            await invoiceRepository.UpdatePrompt(invoice.Id, prompt);
        }

        var paymentData = CreatePaymentData(invoice, attempt, evidence, handler);
        var payment = await paymentService.AddPayment(paymentData, [evidence.TransactionId, attempt.FlowId!]);
        if (payment is null)
        {
            var refreshed = await invoiceRepository.GetInvoice(attempt.InvoiceId);
            return refreshed?.GetPayments(false).Any(p =>
                p.Id == evidence.TransactionId && p.PaymentMethodId == handler.PaymentMethodId) == true
                ? FlowPaymentRecordResult.AlreadyRecorded
                : FlowPaymentRecordResult.Conflict;
        }

        var updated = await invoiceRepository.GetInvoice(attempt.InvoiceId);
        if (updated is not null)
            eventAggregator.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return FlowPaymentRecordResult.Recorded;
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
