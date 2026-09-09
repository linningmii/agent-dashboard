using Dashboard.Contracts;
using Dashboard.Core;
using Xunit;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Tests;

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class HubTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid());
    private readonly FakeClock clock = new();
    private readonly SqliteStateStore<HubState> store;
    private readonly HubService hub;
    public HubTests() { store = new(Path.Combine(directory, "hub.sqlite")); hub = new(store, clock); }
    public void Dispose() => Directory.Delete(directory, true);
    private EnrollmentResponse Enroll(string name = "Device") => hub.Enroll(new(hub.Pair().Code, name));
    private static DeviceReport Report(string session, long sequence = 1) => new()
    {
        SessionId = session, Sequence = sequence, Sources = new() { ["codex"] = new(true, "Test", true) },
        Tasks = [new() { Id = "same-turn", Source = "codex", Title = "Working" }]
    };
    [Fact] public void PairingIsSingleUseAndExpires()
    {
        var code = hub.Pair().Code; hub.Enroll(new(code, "One"));
        Assert.Equal(401, Assert.Throws<DomainException>(() => hub.Enroll(new(code, "Two"))).Status);
        var expired = hub.Pair().Code; clock.Now = clock.Now.AddMinutes(11);
        Assert.Equal(401, Assert.Throws<DomainException>(() => hub.Enroll(new(expired, "Expired"))).Status);
    }
    [Fact] public void DeviceIdentitySeparatesTaskIdsAndLiveness()
    {
        foreach (var name in new[] { "Windows", "macOS", "Linux", "Extra" })
        { var d = Enroll(name); var s = hub.OpenSession(d.DeviceId, d.Token); hub.Report(d.DeviceId, d.Token, Report(s.SessionId)); }
        var snapshot = hub.Snapshot(); Assert.Equal(4, snapshot.RunningCount); Assert.True(snapshot.Healthy);
        Assert.Equal(4, snapshot.Tasks.Select(task => task.Id).Distinct().Count());
        clock.Now = clock.Now.AddSeconds(46); snapshot = hub.Snapshot();
        Assert.Equal(0, snapshot.RunningCount); Assert.False(snapshot.Healthy); Assert.Empty(snapshot.Completions);
        Assert.All(snapshot.Devices, d => Assert.Equal(DeviceStatus.Offline, d.Status));
    }
    [Fact] public void ReplayDoesNotExtendHeartbeatAndSessionsFenceOldClients()
    {
        var d = Enroll(); var s = hub.OpenSession(d.DeviceId, d.Token);
        hub.Report(d.DeviceId, d.Token, Report(s.SessionId, 2)); clock.Now = clock.Now.AddSeconds(46);
        Assert.True(hub.Report(d.DeviceId, d.Token, Report(s.SessionId, 2)).Duplicate);
        Assert.Equal(0, hub.Snapshot().RunningCount);
        Assert.Equal(409, Assert.Throws<DomainException>(() => hub.Report(d.DeviceId, d.Token, Report(s.SessionId))).Status);
        hub.OpenSession(d.DeviceId, d.Token);
        Assert.Equal(409, Assert.Throws<DomainException>(() => hub.Report(d.DeviceId, d.Token, Report(s.SessionId, 3))).Status);
    }
    [Fact] public void ClearRemainsAcknowledgedAfterRetryAndRestart()
    {
        var d = Enroll(); var s = hub.OpenSession(d.DeviceId, d.Token);
        var done = new Completion { Id = "event", TaskId = "same-turn", Source = "codex", Title = "Done", CompletedAt = clock.Now };
        hub.Report(d.DeviceId, d.Token, Report(s.SessionId) with { Completions = [done] });
        hub.Clear(hub.Snapshot().Completions.Single().Id);
        var restarted = new HubService(new(Path.Combine(directory, "hub.sqlite")), clock);
        restarted.Report(d.DeviceId, d.Token, Report(s.SessionId, 2) with { Completions = [done] });
        Assert.Empty(restarted.Snapshot().Completions);
    }
    [Fact] public void CredentialsCannotWriteOtherDevicesAndRevocationIsImmediate()
    {
        var a = Enroll(); var b = Enroll();
        Assert.Equal(401, Assert.Throws<DomainException>(() => hub.OpenSession(b.DeviceId, a.Token)).Status);
        hub.Revoke(a.DeviceId);
        Assert.Equal(401, Assert.Throws<DomainException>(() => hub.OpenSession(a.DeviceId, a.Token)).Status);
    }
    [Fact] public void InvalidReportsAreAtomicAndDoNotEraseValidTasks()
    {
        var d = Enroll(); var s = hub.OpenSession(d.DeviceId, d.Token); hub.Report(d.DeviceId, d.Token, Report(s.SessionId));
        Assert.Throws<DomainException>(() => hub.Report(d.DeviceId, d.Token, Report(s.SessionId, 2) with { Tasks = [new() { Id = "bad", Source = "undeclared", Title = "Bad" }] }));
        Assert.Single(hub.Snapshot().Tasks);
    }
    [Fact] public void ManualTasksExpireAndCompletionIsExplicit()
    {
        var task = hub.CreateTask(new("copilot", "Manual", LeaseMinutes: 1)); Assert.Equal(1, hub.Snapshot().RunningCount);
        clock.Now = clock.Now.AddMinutes(2); Assert.Equal(0, hub.Snapshot().RunningCount); Assert.Empty(hub.Snapshot().Completions);
        hub.UpdateTask(task.Id, new(TaskStatus.Completed, LatestOutput: "Done")); Assert.Single(hub.Snapshot().Completions);
        hub.UpdateTask(task.Id, new(TaskStatus.Completed)); Assert.Single(hub.Snapshot().Completions);
    }
}
