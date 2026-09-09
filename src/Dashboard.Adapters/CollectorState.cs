using Dashboard.Contracts;
using Dashboard.Core;
using TaskStatus = Dashboard.Contracts.TaskStatus;

namespace Dashboard.Adapters;

public sealed class CollectorState
{
    public DateTimeOffset StartedMonitoringAt { get; set; } = DateTimeOffset.UtcNow;
    public HashSet<string> SeenCompletions { get; set; } = [];
    public List<Completion> Outbox { get; set; } = [];
    public Dictionary<string, AgentTask> Observed { get; set; } = [];
    public List<AgentTask> ManualTasks { get; set; } = [];
}
public sealed class CollectorEngine(SqliteStateStore<CollectorState> store, IEnumerable<IAgentAdapter> adapters)
{
    public DeviceReport Collect(string sessionId, long sequence)
    {
        var results = adapters.Select(adapter => adapter.Collect()).ToList();
        return store.Update<DeviceReport>(state =>
        {
            var sources = results.ToDictionary(result => result.Source, result => result.Info);
            var tasks = new List<AgentTask>();
            foreach (var result in results)
            {
                if (!result.Info.Available) continue;
                tasks.AddRange(result.Tasks.Where(task => task.Status != TaskStatus.Stale));
                foreach (var task in result.Tasks) state.Observed[task.Id] = task;
                foreach (var completion in result.Completions)
                {
                    if (completion.CompletedAt < state.StartedMonitoringAt && !state.Observed.ContainsKey(completion.TaskId)) continue;
                    if (state.SeenCompletions.Add(completion.Id)) state.Outbox.Add(completion);
                    state.Observed.Remove(completion.TaskId);
                }
            }
            for (var i = 0; i < state.ManualTasks.Count; i++)
            {
                var task = state.ManualTasks[i];
                if (task.Status == TaskStatus.Running && task.ExpiresAt <= DateTimeOffset.UtcNow) state.ManualTasks[i] = task with { Status = TaskStatus.Stale };
            }
            tasks.AddRange(state.ManualTasks.Where(task => task.Status != TaskStatus.Completed));
            foreach (var task in state.Outbox.Concat<AgentTask>(tasks)) sources.TryAdd(task.Source, new(true, "Reported task", false));
            if (tasks.Count > 500) throw new InvalidOperationException("Collector report exceeds 500 tasks; refusing a silently truncated snapshot");
            return new() { SessionId = sessionId, Sequence = sequence, Sources = sources, Tasks = tasks, Completions = state.Outbox.Take(100).ToList() };
        });
    }
    public void Acknowledge(DeviceReport report) => store.Update(state => state.Outbox.RemoveAll(item => report.Completions.Any(sent => sent.Id == item.Id)));
    public AgentTask AddManual(TaskInput input) => store.Update(state =>
    {
        var task = new AgentTask { Id = Protocol.Id(), Source = input.Source, Title = HubService.Text(input.Title, 2000, "task title", true), Workspace = input.Workspace ?? "",
            LatestOutput = input.LatestOutput ?? "", Confidence = Confidence.Reported, StartedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(input.LeaseMinutes, 1, 1440)) };
        state.ManualTasks.Add(task); return task;
    });
    public void CompleteManual(string id, string? output) => store.Update(state =>
    {
        var index = state.ManualTasks.FindIndex(task => task.Id == id); if (index < 0) throw new InvalidOperationException("Reported task not found");
        var task = state.ManualTasks[index]; if (task.Status == TaskStatus.Completed) return false;
        task = task with { LatestOutput = output ?? task.LatestOutput, Status = TaskStatus.Completed }; state.ManualTasks[index] = task;
        state.Outbox.Add(HubService.ToCompletion(task, Protocol.Id(), DateTimeOffset.UtcNow)); return true;
    });
}
