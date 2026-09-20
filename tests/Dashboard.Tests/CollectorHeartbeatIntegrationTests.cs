using System.Diagnostics;
using System.Net.Http.Headers;
using Dashboard.Contracts;
using Dashboard.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dashboard.Tests;

public sealed class CollectorHeartbeatIntegrationTests
{
    [Fact] public async Task RealCollectorKeepsHeartbeatingWhileSnapshotRequestIsBlocked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dashboard-heartbeat-cli-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var snapshotStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeHeartbeats = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new List<long>(); var credential = Credentials.Token();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapPost("/v1/devices/device/sessions", () => new SessionResponse("session", 1));
        app.MapPost("/v1/devices/device/heartbeat", (DeviceHeartbeat heartbeat, HttpContext context) =>
        {
            Assert.Equal("Bearer " + credential, context.Request.Headers.Authorization.ToString());
            Assert.Equal("session", heartbeat.SessionId);
            lock (heartbeats)
            {
                heartbeats.Add(heartbeat.Sequence);
                if (snapshotStarted.Task.IsCompleted && heartbeats.Count >= 3) threeHeartbeats.TrySetResult();
            }
            return new DeviceHeartbeatResponse(true, heartbeat.Sequence, DateTimeOffset.UtcNow);
        });
        app.MapPut("/v1/devices/device/snapshot", async () =>
        {
            snapshotStarted.TrySetResult(); await releaseSnapshot.Task.WaitAsync(limit.Token);
            return new ReportResponse(true, 1);
        });
        Process? process = null; Task<string>? output = null, errors = null;
        try
        {
            await app.StartAsync(limit.Token);
            var config = Path.Combine(directory, "collector.json");
            Credentials.WritePrivate(config, System.Text.Json.JsonSerializer.Serialize(new CollectorConfig(app.Urls.Single(), "device", credential, "Heartbeat fixture"), Protocol.Json));
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "Dashboard.Collector.dll"), "run", "--config", config, "--state", Path.Combine(directory, "collector.sqlite") }) start.ArgumentList.Add(arg);
            start.Environment["CODEX_STATE_DB"] = Path.Combine(directory, "missing.sqlite");
            start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(directory, "missing-claude");
            start.Environment["COPILOT_STORAGE"] = Path.Combine(directory, "missing-copilot");
            process = Process.Start(start)!; output = process.StandardOutput.ReadToEndAsync(); errors = process.StandardError.ReadToEndAsync();
            await snapshotStarted.Task.WaitAsync(limit.Token);
            await threeHeartbeats.Task.WaitAsync(limit.Token);
            Assert.False(process.HasExited); Assert.False(releaseSnapshot.Task.IsCompleted);
            lock (heartbeats) Assert.Equal(new long[] { 1, 2, 3 }, heartbeats.Take(3));
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(true);
            if (process is not null) { await process.WaitForExitAsync(); process.Dispose(); }
            releaseSnapshot.TrySetResult();
            await app.StopAsync(CancellationToken.None);
            if (output is not null) await output; if (errors is not null) await errors;
            Directory.Delete(directory, true);
        }
    }
}
