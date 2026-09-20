using Dashboard.Contracts;

namespace Dashboard.Core;

// Run on a separate task and transport so reading agent files and retrying task
// snapshots cannot prevent the collector from announcing that it is online.
public static class CollectorHeartbeat
{
    public static async Task RunAsync(string sessionId, TimeSpan interval,
        Func<DeviceHeartbeat, CancellationToken, Task> send, Action<string> warning, CancellationToken token)
    {
        long sequence = 1;
        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await send(new(sessionId, sequence), token); sequence++; }
            catch (DomainException error) when (error.Status == 409) { throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { warning("Heartbeat failed: " + error.GetType().Name + ". Retrying."); }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
