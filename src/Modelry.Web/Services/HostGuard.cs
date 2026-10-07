using Modelry.Tridion;

namespace Modelry.Web.Services;

public static class HostGuard
{
    /// <summary>
    /// Validates an entered URL: absolute, not a placeholder, HTTPS (HTTP only for hosts in Tridion:AllowHttpHosts or localhost),
    /// and host in Tridion:AllowedHosts when that list is configured.
    /// </summary>
    public static string? Validate(string? url, TridionOptions o, string label)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains('{')) return $"{label}: replace the placeholder with your real URL.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return $"{label}: not a valid absolute URL.";
        var httpAllowed = uri.IsLoopback || o.AllowHttpHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && httpAllowed))
            return $"{label}: HTTPS is required (add '{uri.Host}' to Tridion:AllowHttpHosts for a dev server without HTTPS).";
        if (o.AllowedHosts.Count > 0 && !o.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return $"{label}: host '{uri.Host}' is not in Tridion:AllowedHosts.";
        return null;
    }

    public static bool IsHttp(string url) => url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
