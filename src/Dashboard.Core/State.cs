using Dashboard.Contracts;

namespace Dashboard.Core;

public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? TokenHash { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool Revoked { get; set; }
    public string? SessionId { get; set; }
    public long Sequence { get; set; }
    public Dictionary<string, SourceInfo> Sources { get; set; } = [];
    public List<AgentTask> Tasks { get; set; } = [];
    public HashSet<string> SeenCompletions { get; set; } = [];
}
public record PairingRecord(string Hash, DateTimeOffset ExpiresAt);
public sealed class HubState
{
    public Settings Settings { get; set; } = new();
    public Dictionary<string, DeviceRecord> Devices { get; set; } = [];
    public List<PairingRecord> Pairings { get; set; } = [];
    public List<Completion> Completions { get; set; } = [];
    public List<AgentTask> ManualTasks { get; set; } = [];
    public string? LegacyLocalDeviceId { get; set; }
    public bool LegacyImported { get; set; }
    public string CookieKey { get; set; } = Credentials.Token();
}
public sealed class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
