using Modelry.Core.Gateway;

namespace Modelry.Tridion;

public static class CoreServiceGatewayFactory
{
#if CORESERVICE_PROXY
    public static bool IsAvailable => true;
    public static ITridionGateway Create(TridionOptions options, CoreServiceConnection connection) =>
        new CoreServiceTridionGateway(options, connection);
#else
    public static bool IsAvailable => false;
    public static ITridionGateway Create(TridionOptions options, CoreServiceConnection connection) =>
        throw new InvalidOperationException(
            "The Core Service proxy has not been generated yet. Run 'Connected Services/CoreService/generate-proxy.ps1' and rebuild (see README). Demo mode works without it.");
#endif
}
