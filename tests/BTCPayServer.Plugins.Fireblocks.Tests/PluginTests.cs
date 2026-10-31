using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.Fireblocks.Tests;

public class PluginTests
{
    [Fact]
    public void AssemblyExposesOneDiscoverablePluginWithMetadata()
    {
        var type = Assert.Single(typeof(Plugin).Assembly.GetExportedTypes(),
            t => !t.IsAbstract && typeof(IBTCPayServerPlugin).IsAssignableFrom(t));
        var plugin = Assert.IsType<Plugin>(Activator.CreateInstance(type));

        Assert.Equal("BTCPayServer.Plugins.Fireblocks", plugin.Identifier);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Name, plugin.Identifier);
        Assert.Equal("BTCPay Server Fireblocks", plugin.Name);
        Assert.Equal(new Version(0, 1, 0), plugin.Version);
        Assert.Contains("No payment processing is implemented", plugin.Description);
        var dependency = Assert.Single(plugin.Dependencies);
        Assert.Equal("BTCPayServer", dependency.Identifier);
        Assert.Equal(">=2.4.7 && <2.5.0", dependency.Condition);
    }

    [Theory]
    [InlineData("2.4.6", false)]
    [InlineData("2.4.7", true)]
    [InlineData("2.4.8", true)]
    [InlineData("2.5.0", false)]
    public void DependencyEnforcesSupportedHostVersions(string version, bool supported)
    {
        var dependency = Assert.Single(new Plugin().Dependencies);
        Assert.True(VersionCondition.TryParse(dependency.Condition, out _));
        Assert.Equal(supported, dependency.ParseCondition().IsFulfilled(Version.Parse(version)));
    }

    [Fact]
    public void ExecuteLeavesExistingServicesUnchanged()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new object());
        var before = services.ToArray();

        new Plugin().Execute(services);
        new Plugin().Execute(services);

        Assert.Equal(before, services.ToArray());
        Assert.Same(services, services.AddFireblocksPaymentMethod());
    }
}
