using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Dashboard.Contracts;
using Dashboard.Core;

namespace Dashboard.Api;

public record StreamEvent(string Name, string Json);
public sealed class SnapshotWorker(HubService hub, TimeProvider clock, ILogger<SnapshotWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<StreamEvent>> clients = new();
    private DateTimeOffset? lastReminder;
    private bool wasBelow;
    private string? previous;
    public (Guid Id, ChannelReader<StreamEvent> Reader) Subscribe()
    {
        var id = Guid.NewGuid(); var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        clients[id] = channel;
        channel.Writer.TryWrite(new("snapshot", JsonSerializer.Serialize(hub.Snapshot(), Protocol.Json)));
        return (id, channel.Reader);
    }
    public void Unsubscribe(Guid id) { if (clients.TryRemove(id, out var channel)) channel.Writer.TryComplete(); }
    private void Publish(StreamEvent item) { foreach (var client in clients.Values) client.Writer.TryWrite(item); }
    public void Tick()
    {
        var snapshot = hub.Snapshot();
        var key = JsonSerializer.Serialize(snapshot with { GeneratedAt = default }, Protocol.Json);
        if (key != previous) { previous = key; Publish(new("snapshot", JsonSerializer.Serialize(snapshot, Protocol.Json))); }
        var below = !snapshot.Healthy; var now = clock.GetUtcNow();
        if (below && (!wasBelow || lastReminder is null || now - lastReminder >= TimeSpan.FromMinutes(snapshot.Settings.ReminderCooldownMinutes)))
        {
            lastReminder = now;
            var reminder = new Reminder(Protocol.Id(), now, snapshot.RunningCount, snapshot.MinimumRunning,
                $"{snapshot.RunningCount} tasks running. Start {snapshot.MissingCount} more to reach your minimum of {snapshot.MinimumRunning}.");
            Publish(new("reminder", JsonSerializer.Serialize(reminder, Protocol.Json)));
        }
        wasBelow = below;
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        do { try { Tick(); } catch (Exception error) { logger.LogError("Snapshot refresh failed: {Type}", error.GetType().Name); } } while (await timer.WaitForNextTickAsync(token));
    }
}
