using System.Text.RegularExpressions;
using Dashboard.Contracts;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Core;

public sealed partial class HubService(SqliteStateStore<HubState> store, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    public const int LeaseSeconds = 45;
    public const string ManualDeviceId = "manual";
    public void ConfigureUiCredential(string? hash) => store.Update(state =>
    {
        if (state.UiCredentialHash != hash) state.UiSessions.Clear();
        state.UiCredentialHash = hash;
        return true;
    });
    public bool UiCredentialMatches(string hash) => store.Read(state => state.UiCredentialHash == hash);
    public string CreateUiSession(string hash) => store.Update(state =>
    {
        Require(state.UiCredentialHash == hash, "Dashboard credentials changed; restart this API", 401);
        foreach (var expired in state.UiSessions.Where(pair => pair.Value <= Now).Select(pair => pair.Key).ToArray()) state.UiSessions.Remove(expired);
        Require(state.UiSessions.Count < 1024, "Too many dashboard sessions", 429);
        var token = Credentials.Token();
        state.UiSessions[Credentials.Hash(token)] = Now.AddDays(7);
        return token;
    });
    public bool UiSessionValid(string hash, string token) => store.Read(state => state.UiCredentialHash == hash &&
        state.UiSessions.TryGetValue(Credentials.Hash(token), out var expires) && expires > Now);
    public void RevokeUiSession(string token) => store.Update(state => state.UiSessions.Remove(Credentials.Hash(token)));
    public static string TaskKey(string device, string task) => "device:" + device + ":" + Uri.EscapeDataString(task);
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex SourcePattern();
    private static void Require(bool valid, string message, int status = 400) { if (!valid) throw new DomainException(status, message); }
    public static string Text(string? value, int max, string label, bool required = false)
    {
        Require((!required || !string.IsNullOrWhiteSpace(value)) && (value?.Length ?? 0) <= max, "Invalid " + label);
        return value?.Trim() ?? "";
    }
    public Settings Settings() => store.Read(state => state.Settings);
    public Settings UpdateSettings(SettingsUpdate input) => store.Update(state =>
    {
        Require(input.MinimumRunning is null or >= 1 and <= 20, "Minimum must be 1–20");
        Require(input.ReminderCooldownMinutes is null or >= 1 and <= 1440, "Reminder interval must be 1–1440 minutes");
        return state.Settings = new(input.MinimumRunning ?? state.Settings.MinimumRunning,
            input.ReminderCooldownMinutes ?? state.Settings.ReminderCooldownMinutes, input.WindowsNotifications ?? state.Settings.WindowsNotifications);
    });
    public PairingResponse Pair(string? url = null, string? tunnel = null) => store.Update(state =>
    {
        var code = Credentials.Token(); var expiry = Now.AddMinutes(10);
        state.Pairings.RemoveAll(pair => pair.ExpiresAt <= Now);
        Require(state.Pairings.Count < 20, "Too many outstanding pairing codes", 429);
        state.Pairings.Add(new(Credentials.Hash(code), expiry));
        return new PairingResponse(code, expiry, url, tunnel);
    });
    public EnrollmentResponse Enroll(EnrollmentRequest input) => store.Update(state =>
    {
        var name = Text(input.Name, 100, "device name", true);
        var code = Text(input.Code, 100, "pairing code", true);
        var pair = state.Pairings.Find(pair => pair.ExpiresAt > Now && Credentials.Matches(pair.Hash, code));
        Require(pair is not null, "Invalid or expired pairing code", 401);
        var id = Protocol.Id(); var token = Credentials.Token();
        state.Pairings.Remove(pair!);
        state.Devices.Add(id, new DeviceRecord { Id = id, Name = name, TokenHash = Credentials.Hash(token) });
        return new EnrollmentResponse(id, token);
    });
    private static DeviceRecord Authorized(HubState state, string id, string? token)
    {
        Require(state.Devices.TryGetValue(id, out var device) && !device.Revoked && Credentials.Matches(device.TokenHash, token), "Invalid device credentials", 401);
        return device!;
    }
    public void Authenticate(string id, string? token) => store.Read(state => Authorized(state, id, token));
    public SessionResponse OpenSession(string id, string? token) => store.Update(state =>
    {
        var device = Authorized(state, id, token); device.SessionId = Protocol.Id(); device.Sequence = 0;
        return new SessionResponse(device.SessionId);
    });
    public ReportResponse Report(string id, string? token, DeviceReport report) => store.Update(state =>
    {
        var device = Authorized(state, id, token);
        ValidateReport(report);
        Require(device.SessionId is not null && device.SessionId == report.SessionId, "Collector session replaced; restart collector", 409);
        Require(report.Sequence >= device.Sequence, "Out-of-order report", 409);
        if (report.Sequence == device.Sequence) return new ReportResponse(true, device.Sequence, true);
        foreach (var item in report.Completions)
        {
            if (!device.SeenCompletions.Add(item.Id)) continue;
            state.Completions.Add(item with { Id = TaskKey(id, "completion:" + item.Id), TaskId = TaskKey(id, item.TaskId),
                DeviceId = id, DeviceName = device.Name, ManagedLocally = false });
        }
        // No unread notification is discarded silently; acknowledged IDs remain separately for replay safety.
        device.Tasks = report.Tasks.Select(task => task with { DeviceId = null, DeviceName = null, ManagedLocally = false }).ToList();
        device.Sources = report.Sources; device.Sequence = report.Sequence; device.LastSeenAt = Now;
        return new ReportResponse(true, report.Sequence);
    });
    public static void ValidateReport(DeviceReport report)
    {
        Require(report.Version == Protocol.Version, "Unsupported protocol version");
        Text(report.SessionId, 100, "session ID", true);
        Require(report.Sequence >= 1 && report.Sequence <= 9007199254740991, "Invalid sequence");
        Require(report.Sources is { Count: <= 20 } && report.Tasks is { Count: <= 500 } && report.Completions is { Count: <= 100 }, "Invalid report collections");
        foreach (var (source, info) in report.Sources!)
        {
            Require(SourcePattern().IsMatch(source) && info is not null, "Invalid source");
            Text(info!.Detail, 500, "source detail");
        }
        foreach (var task in report.Tasks!) ValidateTask(task, report.Sources);
        Require(report.Tasks.Select(task => task.Id).Distinct().Count() == report.Tasks.Count, "Duplicate task IDs");
        foreach (var item in report.Completions!)
        {
            ValidateTask(item, report.Sources); Text(item.TaskId, 300, "completion task ID", true);
            Require(item.CompletedAt != default, "Missing completion timestamp");
        }
    }
    private static void ValidateTask(AgentTask? task, Dictionary<string, SourceInfo> sources)
    {
        Require(task is not null, "Invalid task");
        Text(task!.Id, 300, "task ID", true); Text(task.Title, 2000, "task title", true);
        Require(task.Source is not null && sources.ContainsKey(task.Source) && Enum.IsDefined(task.Status) && Enum.IsDefined(task.Confidence), "Invalid task source or status");
        Text(task.Workspace, 1000, "workspace"); Text(task.LatestOutput, 4000, "output"); Text(task.Model, 100, "model");
    }
    public RevokedResponse Revoke(string id) => store.Update(state =>
    {
        Require(state.Devices.TryGetValue(id, out var device), "Device not found", 404);
        device!.Revoked = true; device.TokenHash = null; device.Tasks.Clear(); device.SessionId = null;
        return new RevokedResponse(true);
    });
    public RemovedResponse Clear(string? id) => store.Update(state =>
        new RemovedResponse(state.Completions.RemoveAll(item => id is null || item.Id == id)));
    private string ManualDevice(HubState state, string? input)
    {
        if (input is null or ManualDeviceId) return ManualDeviceId;
        Require(state.Devices.TryGetValue(input, out var device) && !device.Revoked, "Device not found", 404);
        return input;
    }
    public AgentTask CreateTask(TaskInput input) => store.Update(state =>
    {
        Require(input.Source is not null && SourcePattern().IsMatch(input.Source), "Invalid source");
        Require(input.LeaseMinutes is >= 1 and <= 1440, "Invalid lease");
        var task = new AgentTask { Id = Protocol.Id(), Source = input.Source!, Title = Text(input.Title, 2000, "title", true),
            Workspace = Text(input.Workspace, 1000, "workspace"), LatestOutput = Text(input.LatestOutput, 4000, "output"),
            StartedAt = Now, CreatedAt = Now, UpdatedAt = Now, ExpiresAt = Now.AddMinutes(input.LeaseMinutes),
            LatestOutputAt = input.LatestOutput is null ? null : Now, DeviceId = ManualDevice(state, input.DeviceId),
            Confidence = Confidence.Reported, ManagedLocally = true };
        state.ManualTasks.Add(task); return task;
    });
    public AgentTask UpdateTask(string id, TaskUpdate input) => store.Update(state =>
    {
        var index = state.ManualTasks.FindIndex(task => task.Id == id); Require(index >= 0, "Reported task not found", 404);
        var previous = state.ManualTasks[index]; var status = input.Status ?? previous.Status;
        Require(Enum.IsDefined(status) && input.LeaseMinutes is >= 1 and <= 1440, "Invalid task update");
        var task = previous with { Status = status, UpdatedAt = Now, Title = input.Title is null ? previous.Title : Text(input.Title, 2000, "title", true),
            LatestOutput = input.LatestOutput is null ? previous.LatestOutput : Text(input.LatestOutput, 4000, "output"),
            LatestOutputAt = input.LatestOutput is null ? previous.LatestOutputAt : Now,
            ExpiresAt = status == TaskStatus.Running ? Now.AddMinutes(input.LeaseMinutes) : previous.ExpiresAt };
        state.ManualTasks[index] = task;
        if (status == TaskStatus.Completed && previous.Status != TaskStatus.Completed)
            state.Completions.Add(ToCompletion(task, Protocol.Id(), Now));
        return task;
    });
    public static Completion ToCompletion(AgentTask task, string id, DateTimeOffset completedAt) => new()
    {
        Id = id, TaskId = task.Id, Source = task.Source, Title = task.Title, Workspace = task.Workspace,
        StartedAt = task.StartedAt ?? task.CreatedAt, CompletedAt = completedAt, LatestOutput = task.LatestOutput,
        LatestOutputAt = task.LatestOutputAt, Confidence = task.Confidence, DeviceId = task.DeviceId,
        DeviceName = task.DeviceName, ManagedLocally = task.ManagedLocally, Status = TaskStatus.Completed
    };
    public Snapshot Snapshot() => store.Read<Snapshot>(state =>
    {
        var devices = new List<DeviceView>(); var tasks = new List<AgentTask>();
        foreach (var device in state.Devices.Values.Where(device => !device.Revoked))
        {
            var online = device.LastSeenAt is not null && Now - device.LastSeenAt < TimeSpan.FromSeconds(LeaseSeconds);
            var deviceTasks = device.Tasks.Select(task => task with { Id = TaskKey(device.Id, task.Id), DeviceId = device.Id, DeviceName = device.Name,
                Status = !online || (task.Confidence != Confidence.Reported && !device.Sources.GetValueOrDefault(task.Source, new()).Available) ? TaskStatus.Stale : task.Status,
                ManagedLocally = false }).ToList();
            tasks.AddRange(deviceTasks);
            devices.Add(new(device.Id, device.Name, device.LastSeenAt is null ? DeviceStatus.Pending : online ? DeviceStatus.Online : DeviceStatus.Offline,
                deviceTasks.Count(task => task.Status == TaskStatus.Running), device.LastSeenAt, device.Sources));
        }
        foreach (var task in state.ManualTasks)
        {
            var device = devices.Find(device => device.Id == task.DeviceId);
            tasks.Add(task with { DeviceName = device?.Name ?? "Manually tracked", DeviceId = task.DeviceId ?? ManualDeviceId,
                Status = task.Status == TaskStatus.Running && ((task.ExpiresAt is not null && task.ExpiresAt <= Now) ||
                    (task.DeviceId is not null and not ManualDeviceId && device?.Status != DeviceStatus.Online)) ? TaskStatus.Stale : task.Status });
        }
        if (state.ManualTasks.Any(task => task.DeviceId is null or ManualDeviceId))
            devices.Add(new(ManualDeviceId, "Manually tracked", DeviceStatus.Online, tasks.Count(task => task.DeviceId == ManualDeviceId && task.Status == TaskStatus.Running), Now, [], true));
        devices = devices.Select(device => device with { RunningCount = tasks.Count(task => task.DeviceId == device.Id && task.Status == TaskStatus.Running) }).ToList();
        var sources = new Dictionary<string, SourceInfo>();
        foreach (var source in new[] { "codex", "copilot", "claude" }.Concat(devices.SelectMany(device => device.Sources.Keys)).Concat(tasks.Select(task => task.Source)).Distinct())
        {
            var reporting = devices.Where(device => device.Status == DeviceStatus.Online && device.Sources.GetValueOrDefault(source, new()).Available).ToList();
            sources[source] = new(reporting.Count > 0 || tasks.Any(task => task.Source == source && task.Status == TaskStatus.Running),
                $"{reporting.Count} online device(s) reporting {source}", reporting.Any(device => device.Sources[source].Automatic));
        }
        var count = tasks.Count(task => task.Status == TaskStatus.Running); var minimum = state.Settings.MinimumRunning;
        var completions = state.Completions.Select(item => item with { DeviceName = state.Devices.GetValueOrDefault(item.DeviceId ?? "")?.Name ?? item.DeviceName ?? "Manually tracked" }).OrderByDescending(item => item.CompletedAt).ToList();
        return new(Protocol.Version, Now, count, minimum, Math.Max(minimum - count, 0), count >= minimum, state.Settings, devices, tasks, completions, sources);
    });
}
