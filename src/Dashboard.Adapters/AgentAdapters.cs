using System.Diagnostics;
using Dashboard.Contracts;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Adapters;

public record AdapterResult(string Source, SourceInfo Info, List<AgentTask> Tasks, List<Completion> Completions);
public interface IAgentAdapter { AdapterResult Collect(); }
public sealed class CopilotAdapter(AdapterPaths paths) : IAgentAdapter
{
    public AdapterResult Collect() => new("copilot", new(Directory.Exists(paths.CopilotStorage), "Manual task reporting; automatic lifecycle is not available", false), [], []);
}

public sealed class ClaudeAdapter(AdapterPaths paths) : IAgentAdapter
{
    // A live CLI process alone is deliberately not counted. Unfinished transcript activity is estimated,
    // and can be inspected without satisfying the running minimum. Explicit/manual reporting is authoritative.
    public AdapterResult Collect()
    {
        if (!Directory.Exists(paths.ClaudeRoot)) return new("claude", new(false, "Claude storage not found"), [], []);
        try
        {
            var root = Path.Combine(paths.ClaudeRoot, "projects");
            var recent = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar)).OrderByDescending(File.GetLastWriteTimeUtc).Take(50) : [];
            var tasks = new List<AgentTask>(); var completions = new List<Completion>();
            foreach (var file in recent)
            {
                var result = Parse(file); if (result.Task is { } task && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(24)) tasks.Add(task); completions.AddRange(result.Completions);
            }
            return new("claude", new(true, "Transcript estimates; manual reports count as running", true), tasks, completions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new("claude", new(false, "Claude transcript read failed"), [], []); }
    }
    public static (AgentTask? Task, List<Completion> Completions) Parse(string file)
    {
        var title = "Claude Code task"; var workspace = ""; var session = Path.GetFileNameWithoutExtension(file);
        AgentTask? active = null; var completed = new List<Completion>();
        foreach (var item in JsonLines.Read(file))
        {
            if (item.Child("isSidechain").ValueKind == System.Text.Json.JsonValueKind.True) continue;
            title = item.Text("aiTitle") ?? title; workspace = item.Text("cwd") ?? workspace; session = item.Text("sessionId") ?? session;
            var userContent = item.Child("message").Child("content");
            var userText = userContent.ValueKind == System.Text.Json.JsonValueKind.String ? userContent.GetString() : JsonLines.Message(item.Child("message"));
            if (item.Text("type") == "user" && !string.IsNullOrEmpty(userText))
            {
                if (userText.StartsWith("<local-command", StringComparison.Ordinal)) { active = null; continue; }
                title = JsonLines.Clip(userText, 200);
                active = new() { Id = "claude:" + session + ":" + (item.Text("uuid") ?? item.Text("timestamp")), Source = "claude", Title = title,
                    Workspace = workspace, StartedAt = item.Time("timestamp"), Status = TaskStatus.Unknown, Confidence = Confidence.Estimated };
            }
            if (active is null) continue;
            active = active with { Title = title, Workspace = workspace };
            if (item.Text("type") == "assistant")
            {
                var message = item.Child("message"); var output = JsonLines.Message(message);
                if (output.Length > 0) active = active with { LatestOutput = JsonLines.Clip(output), LatestOutputAt = item.Time("timestamp") };
                if (message.Text("stop_reason") == "end_turn")
                {
                    completed.Add(Dashboard.Core.HubService.ToCompletion(active, active.Id + ":complete", item.Time("timestamp") ?? DateTimeOffset.UtcNow)); active = null;
                }
            }
        }
        return (active, completed);
    }
}
