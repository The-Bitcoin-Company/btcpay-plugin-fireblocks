using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Data;

public interface IFlowPaymentAttemptRepository
{
    Task<FlowPaymentAttempt?> GetLatestAsync(string storeId, string invoiceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowPaymentAttempt>> ListAsync(string storeId, string invoiceId, CancellationToken cancellationToken = default);
    Task AddAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
    Task UpdateAsync(FlowPaymentAttempt attempt, CancellationToken cancellationToken = default);
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
        await context.SaveChangesAsync(cancellationToken);
    }
}
