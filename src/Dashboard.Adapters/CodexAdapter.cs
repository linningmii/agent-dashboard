using System.Text.Json;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.Data.Sqlite;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Adapters;

public sealed class CodexAdapter(AdapterPaths paths) : IAgentAdapter
{
    private sealed record RolloutState(AdapterResult Result, string? LatestTaskId, DateTimeOffset? LatestStartedAt);
    private readonly Dictionary<string, (DateTime Modified, long Size, RolloutState State)> cache = [];
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
                AgentTask? latestProjected = null; string? latestProjectedStatus = null;
                var projectedStatuses = new Dictionary<string, string>();
                if (history is not null)
                {
                    var latest = true;
                    foreach (var turn in Query(history, "SELECT * FROM thread_turns WHERE thread_id=$id ORDER BY started_at DESC LIMIT 30", ("$id", id)))
                    {
                        var turnId = Value(turn, "turn_id"); var status = Value(turn, "status");
                        var start = Seconds(turn.GetValueOrDefault("started_at"));
                        var task = template with { Id = "codex:" + id + ":" + turnId, StartedAt = start };
                        projectedStatuses[task.Id] = status;
                        var output = Query(history, "SELECT item_json,created_at_ms FROM thread_items WHERE thread_id=$id AND turn_id=$turn AND item_type='agentMessage' ORDER BY created_at_ms DESC,rollout_ordinal DESC LIMIT 1", ("$id", id), ("$turn", turnId)).FirstOrDefault();
                        if (output is not null)
                        {
                            try { using var doc = JsonDocument.Parse(Value(output, "item_json")); task = task with { LatestOutput = JsonLines.Clip(JsonLines.Message(doc.RootElement)), LatestOutputAt = Milliseconds(output.GetValueOrDefault("created_at_ms")) }; } catch (JsonException) { }
                        }
                        if (latest) { latestProjected = task; latestProjectedStatus = status; }
                        if (status == "inProgress" && latest) tasks[task.Id] = task with { Status = Seconds(row.GetValueOrDefault("updated_at")) is { } updated && DateTimeOffset.UtcNow - updated > TimeSpan.FromHours(24) ? TaskStatus.Stale : TaskStatus.Running };
                        else if (status == "completed") completed[task.Id] = HubService.ToCompletion(task, task.Id + ":complete", Seconds(turn.GetValueOrDefault("completed_at")) ?? start ?? DateTimeOffset.UtcNow);
                        latest = false;
                    }
                }
                var file = NormalizePath(Value(row, "rollout_path")); if (!File.Exists(file)) continue;
                RolloutState rollout; DateTime modified;
                try
                {
                    var stat = new FileInfo(file); modified = stat.LastWriteTimeUtc;
                    if (!cache.TryGetValue(file, out var cached) || cached.Modified != modified || cached.Size != stat.Length)
                    {
                        var parsed = ReadRollout(file, template);
                        cached = (modified, stat.Length, parsed); cache[file] = cached;
                    }
                    rollout = cached.State;
                }
                // Rollout rotation/read failures must not hide other threads or erase
                // valid projected state. Retry the current path at the next poll.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                // History can lag or still describe a previous rollout file. Reconcile
                // explicit lifecycle evidence instead of letting any old projected row
                // disable rollout discovery for the rest of this thread's lifetime.
                var sameTurn = rollout.LatestTaskId == latestProjected?.Id;
                var newerTurn = latestProjected is null || (rollout.LatestStartedAt is { } started &&
                    latestProjected.StartedAt is { } projectedStart && started > projectedStart);
                if (rollout.LatestTaskId is not null && (sameTurn || newerTurn))
                {
                    var active = rollout.Result.Tasks.SingleOrDefault();
                    // A terminal projection must not be resurrected by a lagging start.
                    if (latestProjected is not null && (newerTurn || active is null)) tasks.Remove(latestProjected.Id);
                    if (active is not null && !(sameTurn && latestProjectedStatus != "inProgress"))
                    {
                        if (sameTurn && latestProjected!.LatestOutput.Length > 0 && (active.LatestOutput.Length == 0 ||
                            latestProjected.LatestOutputAt > active.LatestOutputAt))
                            active = active with { LatestOutput = latestProjected.LatestOutput, LatestOutputAt = latestProjected.LatestOutputAt };
                        tasks[active.Id] = active with { Title = template.Title,
                            Status = DateTime.UtcNow - modified > TimeSpan.FromHours(24) &&
                                (latestProjected is null || !sameTurn || DateTimeOffset.UtcNow - Seconds(row.GetValueOrDefault("updated_at")) > TimeSpan.FromHours(24))
                                ? TaskStatus.Stale : TaskStatus.Running };
                    }
                }
                foreach (var item in rollout.Result.Completions.TakeLast(30))
                {
                    if (projectedStatuses.TryGetValue(item.TaskId, out var status) && status is not ("inProgress" or "completed")) continue;
                    // Use the same key as projected completions, including after catch-up.
                    if (!completed.ContainsKey(item.TaskId)) completed[item.TaskId] = item with { Title = template.Title };
                }
            }
            return new("codex", new(true, $"{tasks.Values.Count(task => task.Status == TaskStatus.Running)} active turns", true), tasks.Values.ToList(), completed.Values.ToList());
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return new("codex", new(false, "Codex storage unavailable or schema changed: " + e.GetType().Name), [], []); }
    }
    public static AdapterResult ParseRollout(string file, AgentTask template) => ReadRollout(file, template).Result;
    private static RolloutState ReadRollout(string file, AgentTask template)
    {
        var active = new Dictionary<string, AgentTask>(); var completed = new List<Completion>(); string? current = null;
        string? latestTaskId = null; DateTimeOffset? latestStartedAt = null;
        foreach (var item in JsonLines.Read(file))
        {
            var payload = item.Child("payload"); var type = payload.Text("type");
            if (item.Text("type") == "event_msg" && type == "task_started" && payload.Text("turn_id") is { } turn)
            {
                // A thread has one current turn. Older starts without terminal events are interrupted history.
                active.Clear();
                current = turn; active[turn] = template with { Id = "codex:" + template.Id + ":" + turn,
                    StartedAt = payload.Number("started_at") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : item.Time("timestamp") };
                latestTaskId = active[turn].Id; latestStartedAt = active[turn].StartedAt;
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
        return new(new("codex", new(true, "Rollout lifecycle", true), active.Values.ToList(), completed), latestTaskId, latestStartedAt);
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
