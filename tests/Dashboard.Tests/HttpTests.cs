using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dashboard.Contracts;
using Dashboard.Core;
using Xunit;

namespace Dashboard.Tests;

public sealed class HttpTests
{
    private static int Port() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
    [Fact]
    public async Task RealListenersAuthenticateIsolateReportAndStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dashboard-http-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        var ui = Port(); var ingest = Port(); while (ingest == ui) ingest = Port();
        var dll = Path.Combine(AppContext.BaseDirectory, "Dashboard.Api.dll");
        var key = Credentials.Token();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { dll, "--ui-port", ui.ToString(), "--ingestion-port", ingest.ToString(), "--data-dir", dir, "--no-tunnels", "true" }) start.ArgumentList.Add(arg);
        start.Environment["DASHBOARD_ACCESS_KEY"] = key;
        using var host = Process.Start(start)!; var stdout = host.StandardOutput.ReadToEndAsync(); var stderr = host.StandardError.ReadToEndAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ui}"), Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { using var ready = await client.GetAsync("/api/auth/status"); if (ready.IsSuccessStatusCode) break; }
                catch (HttpRequestException) when (attempt < 50) { }
                if (attempt >= 50 || host.HasExited) throw new Exception("API did not start: " + (host.HasExited ? await stderr : "timeout"));
                await Task.Delay(100);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/API/STATUS")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);
            var pair = await (await client.PostAsJsonAsync("/api/devices/pair", new { })).Content.ReadFromJsonAsync<PairingResponse>(Protocol.Json);
            using var collector = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ingest}") };
            Assert.Equal(HttpStatusCode.NotFound, (await collector.GetAsync("/api/status")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await collector.GetAsync("/API/STATUS")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/health")).StatusCode);
            var registered = await (await collector.PostAsJsonAsync("/v1/devices/register", new EnrollmentRequest(pair!.Code, "HTTP device"))).Content.ReadFromJsonAsync<EnrollmentResponse>(Protocol.Json);
            collector.DefaultRequestHeaders.Authorization = new("Bearer", registered!.Token);
            var session = await (await collector.PostAsJsonAsync($"/v1/devices/{registered.DeviceId}/sessions", new { })).Content.ReadFromJsonAsync<SessionResponse>(Protocol.Json);
            var report = new DeviceReport { SessionId = session!.SessionId, Sequence = 1, Sources = new() { ["codex"] = new(true) }, Tasks = [new() { Id = "test", Source = "codex", Title = "Test" }] };
            var accepted = await collector.PutAsJsonAsync($"/v1/devices/{registered.DeviceId}/snapshot", report, Protocol.Json); Assert.True(accepted.IsSuccessStatusCode);
            var snapshot = await client.GetFromJsonAsync<Snapshot>("/api/status", Protocol.Json); Assert.Equal(1, snapshot!.RunningCount);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var events = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(timeout.Token));
            Assert.Equal("event: snapshot", await reader.ReadLineAsync(timeout.Token));
            Assert.StartsWith("data: ", await reader.ReadLineAsync(timeout.Token));
            var json = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json"); Assert.True(json.GetProperty("paths").TryGetProperty("/v1/devices/{id}/snapshot", out _));
        }
        finally { if (!host.HasExited) host.Kill(true); await host.WaitForExitAsync(); await stdout; await stderr; Directory.Delete(dir, true); }
    }
}
