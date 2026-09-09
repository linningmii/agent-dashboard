using System.Security.Cryptography;
using System.Text;
using Dashboard.Core;

namespace Dashboard.Api;

public sealed class UiAuthentication
{
    private readonly string? keyHash;
    private readonly byte[] cookieKey = [];
    private readonly HubOptions options;
    private readonly TunnelWorker tunnels;
    public bool RequiresLogin => !options.UsesDevTunnelAuthentication;
    public const string CookieName = "dashboard-session";
    public UiAuthentication(HubOptions options, HubService hub, TunnelWorker tunnels)
    {
        options.ValidateAuthentication();
        this.options = options; this.tunnels = tunnels;
        if (!RequiresLogin) return;
        var file = Path.Combine(options.DataDirectory, "ui-access-key");
        var key = Environment.GetEnvironmentVariable("DASHBOARD_ACCESS_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!File.Exists(file)) Credentials.WritePrivate(file, Credentials.Token());
            key = File.ReadAllText(file).Trim();
        }
        if (key.Length < 24) throw new InvalidDataException("Dashboard access key must be at least 24 characters");
        keyHash = Credentials.Hash(key); cookieKey = Encoding.UTF8.GetBytes(hub.CookieKey);
    }
    public bool Login(string? input) => Credentials.Matches(keyHash, input);
    public string Issue()
    {
        if (!RequiresLogin) throw new InvalidOperationException("Sign-in is managed by the private Dev Tunnel");
        var value = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds() + "." + Credentials.Token();
        return value + "." + Sign(value);
    }
    private string Sign(string value) => Convert.ToHexStringLower(HMACSHA256.HashData(cookieKey, Encoding.UTF8.GetBytes(value)));
    public bool Authorized(HttpRequest request)
    {
        if (!RequiresLogin) return DevTunnelAccess.Allows(request, options, tunnels.Ui);
        var bearer = request.Headers.Authorization.ToString();
        if (bearer.StartsWith("Bearer ", StringComparison.Ordinal) && Login(bearer[7..])) return true;
        if (!request.Cookies.TryGetValue(CookieName, out var cookie)) return false;
        var parts = cookie.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var expires) || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        var expected = Sign(parts[0] + "." + parts[1]);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2]));
    }
}
