using System.Text.Json;
using Dashboard.Adapters;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Tests;

public sealed class AdapterTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dashboard-adapter-" + Guid.NewGuid());
    public AdapterTests() => Directory.CreateDirectory(dir);
    public void Dispose() => Directory.Delete(dir, true);
    [Theory] [InlineData("windows", "AppData")] [InlineData("macos", "Library")] [InlineData("linux", ".config")]
    public void PlatformDefaultsAreDistinct(string platform, string folder)
    {
        var location = AdapterPaths.DefaultCopilotStorage(dir, platform);
        Assert.StartsWith(dir, location);
        Assert.Contains(folder, location);
        Assert.EndsWith(Path.Combine("Code", "User", "globalStorage", "github.copilot-chat"), location);
    }
    [Fact] public void RolloutTracksLongTurnsAndOnlyExplicitCompletion()
    {
        var file = Path.Combine(dir, "rollout.jsonl");
        File.WriteAllLines(file, ["{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn\",\"started_at\":100}}", new string('x', 1100000), "{partial"]);
        var template = new AgentTask { Id = "thread", Title = "Task", Source = "codex" };
        Assert.Single(CodexAdapter.ParseRollout(file, template).Tasks);
        File.AppendAllText(file, "\n{\"timestamp\":\"2026-01-01T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn\",\"last_agent_message\":\"Finished\"}}\n");
        var parsed = CodexAdapter.ParseRollout(file, template); Assert.Empty(parsed.Tasks); Assert.Equal("Finished", parsed.Completions.Single().LatestOutput);
    }
    [Fact] public void NewTurnSupersedesUnfinishedHistoricalTurn()
    {
        var file = Path.Combine(dir, "superseded.jsonl");
        File.WriteAllLines(file, ["{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"old\"}}", "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"new\"}}"]);
        var parsed = CodexAdapter.ParseRollout(file, new AgentTask { Id = "thread", Title = "Test", Source = "codex" });
        Assert.EndsWith(":new", parsed.Tasks.Single().Id); Assert.Empty(parsed.Completions);
    }
    [Fact] public void ClaudeUnfinishedTranscriptIsUnknownNotConfirmedRunning()
    {
        var file = Path.Combine(dir, "claude.jsonl");
        File.WriteAllLines(file, ["{\"type\":\"user\",\"sessionId\":\"s\",\"uuid\":\"t\",\"message\":{\"content\":\"Work\"}}", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Progress\"}],\"stop_reason\":\"tool_use\"}}"]);
        Assert.Equal(TaskStatus.Unknown, ClaudeAdapter.Parse(file).Task!.Status);
        File.AppendAllText(file, "\n{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Done\"}],\"stop_reason\":\"end_turn\"}}\n");
        var parsed = ClaudeAdapter.Parse(file); Assert.Null(parsed.Task); Assert.Equal("Done", parsed.Completions.Single().LatestOutput);
    }
    [Fact] public void OutboxPersistsUntilAcknowledgedAndIgnoresOldHistory()
    {
        var db = new SqliteStateStore<CollectorState>(Path.Combine(dir, "collector.sqlite"));
        db.Update(state => { state.StartedMonitoringAt = DateTimeOffset.UtcNow.AddMinutes(-1); return true; });
        var old = new Completion { Id = "old", TaskId = "old-task", Source = "codex", Title = "Old", CompletedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var done = old with { Id = "new", TaskId = "new-task", CompletedAt = DateTimeOffset.UtcNow };
        var adapter = new Stub(new("codex", new(true), [], [old, done]));
        var engine = new CollectorEngine(db, [adapter]); var report = engine.Collect("session", 1); Assert.Single(report.Completions);
        var restarted = new CollectorEngine(new(Path.Combine(dir, "collector.sqlite")), [adapter]);
        Assert.Single(restarted.Collect("session", 2).Completions); restarted.Acknowledge(report);
        Assert.Empty(restarted.Collect("session", 3).Completions);
    }
    private sealed class Stub(AdapterResult result) : IAgentAdapter { public AdapterResult Collect() => result; }
}
