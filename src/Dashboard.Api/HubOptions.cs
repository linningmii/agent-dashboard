using System.Text.Json;
using Dashboard.Contracts;

namespace Dashboard.Api;

public sealed record HubOptions(string DataDirectory, string WebRoot, int UiPort, int IngestionPort, string BindAddress,
    TunnelOptions UiTunnel, TunnelOptions IngestionTunnel, string? PublicIngestionUrl)
{
    public static HubOptions Load(IConfiguration configuration)
    {
        var root = Environment.GetEnvironmentVariable("DASHBOARD_ROOT") ?? Directory.GetCurrentDirectory();
        var file = new ConfigurationBuilder().AddJsonFile(Environment.GetEnvironmentVariable("AGENT_DASHBOARD_CONFIG") ?? Path.Combine(root, "config.json"), optional: true).Build();
        var data = configuration["data-dir"] ?? Environment.GetEnvironmentVariable("DASHBOARD_DATA_DIR") ?? file["dataDirectory"] ?? Path.Combine(root, "data");
        var ui = int.Parse(configuration["ui-port"] ?? Environment.GetEnvironmentVariable("PORT") ?? file["port"] ?? "4317");
        var ingest = int.Parse(configuration["ingestion-port"] ?? Environment.GetEnvironmentVariable("INGESTION_PORT") ?? file["ingestionPort"] ?? "4319");
        if (ui == ingest || ui is < 1 or > 65535 || ingest is < 1 or > 65535) throw new InvalidDataException("Two distinct valid ports are required");
        var uiTunnel = new TunnelOptions(); var ingestionTunnel = new TunnelOptions();
        var tunnelFile = Path.Combine(root, "tunnel.json");
        if (File.Exists(tunnelFile) && configuration["no-tunnels"] != "true")
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tunnelFile)); var json = document.RootElement;
            uiTunnel = (json.TryGetProperty("ui", out var u) ? u : json).Deserialize<TunnelOptions>(Protocol.Json) ?? new();
            ingestionTunnel = json.TryGetProperty("ingestion", out var i) ? i.Deserialize<TunnelOptions>(Protocol.Json) ?? new() : new();
        }
        foreach (var (tunnel, port) in new[] { (uiTunnel, ui), (ingestionTunnel, ingest) })
            if (tunnel.Enabled && (tunnel.Port != port || string.IsNullOrWhiteSpace(tunnel.Id))) throw new InvalidDataException("Tunnel port/ID does not match listener");
        if (uiTunnel.Enabled && ingestionTunnel.Enabled && uiTunnel.Id == ingestionTunnel.Id) throw new InvalidDataException("Use two separate tunnels");
        var packagedWeb = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return new(Path.GetFullPath(data), configuration["web-root"] ?? (Directory.Exists(packagedWeb) ? packagedWeb : Path.Combine(root, "web", "dist")), ui, ingest,
            configuration["bind"] ?? file["bindAddress"] ?? "127.0.0.1", uiTunnel, ingestionTunnel, configuration["ingestion-url"] ?? Environment.GetEnvironmentVariable("DASHBOARD_INGESTION_URL") ?? file["ingestionUrl"]);
    }
}
