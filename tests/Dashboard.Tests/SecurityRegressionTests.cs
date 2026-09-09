using System.Net;
using Dashboard.Api;
using Dashboard.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dashboard.Tests;

public sealed class SecurityRegressionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dashboard-security-" + Guid.NewGuid());
    private readonly FakeClock clock = new();
    private HubService Hub() => new(new(Path.Combine(directory, "hub.sqlite")), clock);
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact] public void LogoutRevokesOnlyThatSessionAndSurvivesRestart()
    {
        var hub = Hub(); var credential = Credentials.Hash(Credentials.Token()); hub.ConfigureUiCredential(credential);
        var first = hub.CreateUiSession(credential); var second = hub.CreateUiSession(credential);
        hub.RevokeUiSession(first);
        Assert.False(Hub().UiSessionValid(credential, first));
        Assert.True(Hub().UiSessionValid(credential, second));
        var state = new SqliteStateStore<HubState>(Path.Combine(directory, "hub.sqlite"));
        Assert.True(state.Read(s => s.UiSessions.ContainsKey(Credentials.Hash(second))));
        Assert.False(state.Read(s => s.UiSessions.ContainsKey(second)));
    }

    [Fact] public void RotationInvalidatesAllSessionsAndOldInstancesCannotIssueMore()
    {
        var old = Credentials.Hash(Credentials.Token()); var current = Credentials.Hash(Credentials.Token());
        var hub = Hub(); hub.ConfigureUiCredential(old); var cookie = hub.CreateUiSession(old);
        var restarted = Hub(); restarted.ConfigureUiCredential(old);
        Assert.True(restarted.UiSessionValid(old, cookie));
        restarted.ConfigureUiCredential(current);
        Assert.False(restarted.UiSessionValid(current, cookie));
        Assert.False(hub.UiSessionValid(old, cookie)); Assert.False(hub.UiCredentialMatches(old));
        Assert.Throws<DomainException>(() => hub.CreateUiSession(old));
        var fresh = restarted.CreateUiSession(current); Assert.True(restarted.UiSessionValid(current, fresh));
        restarted.ConfigureUiCredential(old); Assert.False(restarted.UiSessionValid(old, cookie));
        restarted.ConfigureUiCredential(null); Assert.False(restarted.UiSessionValid(current, fresh));
    }

    [Fact] public void ExpiredAndLegacyStatelessCookiesAreRejected()
    {
        var hash = Credentials.Hash(Credentials.Token()); var hub = Hub(); hub.ConfigureUiCredential(hash);
        var token = hub.CreateUiSession(hash); clock.Now = clock.Now.AddDays(7);
        Assert.False(hub.UiSessionValid(hash, token));
        Assert.False(hub.UiSessionValid(hash, "9999999999.old-token.old-signature"));
        hub.CreateUiSession(hash);
        Assert.Equal(1, new SqliteStateStore<HubState>(Path.Combine(directory, "hub.sqlite")).Read(s => s.UiSessions.Count));
    }

    [Fact] public void RateLimitUsesPeerAddressAndIgnoresForwardedHeaders()
    {
        var a = new DefaultHttpContext(); a.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        var b = new DefaultHttpContext(); b.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
        var original = AuthenticationRateLimits.ClientKey(a);
        a.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        Assert.Equal(original, AuthenticationRateLimits.ClientKey(a));
        Assert.NotEqual(original, AuthenticationRateLimits.ClientKey(b));
        a.Connection.RemoteIpAddress = a.Connection.RemoteIpAddress.MapToIPv6();
        Assert.Equal(original, AuthenticationRateLimits.ClientKey(a));
    }

    [Fact] public void PackagedNativeSqliteMeetsPatchedVersionFloor()
    {
        using var db = new SqliteConnection("Data Source=:memory:"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "select sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), "Unpatched native SQLite: " + version);
    }
}
