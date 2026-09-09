using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dashboard.Contracts;

namespace Dashboard.Api;

public sealed partial class TunnelWorker(HubOptions options, ILogger<TunnelWorker> logger) : BackgroundService
{
    public TunnelState Ui { get; private set; } = new(options.UiTunnel.Enabled, options.UiTunnel.Enabled ? "starting" : "disabled", options.UiTunnel.Id);
    public TunnelState Ingestion { get; private set; } = new(options.IngestionTunnel.Enabled, options.IngestionTunnel.Enabled ? "starting" : "disabled", options.IngestionTunnel.Id);
    [GeneratedRegex(@"https://[a-z0-9-]+\.[a-z0-9]+\.devtunnels\.ms/?", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelUrl();
    public static string? BrowserUrl(string line, int port)
    {
        if (!line.Contains("Connect via browser:", StringComparison.OrdinalIgnoreCase)) return null;
        var match = TunnelUrl().Match(line);
        return match.Success && new Uri(match.Value).Host.Split('.')[0].EndsWith("-" + port, StringComparison.Ordinal) ? match.Value : null;
    }
    public static async Task<string> RunCli(IEnumerable<string> args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = new Process { StartInfo = StartInfo(args) };
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); await error; if (process.ExitCode != 0) throw new InvalidOperationException("Dev Tunnels command failed; check CLI sign-in"); return await output; }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    private static ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg); return info;
    }
    protected override Task ExecuteAsync(CancellationToken token) => Task.WhenAll(Host(options.UiTunnel, state => Ui = state, token), Host(options.IngestionTunnel, state => Ingestion = state, token));
    public static void ValidatePrivateConfiguration(JsonElement tunnel, JsonElement ports, JsonElement port, int expectedPort)
    {
        var p = port.GetProperty("port"); var list = ports.GetProperty("ports");
        if (tunnel.GetProperty("tunnel").GetProperty("accessControl").GetArrayLength() != 0 || list.GetArrayLength() != 1 ||
            list[0].GetProperty("portNumber").GetInt32() != expectedPort || p.GetProperty("portNumber").GetInt32() != expectedPort ||
            p.GetProperty("protocol").GetString() != "http" || p.GetProperty("accessControl").GetArrayLength() != 0 ||
            p.GetProperty("requestTimeoutSeconds").GetInt32() != 0)
            throw new InvalidDataException("Tunnel requires owner-only access and exactly its configured HTTP port");
    }
    private static async Task ValidatePrivateTunnel(TunnelOptions settings, CancellationToken token)
    {
        using var tunnel = JsonDocument.Parse(await RunCli(["show", settings.Id!, "--json"], token));
        using var ports = JsonDocument.Parse(await RunCli(["port", "list", settings.Id!, "--json"], token));
        using var port = JsonDocument.Parse(await RunCli(["port", "show", settings.Id!, "--port-number", settings.Port.ToString(), "--json"], token));
        ValidatePrivateConfiguration(tunnel.RootElement, ports.RootElement, port.RootElement, settings.Port);
    }
    private async Task Host(TunnelOptions settings, Action<TunnelState> update, CancellationToken token)
    {
        if (!settings.Enabled) return;
        var state = new TunnelState(true, "starting", settings.Id); var attempts = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                update(state with { State = "starting" });
                await ValidatePrivateTunnel(settings, token);
                await RunCli(["update", settings.Id!, "--expiration", "30d", "--json"], token);
                var trustTunnel = options.UsesDevTunnelAuthentication && settings == options.UiTunnel;
                var hostArgs = new List<string> { "host", settings.Id! };
                if (trustTunnel) hostArgs.AddRange(["--host-header", "unchanged", "--origin-header", "unchanged"]);
                using var process = new Process { StartInfo = StartInfo(hostArgs) };
                using var childToken = CancellationTokenSource.CreateLinkedTokenSource(token);
                process.Start(); update(state with { State = "connecting" });
                async Task Read(StreamReader reader)
                {
                    while (await reader.ReadLineAsync(childToken.Token) is { } line)
                    {
                        var url = BrowserUrl(line, settings.Port);
                        if (url is not null) { state = state with { State = "hosting", Url = url, Error = null }; update(state); attempts = 0; logger.LogInformation("Tunnel {Id}: {Url}", settings.Id, state.Url); }
                    }
                }
                async Task Renew()
                {
                    var nextRenewal = DateTimeOffset.UtcNow.AddHours(24);
                    while (true)
                    {
                        await Task.Delay(trustTunnel ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(24), childToken.Token);
                        // Closing the host on validation failure prevents quietly continuing
                        // after an owner changes the tunnel to shared/anonymous access.
                        if (trustTunnel) await ValidatePrivateTunnel(settings, childToken.Token);
                        if (DateTimeOffset.UtcNow < nextRenewal) continue;
                        try { await RunCli(["update", settings.Id!, "--expiration", "30d", "--json"], childToken.Token); }
                        catch (Exception) when (!childToken.IsCancellationRequested) { logger.LogWarning("Tunnel {Id} expiration renewal failed; check CLI sign-in", settings.Id); }
                        nextRenewal = DateTimeOffset.UtcNow.AddHours(24);
                    }
                }
                var output = Read(process.StandardOutput); var errors = Read(process.StandardError); var renewal = Renew();
                try { await await Task.WhenAny(process.WaitForExitAsync(token), renewal); }
                finally
                {
                    update(state with { State = "stopping" });
                    await childToken.CancelAsync(); if (!process.HasExited) process.Kill(true);
                    try { await Task.WhenAll(output, errors, renewal); } catch (OperationCanceledException) { }
                }
                if (!token.IsCancellationRequested) throw new IOException("Tunnel exited");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Tunnel {Id} retry: {Type}", settings.Id, error.GetType().Name);
                update(state with { State = "retrying", Error = "Check tunnel configuration, network, and devtunnel user show" });
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, attempts++))), token); } catch (OperationCanceledException) { break; }
            }
        }
        update(state with { State = "stopped" });
    }
}
