using System.Net;
using Dashboard.Contracts;

namespace Dashboard.Api;

// The tunnel service authenticates the remote user. The host trusts only its
// loopback listener, with an exact local/tunnel host and browser-origin checks.
// Forwarded headers and claimed identity headers never grant access.
public static class DevTunnelAccess
{
    public static bool Allows(HttpRequest request, HubOptions options, TunnelState tunnel)
    {
        var connection = request.HttpContext.Connection;
        if (!options.UsesDevTunnelAuthentication || connection.LocalPort != options.UiPort ||
            connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)) return false;

        var host = request.Host;
        var local = host.Port == options.UiPort &&
            (host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             (IPAddress.TryParse(host.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)));
        var remote = tunnel.Enabled && tunnel.State == "hosting" && tunnel.Id == options.UiTunnel.Id &&
            Uri.TryCreate(tunnel.Url, UriKind.Absolute, out var url) && url.Scheme == "https" &&
            host.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase) && (host.Port ?? 443) == url.Port;
        if (!local && !remote) return false;
        // A Microsoft sign-in redirect or a bookmark may navigate to the public
        // shell across sites. Never grant that exception to data/API requests.
        var shellNavigation = HttpMethods.IsGet(request.Method) && request.Path == "/" &&
            request.Headers["Sec-Fetch-Mode"] == "navigate" && request.Headers["Sec-Fetch-Dest"] == "document";
        if (!shellNavigation && request.Headers["Sec-Fetch-Site"].Any(value => value == "cross-site")) return false;
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
                origin.Scheme != (local ? request.Scheme : "https") ||
                !origin.Host.Trim('[', ']').Equals(host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
                origin.Port != (host.Port ?? 443) || origin.AbsolutePath != "/" ||
                origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0) return false;
        }
        else if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && request.Headers.ContainsKey("Sec-Fetch-Site"))
            return false;
        return true;
    }
}
