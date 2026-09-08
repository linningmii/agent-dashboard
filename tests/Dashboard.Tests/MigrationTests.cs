using System.Text.Json;
using Dashboard.Contracts;
using Dashboard.Core;
using Xunit;

namespace Dashboard.Tests;

public sealed class MigrationTests
{
    [Fact] public void LegacyImportPreservesIdsCredentialsSettingsAndAcknowledgementsOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dashboard-import-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var token = Credentials.Token();
            var devices = new { local = new { id = "original", name = "Windows" }, devices = new Dictionary<string, object> {
                ["remote"] = new { name = "Linux", tokenHash = Credentials.Hash(token), revoked = false, sessionId = "session", sequence = 4, lastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    tasks = Array.Empty<object>(), sources = new { codex = new { available = true, automatic = true, detail = "" } }, seenCompletions = new[] { "cleared-event" } } }, completions = Array.Empty<object>() };
            var legacy = new { settings = new { minimumRunning = 5, reminderCooldownMinutes = 30, windowsNotifications = true }, tasks = Array.Empty<object>(), completions = new[] {
                new { id = "unread", taskId = "t", source = "codex", title = "Preserved", completedAt = DateTimeOffset.UtcNow.ToString("O") } } };
            File.WriteAllText(Path.Combine(dir, "devices.json"), JsonSerializer.Serialize(devices)); File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(legacy));
            var store = new SqliteStateStore<HubState>(Path.Combine(dir, "hub.sqlite")); LegacyImporter.Import(store, dir);
            var hub = new HubService(store, TimeProvider.System); Assert.Equal(5, hub.Settings().MinimumRunning); Assert.Equal("unread", hub.Snapshot().Completions.Single().Id);
            hub.OpenSession("remote", token); hub.Clear("unread"); LegacyImporter.Import(store, dir); Assert.Empty(hub.Snapshot().Completions);
            Assert.True(File.Exists(Path.Combine(dir, "state.json"))); Assert.True(store.Read(s => s.Devices["remote"].SeenCompletions.Contains("cleared-event")));
            var enrolled = LegacyImporter.EnrollOriginalHost(store, "Windows"); Assert.Equal("original", enrolled.DeviceId);
        }
        finally { Directory.Delete(dir, true); }
    }
}
