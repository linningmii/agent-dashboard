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
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
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
    private (CodexAdapter Adapter, string Rollout, string History) ProjectedWithRollout(string status, string rollout)
    {
        var stateFile = Path.Combine(dir, "state.sqlite"); var historyFile = Path.Combine(dir, "history.sqlite");
        var file = Path.Combine(dir, "current-rollout.jsonl"); File.WriteAllText(file, rollout);
        using (var db = new SqliteConnection("Data Source=" + stateFile))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE threads(id TEXT,title TEXT,cwd TEXT,model TEXT,archived INTEGER,updated_at INTEGER,rollout_path TEXT); INSERT INTO threads VALUES('t','Fixture','/repo','model',0,$now,$file);";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$file", file); cmd.ExecuteNonQuery();
        }
        using (var db = new SqliteConnection("Data Source=" + historyFile))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,status TEXT,started_at INTEGER,completed_at INTEGER); CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_type TEXT,item_json TEXT,created_at_ms INTEGER,rollout_ordinal INTEGER); INSERT INTO thread_turns VALUES('t','projected',$status,100,NULL);";
            cmd.Parameters.AddWithValue("$status", status); cmd.ExecuteNonQuery();
        }
        return (new CodexAdapter(new(stateFile, historyFile, "missing-index", dir, dir, dir)), file, historyFile);
    }
    private static string Start(string id, long seconds) => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds), type = "event_msg", payload = new { type = "task_started", turn_id = id, started_at = seconds } }) + "\n";
    private static string End(string id, string type = "task_complete") => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.FromUnixTimeSeconds(300), type = "event_msg", payload = new { type, turn_id = id, last_agent_message = "Final" } }) + "\n";

    [Theory] [InlineData("interrupted")] [InlineData("completed")] [InlineData("inProgress")]
    public void NewRolloutTurnSupersedesOlderProjection(string status)
    {
        var (adapter, _, _) = ProjectedWithRollout(status, Start("new", 200));
        var result = adapter.Collect();
        Assert.True(result.Info.Available); Assert.Equal("codex:t:new", result.Tasks.Single().Id);
        Assert.Equal(TaskStatus.Running, result.Tasks.Single().Status);
    }

    [Theory] [InlineData("task_complete", 1)] [InlineData("turn_aborted", 0)]
    public void NewerTerminalRolloutDoesNotResurrectOldProjectedTurn(string end, int completions)
    {
        var (adapter, _, _) = ProjectedWithRollout("inProgress", Start("new", 200) + End("new", end));
        var result = adapter.Collect(); Assert.Empty(result.Tasks); Assert.Equal(completions, result.Completions.Count);
    }

    [Fact] public void RolloutCompletionWinsOverLaggingInProgressProjectionAndDeduplicatesAfterCatchup()
    {
        var (adapter, file, history) = ProjectedWithRollout("inProgress", Start("projected", 100));
        Assert.Single(adapter.Collect().Tasks);
        File.AppendAllText(file, End("projected"));
        var result = adapter.Collect(); Assert.Empty(result.Tasks); Assert.Equal("codex:t:projected:complete", result.Completions.Single().Id);
        using (var db = new SqliteConnection("Data Source=" + history))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE thread_turns SET status='completed', completed_at=300"; cmd.ExecuteNonQuery();
        }
        result = adapter.Collect(); Assert.Empty(result.Tasks); Assert.Single(result.Completions);
    }

    [Theory] [InlineData("completed")] [InlineData("interrupted")] [InlineData("failed")]
    public void StaleRolloutCannotResurrectProjectedTerminalTurn(string status)
    {
        var (adapter, _, _) = ProjectedWithRollout(status, Start("projected", 100));
        Assert.Empty(adapter.Collect().Tasks);
    }

    [Fact] public void OlderRolloutDoesNotOverrideNewerProjectedTurn()
    {
        var (adapter, _, _) = ProjectedWithRollout("inProgress", Start("older", 50) + End("older"));
        Assert.Equal("codex:t:projected", adapter.Collect().Tasks.Single().Id);
    }

    [Fact] public void UnprojectedTurnDoesNotCountAfterItsRolloutBecomesStale()
    {
        var (adapter, file, _) = ProjectedWithRollout("interrupted", Start("new", 200));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        Assert.Equal(TaskStatus.Stale, adapter.Collect().Tasks.Single().Status);
    }

    [Fact] public void RolloutPathRotationAndProjectionCatchupKeepOneActiveTurn()
    {
        var (adapter, _, history) = ProjectedWithRollout("interrupted", Start("projected", 100) + End("projected", "turn_aborted"));
        Assert.Empty(adapter.Collect().Tasks);
        var rotated = Path.Combine(dir, "rotated.jsonl"); File.WriteAllText(rotated, Start("new", 200));
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(dir, "state.sqlite")))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE threads SET rollout_path=$file";
            cmd.Parameters.AddWithValue("$file", rotated); cmd.ExecuteNonQuery();
        }
        Assert.Equal("codex:t:new", adapter.Collect().Tasks.Single().Id);
        using (var db = new SqliteConnection("Data Source=" + history))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO thread_turns VALUES('t','new','inProgress',200,NULL); INSERT INTO thread_items VALUES('t','new','agentMessage','{\"text\":\"Projected progress\"}',210000,2)";
            cmd.ExecuteNonQuery();
        }
        var result = adapter.Collect(); Assert.Equal("codex:t:new", result.Tasks.Single().Id);
        Assert.Equal("Projected progress", result.Tasks.Single().LatestOutput);
        File.AppendAllText(rotated, End("new"));
        result = adapter.Collect(); Assert.Empty(result.Tasks); Assert.Single(result.Completions);
    }
    [Fact] public void CodexReadsCanonicalNameLatestTurnAndFinalOutputFromSqlite()
    {
        var stateFile = Path.Combine(dir, "state.sqlite"); var historyFile = Path.Combine(dir, "history.sqlite"); var indexFile = Path.Combine(dir, "index.jsonl");
        using (var db = new SqliteConnection("Data Source=" + stateFile))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE threads(id TEXT,name TEXT,title TEXT,cwd TEXT,model TEXT,archived INTEGER,updated_at INTEGER,rollout_path TEXT); INSERT INTO threads VALUES('t',NULL,'Original prompt','/repo','test-model',0,$now,'');";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); cmd.ExecuteNonQuery();
        }
        using (var db = new SqliteConnection("Data Source=" + historyFile))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,status TEXT,started_at INTEGER,completed_at INTEGER); CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_type TEXT,item_json TEXT,created_at_ms INTEGER,rollout_ordinal INTEGER); INSERT INTO thread_turns VALUES('t','old','inProgress',100,NULL),('t','current','completed',200,300); INSERT INTO thread_items VALUES('t','current','agentMessage','{\"text\":\"Final output\"}',300000,2);";
            cmd.ExecuteNonQuery();
        }
        File.WriteAllText(indexFile, "{\"id\":\"t\",\"thread_name\":\"Canonical sidebar name\"}");
        var adapter = new CodexAdapter(new(stateFile, historyFile, indexFile, dir, dir, dir));
        var result = adapter.Collect(); Assert.True(result.Info.Available); Assert.Empty(result.Tasks);
        Assert.Equal("Canonical sidebar name", result.Completions.Single().Title); Assert.Equal("Final output", result.Completions.Single().LatestOutput);
        using (var db = new SqliteConnection("Data Source=" + historyFile))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE thread_turns SET status='inProgress' WHERE turn_id='current'"; cmd.ExecuteNonQuery();
        }
        result = adapter.Collect(); Assert.Single(result.Tasks); Assert.EndsWith(":current", result.Tasks[0].Id);
        SqliteConnection.ClearAllPools();
    }
}
