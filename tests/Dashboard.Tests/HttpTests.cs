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
        start.Environment["DASHBOARD_UI_AUTH"] = "access-key";
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
            var auth = await client.GetFromJsonAsync<AuthInfo>("/api/auth/status", Protocol.Json);
            Assert.True(auth!.RequiresLogin); Assert.False(auth.Authenticated);
            using var claimed = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            claimed.Headers.Add("X-Tunnel-Authorization", "tunnel forged");
            claimed.Headers.Add("X-Forwarded-Host", "example-4317.jpe1.devtunnels.ms");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(claimed)).StatusCode);
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
            // Exercise the real packaged CLI, not just the shared HTTP models.
            var cliPair = await (await client.PostAsJsonAsync("/api/devices/pair", new { })).Content.ReadFromJsonAsync<PairingResponse>(Protocol.Json);
            var collectorDll = Path.Combine(AppContext.BaseDirectory, "Dashboard.Collector.dll");
            var configFile = Path.Combine(dir, "cli.json"); var stateFile = Path.Combine(dir, "collector.sqlite");
            async Task<string> RunCollector(params string[] arguments)
            {
                var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var value in new[] { collectorDll }.Concat(arguments).Concat(["--config", configFile, "--state", stateFile])) info.ArgumentList.Add(value);
                info.Environment["AGENT_PAIR_CODE"] = cliPair!.Code;
                info.Environment["CODEX_STATE_DB"] = Path.Combine(dir, "missing-codex.sqlite");
                info.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(dir, "missing-claude");
                info.Environment["COPILOT_STORAGE"] = Path.Combine(dir, "missing-copilot");
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(limit.Token); Assert.True(process.ExitCode == 0, await errors); return (await output).Trim(); }
                finally { if (!process.HasExited) process.Kill(true); }
            }
            await RunCollector("enroll", "--url", $"http://127.0.0.1:{ingest}", "--name", "CLI integration device");
            var manualId = await RunCollector("report", "--source", "copilot", "--title", "CLI task");
            await RunCollector("run", "--once");
            snapshot = await client.GetFromJsonAsync<Snapshot>("/api/status", Protocol.Json);
            Assert.Equal(2, snapshot!.RunningCount);
            Assert.Contains(snapshot.Tasks, task => task.Title == "CLI task" && task.DeviceName == "CLI integration device");
            await RunCollector("complete", "--id", manualId, "--output", "CLI completed output");
            await RunCollector("run", "--once");
            snapshot = await client.GetFromJsonAsync<Snapshot>("/api/status", Protocol.Json);
            Assert.Contains(snapshot!.Completions, item => item.LatestOutput == "CLI completed output");
            // Separate anonymous enrollment throttling from dashboard login.
            using var anonymous = new HttpClient(new HttpClientHandler { UseCookies = false });
            for (var i = 0; i < 10; i++)
            {
                var denied = await anonymous.PostAsJsonAsync($"http://127.0.0.1:{ingest}/v1/devices/register", new EnrollmentRequest("invalid", "Rate-limit fixture"));
                Assert.Contains(denied.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests });
            }
            Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.PostAsJsonAsync($"http://127.0.0.1:{ingest}/v1/devices/register", new EnrollmentRequest("invalid", "Rate-limit fixture"))).StatusCode);
            var login = await anonymous.PostAsJsonAsync($"http://127.0.0.1:{ui}/api/auth/login", new LoginInput(key));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            using var cookieClient = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = client.BaseAddress };
            cookieClient.DefaultRequestHeaders.Add("Cookie", cookie);
            Assert.Equal(HttpStatusCode.OK, (await cookieClient.GetAsync("/api/status")).StatusCode);
            using var streamLimit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cookieEvents = await cookieClient.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, streamLimit.Token);
            using var cookieReader = new StreamReader(await cookieEvents.Content.ReadAsStreamAsync(streamLimit.Token));
            Assert.Equal("event: snapshot", await cookieReader.ReadLineAsync(streamLimit.Token));
            Assert.Equal(HttpStatusCode.OK, (await cookieClient.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await cookieClient.GetAsync("/api/status")).StatusCode);
            // A revoked cookie must also lose an already-open stream.
            while (await cookieReader.ReadLineAsync(streamLimit.Token) is not null) { }
            for (var i = 0; i < 10; i++) await anonymous.PostAsJsonAsync($"http://127.0.0.1:{ui}/api/auth/login", new LoginInput("invalid"));
            Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.PostAsJsonAsync($"http://127.0.0.1:{ui}/api/auth/login", new LoginInput(key))).StatusCode);
        }
        finally { if (!host.HasExited) host.Kill(true); await host.WaitForExitAsync(); await stdout; await stderr; Directory.Delete(dir, true); }
    }
}
