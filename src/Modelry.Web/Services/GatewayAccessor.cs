using Microsoft.Extensions.Options;
using Modelry.Core.Gateway;
using Modelry.Tridion;

namespace Modelry.Web.Services;

/// <summary>Creates the gateway for the current request (demo or live) and disposes it at the end of the request.</summary>
public sealed class GatewayAccessor : IDisposable
{
    private readonly TridionSession _session;
    private readonly TridionOptions _options;
    private readonly InMemoryTridionGateway _demo;
    private ITridionGateway? _gateway;

    public GatewayAccessor(TridionSession session, IOptions<TridionOptions> options, InMemoryTridionGateway demo)
    { _session = session; _options = options.Value; _demo = demo; }

    public ITridionGateway Gateway => _gateway ??= _session.IsAem
        ? throw new InvalidOperationException("This action is only available for Tridion Sites.")
        : _session.IsDemo
        ? _demo
        : CoreServiceGatewayFactory.Create(_options, _session.Connection);

    public void Dispose()
    {
        if (_gateway is IDisposable d && _gateway is not InMemoryTridionGateway) d.Dispose();
    }
}
