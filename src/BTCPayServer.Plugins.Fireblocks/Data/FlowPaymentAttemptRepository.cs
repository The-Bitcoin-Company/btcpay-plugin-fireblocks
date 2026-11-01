using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Data;

public interface IFlowPaymentAttemptRepository
{
    Task<FlowPaymentAttempt?> GetLatestAsync(string storeId, string invoiceId, CancellationToken cancellationToken = default);
    Task<FlowPaymentAttempt?> GetByFlowIdAsync(string storeId, string flowId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowPaymentAttempt>> ListAsync(string storeId, string invoiceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowPaymentAttempt>> ListOutstandingAsync(int limit, CancellationToken cancellationToken = default);
    Task AddAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task UpdateAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
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
        return await context.PaymentAttempts.AsNoTracking()
            .Where(a => a.FlowId != null && a.State == FlowAttemptState.Created)
            .OrderBy(a => a.LastReconciledAt ?? a.CreatedAt)
            .Take(limit)
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
        persisted.State = attempt.State;
        persisted.FlowId = attempt.FlowId;
        persisted.PaymentUrl = attempt.PaymentUrl;
        persisted.ProviderExpiresAt = attempt.ProviderExpiresAt;
        persisted.Error = attempt.Error;
        persisted.LastReconciledAt = attempt.LastReconciledAt;
        persisted.ProviderStatus = attempt.ProviderStatus;
        persisted.RiskStatus = attempt.RiskStatus;
        persisted.SettlementTransactionId = attempt.SettlementTransactionId;
        persisted.SettlementAmountAtomic = attempt.SettlementAmountAtomic;
        persisted.SettlementAmount = attempt.SettlementAmount;
        persisted.SettlementCompletedAt = attempt.SettlementCompletedAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> AddWebhookEventAsync(
        FlowWebhookEvent webhookEvent,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
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
