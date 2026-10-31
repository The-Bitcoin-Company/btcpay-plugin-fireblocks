using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Fireblocks;

public static class FireblocksServiceCollectionExtensions
{
    /// <summary>
    /// Registration seam for the future payment-method handler. Intentionally registers
    /// nothing until a real handler exists; no payment method is advertised by this scaffold.
    /// </summary>
    public static IServiceCollection AddFireblocksPaymentMethod(this IServiceCollection services)
    {
        return services;
    }
}
