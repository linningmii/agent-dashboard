namespace Dashboard.Adapters;

public sealed record AdapterPaths(string CodexState, string CodexHistory, string CodexIndex, string CodexSessions, string ClaudeRoot, string CopilotStorage)
{
    public static AdapterPaths Discover(string? home = null, string? platform = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        platform ??= OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var codex = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        var customAppData = platform == "windows" ? Environment.GetEnvironmentVariable("APPDATA") : platform == "linux" ? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") : null;
        var copilot = customAppData is null ? DefaultCopilotStorage(home, platform) : Path.Combine(customAppData, "Code", "User", "globalStorage", "github.copilot-chat");
        return new(Environment.GetEnvironmentVariable("CODEX_STATE_DB") ?? Path.Combine(codex, "state_5.sqlite"),
            Environment.GetEnvironmentVariable("CODEX_HISTORY_DB") ?? Path.Combine(codex, "thread_history_1.sqlite"),
            Environment.GetEnvironmentVariable("CODEX_SESSION_INDEX") ?? Path.Combine(codex, "session_index.jsonl"), Path.Combine(codex, "sessions"),
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"),
            Environment.GetEnvironmentVariable("COPILOT_STORAGE") ?? copilot);
    }
    public static string DefaultCopilotStorage(string home, string platform) => Path.Combine(platform switch
    {
        "windows" => Path.Combine(home, "AppData", "Roaming"),
        "macos" => Path.Combine(home, "Library", "Application Support"),
        "linux" => Path.Combine(home, ".config"),
        _ => throw new ArgumentException("Unknown collector platform", nameof(platform))
    }, "Code", "User", "globalStorage", "github.copilot-chat");
}
