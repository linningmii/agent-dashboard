using System.Text.Json;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.Data.Sqlite;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Adapters;

public sealed class CodexAdapter(AdapterPaths paths) : IAgentAdapter
{
    private readonly Dictionary<string, (DateTime Modified, long Size, List<AgentTask> Tasks, List<Completion> Completed)> cache = [];
    public AdapterResult Collect()
    {
        if (!File.Exists(paths.CodexState)) return new("codex", new(false, "Codex state database not found"), [], []);
        try
        {
            var names = new Dictionary<string, string>();
            if (File.Exists(paths.CodexIndex)) foreach (var item in JsonLines.Read(paths.CodexIndex))
                if (item.Text("id") is { } id && item.Text("thread_name") is { Length: > 0 } name) names[id] = name;
            using var state = OpenRead(paths.CodexState);
            var rows = Query(state, "SELECT * FROM threads WHERE archived=0 ORDER BY updated_at DESC");
            var tasks = new Dictionary<string, AgentTask>(); var completed = new Dictionary<string, Completion>();
            using var history = File.Exists(paths.CodexHistory) ? OpenRead(paths.CodexHistory) : null;
            foreach (var row in rows)
            {
                var id = Value(row, "id"); var title = names.GetValueOrDefault(id) ?? (Value(row, "name") is { Length: > 0 } n ? n : Value(row, "title"));
                if (title.Length == 0) title = "Codex task";
                var template = new AgentTask { Id = id, ExternalId = id, Source = "codex", Title = JsonLines.Clip(title),
                    Workspace = NormalizePath(Value(row, "cwd")), Model = Value(row, "model"), Confidence = Confidence.Automatic };
                var hadProjection = false;
                if (history is not null)
                {
                    var latest = true;
                    foreach (var turn in Query(history, "SELECT * FROM thread_turns WHERE thread_id=$id ORDER BY started_at DESC LIMIT 30", ("$id", id)))
                    {
                        hadProjection = true;
                        var turnId = Value(turn, "turn_id"); var status = Value(turn, "status");
                        var start = Seconds(turn.GetValueOrDefault("started_at"));
                        var task = template with { Id = "codex:" + id + ":" + turnId, StartedAt = start };
                        var output = Query(history, "SELECT item_json,created_at_ms FROM thread_items WHERE thread_id=$id AND turn_id=$turn AND item_type='agentMessage' ORDER BY created_at_ms DESC,rollout_ordinal DESC LIMIT 1", ("$id", id), ("$turn", turnId)).FirstOrDefault();
                        if (output is not null)
                        {
                            try { using var doc = JsonDocument.Parse(Value(output, "item_json")); task = task with { LatestOutput = JsonLines.Clip(JsonLines.Message(doc.RootElement)), LatestOutputAt = Milliseconds(output.GetValueOrDefault("created_at_ms")) }; } catch (JsonException) { }
                        }
                        if (status == "inProgress" && latest) tasks[task.Id] = task;
                        else if (status == "completed") completed[task.Id] = HubService.ToCompletion(task, task.Id + ":complete", Seconds(turn.GetValueOrDefault("completed_at")) ?? start ?? DateTimeOffset.UtcNow);
                        latest = false;
                    }
                }
                if (hadProjection) continue;
                var file = NormalizePath(Value(row, "rollout_path")); if (!File.Exists(file)) continue;
                var stat = new FileInfo(file);
                if (!cache.TryGetValue(file, out var cached) || cached.Modified != stat.LastWriteTimeUtc || cached.Size != stat.Length)
                {
                    var parsed = ParseRollout(file, template);
                    cached = (stat.LastWriteTimeUtc, stat.Length, parsed.Tasks, parsed.Completions); cache[file] = cached;
                }
                foreach (var task in cached.Tasks) tasks[task.Id] = task with { Title = template.Title,
                    Status = DateTime.UtcNow - stat.LastWriteTimeUtc > TimeSpan.FromHours(24) ? TaskStatus.Stale : task.Status };
                foreach (var item in cached.Completed.TakeLast(30)) completed[item.TaskId] = item with { Title = template.Title };
            }
            return new("codex", new(true, $"{tasks.Values.Count(task => task.Status == TaskStatus.Running)} active turns", true), tasks.Values.ToList(), completed.Values.ToList());
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return new("codex", new(false, "Codex storage unavailable or schema changed: " + e.GetType().Name), [], []); }
    }
    public static AdapterResult ParseRollout(string file, AgentTask template)
    {
        var active = new Dictionary<string, AgentTask>(); var completed = new List<Completion>(); string? current = null;
        foreach (var item in JsonLines.Read(file))
        {
            var payload = item.Child("payload"); var type = payload.Text("type");
            if (item.Text("type") == "event_msg" && type == "task_started" && payload.Text("turn_id") is { } turn)
            {
                // A thread has one current turn. Older starts without terminal events are interrupted history.
                active.Clear();
                current = turn; active[turn] = template with { Id = "codex:" + template.Id + ":" + turn,
                    StartedAt = payload.Number("started_at") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : item.Time("timestamp") };
            }
            if (current is not null && active.TryGetValue(current, out var task))
            {
                var output = item.Text("type") == "response_item" && type == "message" && payload.Text("role") == "assistant" && payload.Text("phase") != "analysis" ? JsonLines.Message(payload) :
                    item.Text("type") == "event_msg" && type == "agent_message" ? payload.Text("message") ?? "" : "";
                if (output.Length > 0) active[current] = task with { LatestOutput = JsonLines.Clip(output), LatestOutputAt = item.Time("timestamp") };
            }
            if (item.Text("type") == "event_msg" && type is "task_complete" or "turn_aborted" && payload.Text("turn_id") is { } ended && active.Remove(ended, out var finished))
            {
                if (type == "task_complete") completed.Add(HubService.ToCompletion(finished with { LatestOutput = JsonLines.Clip(payload.Text("last_agent_message") ?? finished.LatestOutput) }, finished.Id + ":complete", item.Time("timestamp") ?? DateTimeOffset.UtcNow));
                if (current == ended) current = null;
            }
        }
        return new("codex", new(true, "Rollout lifecycle", true), active.Values.ToList(), completed);
    }
    public static string NormalizePath(string value) => value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    private static SqliteConnection OpenRead(string file) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); connection.Open(); return connection; }
    private static List<Dictionary<string, object?>> Query(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
        while (reader.Read()) { var row = new Dictionary<string, object?>(); for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i); rows.Add(row); }
        return rows;
    }
    private static string Value(Dictionary<string, object?> row, string key) => row.GetValueOrDefault(key)?.ToString() ?? "";
    private static DateTimeOffset? Seconds(object? value) => value is long number ? DateTimeOffset.FromUnixTimeSeconds(number) : null;
    private static DateTimeOffset? Milliseconds(object? value) => value is long number ? DateTimeOffset.FromUnixTimeMilliseconds(number) : null;
}
