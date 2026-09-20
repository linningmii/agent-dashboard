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

    [Fact] public void HeartbeatsMaintainIdleDeviceAndExpireAtExactDeadline()
    {
        var d = Enroll(); var s = hub.OpenSession(d.DeviceId, d.Token);
        Assert.Equal(DeviceStatus.Pending, hub.Snapshot().Devices.Single().Status);
        var first = hub.Heartbeat(d.DeviceId, d.Token, new(s.SessionId, 1));
        var view = hub.Snapshot().Devices.Single();
        Assert.Equal(DeviceStatus.Online, view.Status); Assert.Null(view.LastReportAt); Assert.True(view.TaskDataStale);
        Assert.Equal(clock.Now, first.LastSeenAt); Assert.Equal(0, hub.Snapshot().RunningCount);
        clock.Now = clock.Now.AddSeconds(44); Assert.Equal(DeviceStatus.Online, hub.Snapshot().Devices.Single().Status);
        clock.Now = clock.Now.AddSeconds(1); Assert.Equal(DeviceStatus.Offline, hub.Snapshot().Devices.Single().Status);
        hub.Heartbeat(d.DeviceId, d.Token, new(s.SessionId, 2));
        Assert.Equal(DeviceStatus.Online, hub.Snapshot().Devices.Single().Status);
        Assert.Null(hub.Snapshot().Devices.Single().LastReportAt); Assert.Empty(hub.Snapshot().Completions);
    }

    [Fact] public void HeartbeatCannotRefreshStaleTaskDataOrReviveOldTasks()
    {
        var d = Enroll(); var s = hub.OpenSession(d.DeviceId, d.Token);
        hub.Report(d.DeviceId, d.Token, Report(s.SessionId)); var reportedAt = clock.Now;
        clock.Now = clock.Now.AddSeconds(46); hub.Heartbeat(d.DeviceId, d.Token, new(s.SessionId, 1));
        var snapshot = hub.Snapshot();
        Assert.Equal(DeviceStatus.Online, snapshot.Devices.Single().Status);
        Assert.True(snapshot.Devices.Single().TaskDataStale); Assert.Equal(reportedAt, snapshot.Devices.Single().LastReportAt);
        Assert.Equal(0, snapshot.RunningCount); Assert.Equal(TaskStatus.Stale, snapshot.Tasks.Single().Status);
        Assert.Empty(snapshot.Completions);
        hub.Report(d.DeviceId, d.Token, Report(s.SessionId, 2));
        Assert.Equal(1, hub.Snapshot().RunningCount); Assert.False(hub.Snapshot().Devices.Single().TaskDataStale);
    }

    [Fact] public void HeartbeatsAreScopedFencedAndReplaySafeAcrossRestart()
    {
        var a = Enroll(); var b = Enroll(); var s = hub.OpenSession(a.DeviceId, a.Token);
        Assert.Equal(401, Assert.Throws<DomainException>(() => hub.Heartbeat(a.DeviceId, b.Token, new(s.SessionId, 1))).Status);
        Assert.Equal(400, Assert.Throws<DomainException>(() => hub.Heartbeat(a.DeviceId, a.Token, new(s.SessionId, 0))).Status);
        hub.Heartbeat(a.DeviceId, a.Token, new(s.SessionId, 2)); var seen = clock.Now;
        clock.Now = clock.Now.AddSeconds(46);
        var restarted = new HubService(new(Path.Combine(directory, "hub.sqlite")), clock);
        var duplicate = restarted.Heartbeat(a.DeviceId, a.Token, new(s.SessionId, 2));
        Assert.True(duplicate.Duplicate); Assert.Equal(seen, duplicate.LastSeenAt);
        Assert.Equal(DeviceStatus.Offline, restarted.Snapshot().Devices.Single(d => d.Id == a.DeviceId).Status);
        Assert.Equal(409, Assert.Throws<DomainException>(() => restarted.Heartbeat(a.DeviceId, a.Token, new(s.SessionId, 1))).Status);
        restarted.OpenSession(a.DeviceId, a.Token);
        Assert.Equal(409, Assert.Throws<DomainException>(() => restarted.Heartbeat(a.DeviceId, a.Token, new(s.SessionId, 3))).Status);
        hub.Revoke(a.DeviceId);
        Assert.Equal(401, Assert.Throws<DomainException>(() => hub.Heartbeat(a.DeviceId, a.Token, new(s.SessionId, 4))).Status);
    }

    [Fact] public void LegacyReportFreshnessSurvivesFirstHeartbeatAndSessionReplacement()
    {
        var d = Enroll(); var s = hub.OpenSession(d.DeviceId, d.Token); hub.Report(d.DeviceId, d.Token, Report(s.SessionId));
        var reported = clock.Now; store.Update(state => { state.Devices[d.DeviceId].LastReportAt = null; return true; });
        clock.Now = clock.Now.AddSeconds(46); var replacement = hub.OpenSession(d.DeviceId, d.Token);
        hub.Heartbeat(d.DeviceId, d.Token, new(replacement.SessionId, 1));
        Assert.Equal(reported, hub.Snapshot().Devices.Single().LastReportAt); Assert.Equal(0, hub.Snapshot().RunningCount);
    }
}
