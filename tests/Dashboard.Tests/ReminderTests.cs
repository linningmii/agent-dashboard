using Dashboard.Api;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dashboard.Tests;

public sealed class ReminderTests
{
    [Fact] public void RemindersRepeatBelowMinimumAndStopAtOrAboveIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-reminders-" + Guid.NewGuid());
        try
        {
            var clock = new FakeClock(); var hub = new HubService(new(Path.Combine(directory, "hub.sqlite")), clock);
            using var worker = new SnapshotWorker(hub, clock, NullLogger<SnapshotWorker>.Instance);
            var (id, reader) = worker.Subscribe();
            Assert.True(reader.TryRead(out var snapshot)); Assert.Equal("snapshot", snapshot!.Name);
            Assert.True(reader.TryRead(out var initial)); Assert.Equal("reminder", initial!.Name);
            worker.Tick(); while (reader.TryRead(out _)) { }
            clock.Now = clock.Now.AddMinutes(14); worker.Tick(); Assert.False(reader.TryRead(out _));
            clock.Now = clock.Now.AddMinutes(1); worker.Tick(); Assert.True(reader.TryRead(out var reminder)); Assert.Equal("reminder", reminder!.Name);
            for (var i = 0; i < 4; i++) hub.CreateTask(new("copilot", "Manual"));
            worker.Tick(); while (reader.TryRead(out var item)) Assert.NotEqual("reminder", item.Name);
            clock.Now = clock.Now.AddMinutes(15); worker.Tick(); while (reader.TryRead(out var item)) Assert.NotEqual("reminder", item.Name);
            worker.Unsubscribe(id);
        }
        finally { Directory.Delete(directory, true); }
    }
}
