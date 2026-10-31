using BTCPayServer.Payments;
using BTCPayServer.Plugins.Fireblocks.Data;
using BTCPayServer.Plugins.Fireblocks.Flow;
using BTCPayServer.Plugins.Fireblocks.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BTCPayServer.Plugins.Fireblocks;

public static class FireblocksServiceCollectionExtensions
{
    public const string FlowHttpClient = "BTCPayServer.Plugins.Fireblocks.Flow";

    public static IServiceCollection AddFireblocksPaymentMethod(this IServiceCollection services)
    {
        services.AddPluginDbContext<FireblocksDbContext>();
        services.AddHttpClient(FlowHttpClient, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton<IFlowApiClient>(provider => new FlowApiClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(FlowHttpClient)));
        services.TryAddSingleton<IFlowPaymentAttemptRepository, FlowPaymentAttemptRepository>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFlowPaymentService, FlowPaymentService>();
        services.TryAddSingleton<FireblocksPaymentMethodHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPaymentMethodHandler, FireblocksPaymentMethodHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICheckoutModelExtension, FireblocksCheckoutModelExtension>());
        services.AddUIExtension("store-integrations-nav", "/Views/Shared/Fireblocks/NavExtension.cshtml");
        services.AddUIExtension("checkout-end", "/Views/Shared/Fireblocks/FireblocksMethodCheckout.cshtml");
        return services;
    }
}
