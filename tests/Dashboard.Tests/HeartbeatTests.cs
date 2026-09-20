using Dashboard.Core;
using Xunit;

namespace Dashboard.Tests;

public sealed class HeartbeatTests
{
    [Fact] public async Task HeartbeatContinuesIndependentlyAndRetriesTheSameSequence()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var received = new List<long>(); var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = CollectorHeartbeat.RunAsync("session", TimeSpan.FromMilliseconds(10), (input, _) =>
        {
            received.Add(input.Sequence); Assert.Equal("session", input.SessionId);
            if (received.Count == 1) throw new HttpRequestException("Temporary network failure");
            if (received.Count == 4) complete.TrySetResult();
            return Task.CompletedTask;
        }, _ => { }, stop.Token);
        // The task/snapshot producer need not make any progress for heartbeats to arrive.
        await complete.Task.WaitAsync(timeout.Token); await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heartbeat);
        Assert.Equal(new long[] { 1, 1, 2, 3 }, received.Take(4));
    }

    [Fact]
    public async Task ReplacedSessionStopsHeartbeats()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<DomainException>(() => CollectorHeartbeat.RunAsync("session", TimeSpan.FromMilliseconds(1),
            (_, _) => { calls++; throw new DomainException(409, "Rejected"); }, _ => { }, CancellationToken.None));
        Assert.Equal(409, error.Status); Assert.Equal(1, calls);
    }

    [Theory] [InlineData(401)] [InlineData(403)]
    public async Task AuthenticationFailuresRetryAllowingTunnelTokenRefresh(int status)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var task = CollectorHeartbeat.RunAsync("session", TimeSpan.FromMilliseconds(10), (input, _) =>
        {
            if (++calls == 1) throw new DomainException(status, "Expired tunnel token");
            Assert.Equal(1, input.Sequence); recovered.TrySetResult(); return Task.CompletedTask;
        }, _ => { }, stop.Token);
        await recovered.Task.WaitAsync(stop.Token); await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
