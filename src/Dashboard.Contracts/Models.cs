using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dashboard.Contracts;

public static class Protocol
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static string Id() => Guid.NewGuid().ToString();
}

public enum TaskStatus { Running, Completed, Paused, Stale, Interrupted, Unknown }
public enum Confidence { Automatic, Reported, Estimated }
public enum DeviceStatus { Pending, Online, Offline }
public record SourceInfo(bool Available = false, string Detail = "", bool Automatic = false);
public record AgentTask
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Workspace { get; init; } = "";
    public TaskStatus Status { get; init; } = TaskStatus.Running;
    public Confidence Confidence { get; init; } = Confidence.Automatic;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string LatestOutput { get; init; } = "";
    public DateTimeOffset? LatestOutputAt { get; init; }
    public string? Model { get; init; }
    public string? ExternalId { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public bool ManagedLocally { get; init; }
}
public record Completion : AgentTask
{
    public string TaskId { get; init; } = "";
    public DateTimeOffset CompletedAt { get; init; }
}
public record Settings(int MinimumRunning = 3, double ReminderCooldownMinutes = 15, bool WindowsNotifications = false);
public record SettingsUpdate(int? MinimumRunning = null, double? ReminderCooldownMinutes = null, bool? WindowsNotifications = null);
public record DeviceView(string Id, string Name, DeviceStatus Status, int RunningCount, DateTimeOffset? LastSeenAt, Dictionary<string, SourceInfo> Sources, bool Local = false);
public record Snapshot(int Version, DateTimeOffset GeneratedAt, int RunningCount, int MinimumRunning, int MissingCount, bool Healthy,
    Settings Settings, List<DeviceView> Devices, List<AgentTask> Tasks, List<Completion> Completions, Dictionary<string, SourceInfo> Sources);
public record EnrollmentRequest(string Code, string Name);
public record EnrollmentResponse(string DeviceId, string Token, int HeartbeatSeconds = 10, int OfflineAfterSeconds = 45);
public record SessionResponse(string SessionId, int HeartbeatSeconds = 10);
public record DeviceReport
{
    public int Version { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public long Sequence { get; init; }
    public Dictionary<string, SourceInfo> Sources { get; init; } = [];
    public List<AgentTask> Tasks { get; init; } = [];
    public List<Completion> Completions { get; init; } = [];
}
public record ReportResponse(bool Accepted, long Sequence, bool Duplicate = false);
public record PairingResponse(string Code, DateTimeOffset ExpiresAt, string? IngestionUrl, string? TunnelId);
public record TaskInput(string Source, string Title, string? Workspace = null, string? LatestOutput = null, double LeaseMinutes = 240, string? DeviceId = null);
public record TaskUpdate(TaskStatus? Status = null, string? Title = null, string? LatestOutput = null, double LeaseMinutes = 240);
public record HeartbeatInput(double LeaseMinutes = 5, string? LatestOutput = null);
public record RemovedResponse(int Removed);
public record RevokedResponse(bool Revoked);
public record ErrorResponse(string Error);
public record HealthResponse(string Service, int Version = 1);
public record Reminder(string Id, DateTimeOffset CreatedAt, int RunningCount, int MinimumRunning, string Message);
public record TunnelOptions(bool Enabled = false, string? Id = null, int Port = 0);
public record TunnelState(bool Enabled, string State, string? Id = null, string? Url = null, string? Error = null);
public record TunnelStates(TunnelState Ui, TunnelState Ingestion);
public record CollectorConfig(string Url, string DeviceId, string Token, string Name, string? TunnelId = null);
public record AuthInfo(bool Authenticated, bool RequiresLogin);
public record LoginInput(string AccessKey);
