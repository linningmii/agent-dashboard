using Dashboard.Core;

namespace Dashboard.Api;

public sealed class UiAuthentication
{
    private readonly string? keyHash;
    private readonly HubService hub;
    private readonly HubOptions options;
    private readonly TunnelWorker tunnels;
    public bool RequiresLogin => !options.UsesDevTunnelAuthentication;
    public const string CookieName = "dashboard-session";
    public UiAuthentication(HubOptions options, HubService hub, TunnelWorker tunnels)
    {
        options.ValidateAuthentication();
        this.options = options; this.tunnels = tunnels; this.hub = hub;
        if (!RequiresLogin) { hub.ConfigureUiCredential(null); return; }
        var file = Path.Combine(options.DataDirectory, "ui-access-key");
        var key = Environment.GetEnvironmentVariable("DASHBOARD_ACCESS_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!File.Exists(file)) Credentials.WritePrivate(file, Credentials.Token());
            key = File.ReadAllText(file).Trim();
        }
        if (key.Length < 24) throw new InvalidDataException("Dashboard access key must be at least 24 characters");
        keyHash = Credentials.Hash(key);
        hub.ConfigureUiCredential(keyHash);
    }
    public bool Login(string? input) => keyHash is not null && Credentials.Matches(keyHash, input) && hub.UiCredentialMatches(keyHash);
    public string Issue()
    {
        if (!RequiresLogin) throw new InvalidOperationException("Sign-in is managed by the private Dev Tunnel");
        return hub.CreateUiSession(keyHash!);
    }
    public void Logout(HttpRequest request)
    {
        if (request.Cookies.TryGetValue(CookieName, out var cookie) && cookie.Length == 43) hub.RevokeUiSession(cookie);
    }
    public bool Authorized(HttpRequest request)
    {
        if (!RequiresLogin) return DevTunnelAccess.Allows(request, options, tunnels.Ui);
        var bearer = request.Headers.Authorization.ToString();
        if (bearer.StartsWith("Bearer ", StringComparison.Ordinal) && Login(bearer[7..])) return true;
        if (!request.Cookies.TryGetValue(CookieName, out var cookie)) return false;
        return cookie.Length == 43 && hub.UiSessionValid(keyHash!, cookie);
    }
}
