using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Data;

public interface IFlowPaymentAttemptRepository
{
    Task<FlowPaymentAttempt?> GetLatestAsync(string storeId, string invoiceId, CancellationToken cancellationToken = default);
    Task<FlowPaymentAttempt?> GetByFlowIdAsync(string storeId, string flowId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowPaymentAttempt>> ListAsync(string storeId, string invoiceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowPaymentAttempt>> ListOutstandingAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListSettledInvoiceIdsAsync(CancellationToken cancellationToken = default);
    Task AddAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task UpdateAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task UpdateReconciliationDiagnosticAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task UpdateReconciliationTerminalAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task<bool> ReserveSettlementAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task FinalizeSettlementAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task<bool> AddWebhookEventAsync(FlowWebhookEvent webhookEvent, CancellationToken cancellationToken = default);
}

public sealed class FlowPaymentAttemptRepository(IDbContextFactory<FireblocksDbContext> contextFactory)
    : IFlowPaymentAttemptRepository
{
    public async Task<FlowPaymentAttempt?> GetLatestAsync(
        string storeId,
        string invoiceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PaymentAttempts.AsNoTracking()
            .Where(a => a.StoreId == storeId && a.InvoiceId == invoiceId)
            .OrderByDescending(a => a.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<FlowPaymentAttempt?> GetByFlowIdAsync(
        string storeId,
        string flowId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PaymentAttempts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.StoreId == storeId && a.FlowId == flowId, cancellationToken);
    }

    public async Task<IReadOnlyList<FlowPaymentAttempt>> ListAsync(
        string storeId,
        string invoiceId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PaymentAttempts.AsNoTracking()
            .Where(a => a.StoreId == storeId && a.InvoiceId == invoiceId)
            .OrderBy(a => a.AttemptNumber)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FlowPaymentAttempt>> ListOutstandingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.PaymentAttempts.AsNoTracking()
            .Where(a => a.FlowId != null && a.State == FlowAttemptState.Created);
        if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // SQLite is a test-only provider and cannot ORDER BY DateTimeOffset.
            var outstanding = await query.ToArrayAsync(cancellationToken);
            return outstanding.OrderBy(a => a.LastReconciledAt ?? a.CreatedAt).Take(limit).ToArray();
        }
        return await query
            .OrderBy(a => a.LastReconciledAt ?? a.CreatedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListSettledInvoiceIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PaymentAttempts.AsNoTracking()
            .Where(a => a.State == FlowAttemptState.Settled)
            .Select(a => a.InvoiceId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
    }

    public async Task AddAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.PaymentAttempts.Add(attempt);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var persisted = await context.PaymentAttempts.SingleAsync(a => a.Id == attempt.Id, cancellationToken);
        // This method belongs to link creation. Never let a delayed creator or
        // diagnostic caller regress a reconciled terminal state.
        if (persisted.State is FlowAttemptState.Failed or FlowAttemptState.Settled or FlowAttemptState.Cancelled)
            return;
        persisted.State = attempt.State;
        persisted.FlowId = attempt.FlowId;
        persisted.PaymentUrl = attempt.PaymentUrl;
        persisted.ProviderExpiresAt = attempt.ProviderExpiresAt;
        persisted.Error = attempt.Error;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateReconciliationDiagnosticAsync(
        FlowPaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.PaymentAttempts
            .Where(a => a.Id == attempt.Id && a.State == FlowAttemptState.Created)
            .ExecuteUpdateAsync(update => update
                .SetProperty(a => a.LastReconciledAt, attempt.LastReconciledAt)
                .SetProperty(a => a.ProviderStatus, attempt.ProviderStatus)
                .SetProperty(a => a.RiskStatus, attempt.RiskStatus)
                .SetProperty(a => a.Error, attempt.Error), cancellationToken);
    }

    public async Task UpdateReconciliationTerminalAsync(
        FlowPaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.PaymentAttempts
            .Where(a => a.Id == attempt.Id && a.State == FlowAttemptState.Created &&
                a.SettlementTransactionId == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(a => a.State, attempt.State)
                .SetProperty(a => a.LastReconciledAt, attempt.LastReconciledAt)
                .SetProperty(a => a.ProviderStatus, attempt.ProviderStatus)
                .SetProperty(a => a.RiskStatus, attempt.RiskStatus)
                .SetProperty(a => a.Error, attempt.Error), cancellationToken);
    }

    public async Task<bool> ReserveSettlementAsync(
        FlowPaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context.PaymentAttempts
            .Where(a => a.Id == attempt.Id && a.State == FlowAttemptState.Created &&
                (a.SettlementTransactionId == null || a.SettlementTransactionId == attempt.SettlementTransactionId) &&
                (a.SettlementAmountAtomic == null || a.SettlementAmountAtomic == attempt.SettlementAmountAtomic) &&
                (a.SettlementAmount == null || a.SettlementAmount == attempt.SettlementAmount))
            .ExecuteUpdateAsync(update => update
                .SetProperty(a => a.LastReconciledAt, attempt.LastReconciledAt)
                .SetProperty(a => a.ProviderStatus, attempt.ProviderStatus)
                .SetProperty(a => a.RiskStatus, attempt.RiskStatus)
                .SetProperty(a => a.Error, attempt.Error)
                .SetProperty(a => a.SettlementTransactionId, attempt.SettlementTransactionId)
                .SetProperty(a => a.SettlementAmountAtomic, attempt.SettlementAmountAtomic)
                .SetProperty(a => a.SettlementAmount, attempt.SettlementAmount)
                .SetProperty(a => a.SettlementCompletedAt, attempt.SettlementCompletedAt), cancellationToken);
        return updated == 1;
    }

    public async Task FinalizeSettlementAsync(
        FlowPaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.PaymentAttempts
            .Where(a => a.Id == attempt.Id && a.State == FlowAttemptState.Created &&
                a.SettlementTransactionId == attempt.SettlementTransactionId &&
                a.SettlementAmountAtomic == attempt.SettlementAmountAtomic &&
                a.SettlementAmount == attempt.SettlementAmount)
            .ExecuteUpdateAsync(update => update
                .SetProperty(a => a.State, attempt.State)
                .SetProperty(a => a.LastReconciledAt, attempt.LastReconciledAt)
                .SetProperty(a => a.ProviderStatus, attempt.ProviderStatus)
                .SetProperty(a => a.RiskStatus, attempt.RiskStatus)
                .SetProperty(a => a.Error, attempt.Error), cancellationToken);
    }

    public async Task<bool> AddWebhookEventAsync(
        FlowWebhookEvent webhookEvent,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.WebhookEvents.AsNoTracking()
            .AnyAsync(e => e.Id == webhookEvent.Id, cancellationToken))
            return false;
        context.WebhookEvents.Add(webhookEvent);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            if (await context.WebhookEvents.AsNoTracking()
                .AnyAsync(e => e.Id == webhookEvent.Id, cancellationToken))
                return false;
            throw;
        }
    }
}
