using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;
using System.Xml;

namespace Modelry.Tridion;

/// <summary>Binding, credentials and bearer-token plumbing for the Core Service channel.</summary>
public static class CoreServiceChannel
{
    public static Binding CreateBinding(TridionOptions o, CoreServiceConnection c)
    {
        var uri = new Uri(c.Url);
        var https = uri.Scheme == Uri.UriSchemeHttps;
        var timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);

        if (c.Mode == AuthMode.OAuth && o.OAuthBinding.Equals("wsHttp", StringComparison.OrdinalIgnoreCase))
        {
            // .NET (Core) WCF supports WSHttpBinding with Transport security only (no Message security).
            var ws = new WSHttpBinding(https ? SecurityMode.Transport : SecurityMode.None)
            {
                MaxReceivedMessageSize = o.MaxReceivedMessageSize, ReaderQuotas = XmlDictionaryReaderQuotas.Max,
                SendTimeout = timeout, ReceiveTimeout = timeout, OpenTimeout = timeout, CloseTimeout = timeout
            };
            ws.Security.Transport.ClientCredentialType = HttpClientCredentialType.None;
            ApplyProxy(ws, o);
            return ws;
        }

        BasicHttpSecurityMode mode;
        HttpClientCredentialType credential;
        if (c.Mode == AuthMode.Windows)
        {
            // Windows (NTLM / Kerberos) at HTTP level: Transport over HTTPS, TransportCredentialOnly over HTTP (dev only).
            mode = https ? BasicHttpSecurityMode.Transport : BasicHttpSecurityMode.TransportCredentialOnly;
            credential = HttpClientCredentialType.Windows;
        }
        else
        {
            mode = https ? BasicHttpSecurityMode.Transport : BasicHttpSecurityMode.None;
            credential = HttpClientCredentialType.None;   // bearer header added by BearerTokenBehavior
        }
        var b = new BasicHttpBinding(mode)
        {
            MaxReceivedMessageSize = o.MaxReceivedMessageSize, ReaderQuotas = XmlDictionaryReaderQuotas.Max,
            SendTimeout = timeout, ReceiveTimeout = timeout, OpenTimeout = timeout, CloseTimeout = timeout
        };
        b.Security.Transport.ClientCredentialType = credential;
        ApplyProxy(b, o);
        return b;
    }

    /// <summary>Applies credentials / token to a channel factory.</summary>
    public static void Configure(ChannelFactory factory, CoreServiceConnection c)
    {
        if (c.Mode == AuthMode.Windows)
        {
            factory.Credentials.Windows.ClientCredential = c.ToNetworkCredential();
            factory.Credentials.Windows.AllowedImpersonationLevel = System.Security.Principal.TokenImpersonationLevel.Impersonation;
        }
        else
        {
            factory.Endpoint.EndpointBehaviors.Add(new BearerTokenBehavior(c.AccessToken ?? ""));
        }
    }

    private static void ApplyProxy(BasicHttpBinding b, TridionOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ProxyUrl)) return;   // default: system proxy settings
        b.UseDefaultWebProxy = false;
        b.ProxyAddress = new Uri(o.ProxyUrl);
        b.BypassProxyOnLocal = true;
    }

    private static void ApplyProxy(WSHttpBinding b, TridionOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ProxyUrl)) return;
        b.UseDefaultWebProxy = false;
        b.ProxyAddress = new Uri(o.ProxyUrl);
        b.BypassProxyOnLocal = true;
    }
}

/// <summary>Adds "Authorization: Bearer {token}" to every Core Service request (OAuth mode).</summary>
public sealed class BearerTokenBehavior : IEndpointBehavior
{
    private readonly string _token;
    public BearerTokenBehavior(string token) => _token = token;

    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters) { }
    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime) =>
        clientRuntime.ClientMessageInspectors.Add(new Inspector(_token));
    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher) { }
    public void Validate(ServiceEndpoint endpoint) { }

    private sealed class Inspector : IClientMessageInspector
    {
        private readonly string _token;
        public Inspector(string token) => _token = token;

        public object? BeforeSendRequest(ref Message request, IClientChannel channel)
        {
            HttpRequestMessageProperty http;
            if (request.Properties.TryGetValue(HttpRequestMessageProperty.Name, out var existing) && existing is HttpRequestMessageProperty p)
                http = p;
            else
            {
                http = new HttpRequestMessageProperty();
                request.Properties[HttpRequestMessageProperty.Name] = http;
            }
            http.Headers[HttpRequestHeader.Authorization] = "Bearer " + _token;
            return null;
        }

        public void AfterReceiveReply(ref Message reply, object? correlationState) { }
    }
}
