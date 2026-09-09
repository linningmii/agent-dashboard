using System.Text.Json;
using Dashboard.Adapters;
using Dashboard.Contracts;
using Dashboard.Core;

string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var command = args.FirstOrDefault() ?? "run";
var root = Environment.GetEnvironmentVariable("DASHBOARD_ROOT") ?? Directory.GetCurrentDirectory();
var configFile = Option("--config") ?? Environment.GetEnvironmentVariable("AGENT_COLLECTOR_CONFIG") ?? Path.Combine(root, "collector.json");
var stateFile = Option("--state") ?? Environment.GetEnvironmentVariable("AGENT_COLLECTOR_STATE") ?? Path.Combine(root, "data", "collector.sqlite");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (command == "enroll")
    {
        if (File.Exists(configFile)) throw new InvalidOperationException("Collector is already enrolled; move its config aside to enroll again");
        var url = Option("--url") ?? throw new ArgumentException("--url is required");
        var name = Option("--name") ?? Environment.MachineName;
        var code = Environment.GetEnvironmentVariable("AGENT_PAIR_CODE");
        if (code is null) { Console.Write("Pairing code: "); code = Console.ReadLine(); }
        using var client = new CollectorClient(new(url, "", "", name, Option("--tunnel-id")));
        var enrolled = await client.Send<EnrollmentResponse>("/v1/devices/register", HttpMethod.Post, new EnrollmentRequest(code ?? "", name), false, cancellation.Token);
        Credentials.WritePrivate(configFile, JsonSerializer.Serialize(new CollectorConfig(url, enrolled.DeviceId, enrolled.Token, name, Option("--tunnel-id")), Protocol.Json));
        Console.WriteLine("Device enrolled. Run the collector with the saved config."); return;
    }
    if (command == "migrate-host")
    {
        var data = Option("--legacy-dir") ?? Path.Combine(root, "data");
        var hub = new SqliteStateStore<HubState>(Option("--hub-db") ?? Path.Combine(data, "hub.sqlite"));
        LegacyImporter.Import(hub, data);
        if (File.Exists(configFile)) throw new InvalidOperationException("Refusing to replace existing collector credentials");
        var enrolled = LegacyImporter.EnrollOriginalHost(hub, Environment.MachineName);
        Credentials.WritePrivate(configFile, JsonSerializer.Serialize(new CollectorConfig(Option("--url") ?? "http://127.0.0.1:4319", enrolled.DeviceId, enrolled.Token, Environment.MachineName), Protocol.Json));
        var collector = new SqliteStateStore<CollectorState>(stateFile);
        collector.Update(state =>
        {
            var legacy = Path.Combine(data, "state.json");
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                if (doc.RootElement.TryGetProperty("observedAutomatic", out var observed)) foreach (var source in observed.EnumerateObject())
                    foreach (var task in source.Value.GetProperty("tasks").EnumerateObject())
                    { var item = task.Value.Deserialize<AgentTask>(Protocol.Json); if (item is not null) state.Observed[item.Id] = item; }
            }
            return true;
        });
        Console.WriteLine("Legacy state imported and original host enrolled. Source JSON files were preserved."); return;
    }
    var store = new SqliteStateStore<CollectorState>(stateFile);
    var paths = AdapterPaths.Discover();
    var engine = new CollectorEngine(store, [new CodexAdapter(paths), new CopilotAdapter(paths), new ClaudeAdapter(paths)]);
    if (command == "inspect")
    {
        var result = engine.Collect("inspection", 1);
        Console.WriteLine(JsonSerializer.Serialize(result, Protocol.Json)); return;
    }
    if (command == "report")
    {
        var task = engine.AddManual(new(Option("--source") ?? "copilot", Option("--title") ?? throw new ArgumentException("--title required"), Option("--workspace"), Option("--output"), double.Parse(Option("--minutes") ?? "240")));
        Console.WriteLine(task.Id); return;
    }
    if (command == "complete") { engine.CompleteManual(Option("--id") ?? throw new ArgumentException("--id required"), Option("--output")); return; }
    if (command != "run") throw new ArgumentException("Commands: run, enroll, inspect, report, complete, migrate-host");
    var config = JsonSerializer.Deserialize<CollectorConfig>(File.ReadAllText(configFile), Protocol.Json) ?? throw new InvalidDataException("Invalid collector config");
    using var sender = new CollectorClient(config);
    string? sessionId = null; long sequence = 0; DeviceReport? pending = null; var once = args.Contains("--once");
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            sessionId ??= (await sender.Send<SessionResponse>($"/v1/devices/{config.DeviceId}/sessions", HttpMethod.Post, new { }, true, cancellation.Token)).SessionId;
            pending ??= engine.Collect(sessionId, ++sequence);
            await sender.Send<ReportResponse>($"/v1/devices/{config.DeviceId}/snapshot", HttpMethod.Put, pending, true, cancellation.Token);
            engine.Acknowledge(pending); pending = null;
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} Snapshot delivered");
            if (once) break;
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation.Token);
        }
        catch (DomainException e) when (e.Status == 409) { Console.Error.WriteLine("Collector session replaced; stop duplicate collectors and restart."); Environment.ExitCode = 1; break; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
        catch (Exception e)
        {
            Console.Error.WriteLine("Report failed: " + e.GetType().Name + ". Check enrollment, network, and tunnel sign-in.");
            if (once) { Environment.ExitCode = 1; break; }
            await Task.Delay(TimeSpan.FromSeconds(15), cancellation.Token);
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
catch (Exception e) { Console.Error.WriteLine(e is DomainException or ArgumentException or InvalidOperationException ? e.Message : "Collector failed: " + e.GetType().Name); Environment.ExitCode = 1; }
