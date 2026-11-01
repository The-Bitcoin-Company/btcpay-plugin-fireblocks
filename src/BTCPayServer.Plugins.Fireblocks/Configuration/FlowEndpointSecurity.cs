using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.Fireblocks.Configuration;

public static class FlowEndpointSecurity
{
    public const string EnableLocalFakeSetting = "FIREBLOCKS_ENABLE_LOCAL_FLOW_FAKE";

    public static bool AllowLocalEndpoints(IConfiguration? configuration) =>
        string.Equals(configuration?[EnableLocalFakeSetting], "true", StringComparison.OrdinalIgnoreCase);
}
