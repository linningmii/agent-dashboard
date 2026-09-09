using System.Net;
using System.Text.Json;
using Dashboard.Api;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dashboard.Tests;

public sealed class UiAuthenticationTests
{
    private static HubOptions Options(string mode = "dev-tunnel") => new("unused", "unused", 4317, 4319, "127.0.0.1", new(true, "owner.jpe1", 4317), new(), null, mode);
    private static readonly TunnelState Hosted = new(true, "hosting", "owner.jpe1", "https://example-4317.jpe1.devtunnels.ms");
    private static HttpRequest Request(string host = "localhost:4317", string method = "GET", string? origin = null)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Connection.LocalPort = 4317;
        context.Request.Scheme = "http"; context.Request.Host = new(host); context.Request.Method = method;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context.Request;
    }

    [Fact] public void TunnelModeRequiresExplicitPrivateLoopbackConfiguration()
    {
        Options().ValidateAuthentication();
        Options("access-key").ValidateAuthentication();
        Assert.Throws<InvalidDataException>(() => (Options() with { BindAddress = "0.0.0.0" }).ValidateAuthentication());
        Assert.Throws<InvalidDataException>(() => (Options() with { BindAddress = "::" }).ValidateAuthentication());
        Assert.Throws<InvalidDataException>(() => (Options() with { UiTunnel = new() }).ValidateAuthentication());
        Assert.Throws<InvalidDataException>(() => Options("none").ValidateAuthentication());
    }

    [Theory]
    [InlineData("localhost:4317")]
    [InlineData("127.0.0.1:4317")]
    [InlineData("[::1]:4317")]
    [InlineData("example-4317.jpe1.devtunnels.ms")]
    public void AllowsLocalAndExactHostedUiOrigin(string host)
    {
        Assert.True(DevTunnelAccess.Allows(Request(host), Options(), Hosted));
        var origin = (host.Contains("devtunnels") ? "https://" : "http://") + host;
        var post = Request(host, "POST", origin); post.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.True(DevTunnelAccess.Allows(post, Options(), Hosted));
    }

    [Theory]
    [InlineData("evil.example:4317")]
    [InlineData("localhost:4319")]
    [InlineData("example-4317.jpe1.devtunnels.ms.evil.example")]
    [InlineData("example-4317.jpe1.devtunnels.ms:444")]
    [InlineData("other-4317.jpe1.devtunnels.ms")]
    public void RejectsRebindingAndUnrelatedHostsEvenWithClaimedIdentity(string host)
    {
        var request = Request(host); request.Headers["X-Forwarded-Host"] = "example-4317.jpe1.devtunnels.ms";
        request.Headers["X-Tunnel-Authorization"] = "tunnel forged"; request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"] = "owner@example.com";
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
    }

    [Fact] public void RejectsNonLocalPeersWrongListenerAndInactiveTunnels()
    {
        var request = Request(); request.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
        request = Request(); request.HttpContext.Connection.LocalPort = 4319;
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
        request = Request("example-4317.jpe1.devtunnels.ms");
        foreach (var state in new[] { "starting", "retrying", "stopping", "stopped", "disabled" })
            Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted with { State = state }));
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted with { Id = "another.jpe1" }));
        Assert.False(DevTunnelAccess.Allows(Request(), Options("access-key"), Hosted));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost:4319")]
    [InlineData("http://localhost:4317/extra")]
    public void RejectsCrossOriginBrowserRequests(string origin)
    {
        Assert.False(DevTunnelAccess.Allows(Request(origin: origin), Options(), Hosted));
        Assert.False(DevTunnelAccess.Allows(Request(method: "POST", origin: origin), Options(), Hosted));
    }

    [Fact] public void RejectsCrossSiteFetchesAndBrowserWritesWithoutOrigin()
    {
        var request = Request(); request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
        request = Request(method: "POST"); request.Headers["Sec-Fetch-Site"] = "same-origin";
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
        request.Headers.Remove("Sec-Fetch-Site");
        Assert.True(DevTunnelAccess.Allows(request, Options(), Hosted)); // Local CLI has no browser headers.
    }

    [Fact] public void AllowsSignInRedirectToShellButNotApiNavigation()
    {
        var request = Request("example-4317.jpe1.devtunnels.ms"); request.Path = "/";
        request.Headers["Sec-Fetch-Site"] = "cross-site"; request.Headers["Sec-Fetch-Mode"] = "navigate";
        request.Headers["Sec-Fetch-Dest"] = "document";
        Assert.True(DevTunnelAccess.Allows(request, Options(), Hosted));
        request.Path = "/api/status"; Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
        request.Path = "/"; request.Headers["Sec-Fetch-Dest"] = "iframe";
        Assert.False(DevTunnelAccess.Allows(request, Options(), Hosted));
    }

    [Fact] public void TunnelModeDoesNotCreateOrAcceptDashboardKeyOrCookie()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dashboard-auth-" + Guid.NewGuid());
        try
        {
            var options = Options() with { DataDirectory = dir };
            var hub = new HubService(new(Path.Combine(dir, "hub.sqlite")), TimeProvider.System);
            using var tunnels = new TunnelWorker(options, NullLogger<TunnelWorker>.Instance);
            var auth = new UiAuthentication(options, hub, tunnels);
            Assert.False(auth.RequiresLogin); Assert.True(auth.Authorized(Request()));
            Assert.False(File.Exists(Path.Combine(dir, "ui-access-key")));
            Assert.False(auth.Login("any-key")); Assert.Throws<InvalidOperationException>(() => auth.Issue());
            var request = Request("evil.example"); request.Headers.Authorization = "Bearer any-key";
            request.Headers.Cookie = "dashboard-session=claimed-session"; Assert.False(auth.Authorized(request));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("[]", "[]", 1, 4317, "http", 0, true)]
    [InlineData("[{\"type\":\"anonymous\"}]", "[]", 1, 4317, "http", 0, false)]
    [InlineData("[]", "[{\"type\":\"users\"}]", 1, 4317, "http", 0, false)]
    [InlineData("[]", "[]", 2, 4317, "http", 0, false)]
    [InlineData("[]", "[]", 1, 4319, "http", 0, false)]
    [InlineData("[]", "[]", 1, 4317, "auto", 0, false)]
    [InlineData("[]", "[]", 1, 4317, "http", 60, false)]
    public void RequiresOwnerOnlyTunnelAndPort(string acl, string portAcl, int count, int port, string protocol, int timeout, bool valid)
    {
        using var tunnel = JsonDocument.Parse("{\"tunnel\":{\"accessControl\":" + acl + "}}");
        var ports = JsonSerializer.SerializeToElement(new { ports = Enumerable.Range(0, count).Select(_ => new { portNumber = port }) });
        using var details = JsonDocument.Parse("{\"port\":{\"portNumber\":" + port + ",\"protocol\":\"" + protocol + "\",\"requestTimeoutSeconds\":" + timeout + ",\"accessControl\":" + portAcl + "}}");
        if (valid) TunnelWorker.ValidatePrivateConfiguration(tunnel.RootElement, ports, details.RootElement, 4317);
        else Assert.Throws<InvalidDataException>(() => TunnelWorker.ValidatePrivateConfiguration(tunnel.RootElement, ports, details.RootElement, 4317));
    }
}
