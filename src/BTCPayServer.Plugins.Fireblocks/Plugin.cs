using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Fireblocks;

public class Plugin : BaseBTCPayServerPlugin
{
    public const string Area = "Fireblocks";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7 && <2.5.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddFireblocksPaymentMethod();
    }
}
