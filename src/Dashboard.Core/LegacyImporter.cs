using System.Text.Json;
using Dashboard.Contracts;

namespace Dashboard.Core;

public static class LegacyImporter
{
    public static void Import(SqliteStateStore<HubState> store, string directory)
    {
        store.Update(state =>
        {
            if (state.LegacyImported) return false;
            var devicesFile = Path.Combine(directory, "devices.json"); var tasksFile = Path.Combine(directory, "state.json");
            if (File.Exists(devicesFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(devicesFile)); var root = document.RootElement;
                if (root.TryGetProperty("local", out var local))
                {
                    var id = local.GetProperty("id").GetString()!; state.LegacyLocalDeviceId = id;
                    state.Devices[id] = new() { Id = id, Name = local.GetProperty("name").GetString() ?? "Original host" };
                }
                foreach (var property in root.GetProperty("devices").EnumerateObject())
                {
                    var item = property.Value;
                    var device = new DeviceRecord { Id = property.Name, Name = item.GetProperty("name").GetString()!,
                        TokenHash = item.GetProperty("tokenHash").GetString(), Revoked = item.GetProperty("revoked").GetBoolean(),
                        SessionId = item.GetProperty("sessionId").GetString(), Sequence = item.GetProperty("sequence").GetInt64(),
                        LastSeenAt = item.GetProperty("lastSeenAt").ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("lastSeenAt").GetInt64()) : null,
                        Tasks = item.GetProperty("tasks").Deserialize<List<AgentTask>>(Protocol.Json) ?? [],
                        Sources = item.GetProperty("sources").Deserialize<Dictionary<string, SourceInfo>>(Protocol.Json) ?? [],
                        SeenCompletions = item.GetProperty("seenCompletions").Deserialize<HashSet<string>>(Protocol.Json) ?? [] };
                    state.Devices[device.Id] = device;
                }
                state.Completions.AddRange(root.GetProperty("completions").Deserialize<List<Completion>>(Protocol.Json) ?? []);
            }
            if (File.Exists(tasksFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(tasksFile)); var root = document.RootElement;
                state.Settings = root.GetProperty("settings").Deserialize<Settings>(Protocol.Json) ?? new();
                state.ManualTasks = (root.GetProperty("tasks").Deserialize<List<AgentTask>>(Protocol.Json) ?? []).Select(task => task with { DeviceId = state.LegacyLocalDeviceId ?? HubService.ManualDeviceId, ManagedLocally = true }).ToList();
                state.Completions.AddRange((root.GetProperty("completions").Deserialize<List<Completion>>(Protocol.Json) ?? []).Select(item => item with { DeviceId = state.LegacyLocalDeviceId }));
            }
            state.Completions = state.Completions.DistinctBy(item => item.Id).ToList();
            state.LegacyImported = true; return true;
        });
    }
    // Called locally by the migration command to preserve the host's device ID, not exposed over HTTP.
    public static EnrollmentResponse EnrollOriginalHost(SqliteStateStore<HubState> store, string name) => store.Update(state =>
    {
        var id = state.LegacyLocalDeviceId ?? Protocol.Id(); var token = Credentials.Token();
        state.Devices[id] = new DeviceRecord { Id = id, Name = name, TokenHash = Credentials.Hash(token) };
        state.LegacyLocalDeviceId = id;
        return new EnrollmentResponse(id, token);
    });
}
