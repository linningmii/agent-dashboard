using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Dashboard.Contracts;

namespace Dashboard.Core;

public sealed class CollectorClient : IDisposable
{
    private readonly CollectorConfig config;
    private readonly HttpClient http;
    private string? tunnelToken;
    private DateTimeOffset tokenExpires;
    public CollectorClient(CollectorConfig config)
    {
        this.config = config;
        var url = new Uri(config.Url);
        if ((!url.IsLoopback && url.Scheme != "https") || url.Scheme is not ("http" or "https") || url.UserInfo != "" || url.AbsolutePath != "/" || url.Query != "" || url.Fragment != "")
            throw new InvalidDataException("Collector URL must be an HTTPS origin or HTTP loopback origin");
        http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(30) };
    }
    public async Task<T> Send<T>(string route, HttpMethod method, object? body, bool authenticate, CancellationToken token)
    {
        if (!route.StartsWith('/') || route.StartsWith("//", StringComparison.Ordinal)) throw new InvalidOperationException("Relative collector route required");
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: Protocol.Json);
        request.Headers.Accept.ParseAdd("application/json");
        if (authenticate) request.Headers.Authorization = new("Bearer", config.Token);
        if (!string.IsNullOrEmpty(config.TunnelId))
        {
            if (tunnelToken is null || DateTimeOffset.UtcNow >= tokenExpires) await RefreshTunnelToken(token);
            request.Headers.Add("X-Tunnel-Authorization", "tunnel " + tunnelToken);
        }
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 401 or 403) tunnelToken = null;
            throw new DomainException((int)response.StatusCode, $"Collector HTTP {(int)response.StatusCode}; check sign-in and enrollment");
        }
        return await response.Content.ReadFromJsonAsync<T>(Protocol.Json, token) ?? throw new InvalidDataException("Empty API response");
    }
    private async Task RefreshTunnelToken(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "devtunnel.exe" : "devtunnel") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
        foreach (var value in new[] { "token", config.TunnelId!, "--scopes", "connect", "--json" }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Dev Tunnels CLI unavailable");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token); await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Run devtunnel user login on this device");
            using var doc = JsonDocument.Parse(await output); var root = doc.RootElement;
            tunnelToken = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : root.GetProperty("accessToken").GetString();
            tokenExpires = DateTimeOffset.UtcNow.AddHours(1);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    public void Dispose() => http.Dispose();
}
