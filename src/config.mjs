import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { validateTunnelConfig } from "./tunnel.mjs";

const workspace = path.resolve(import.meta.dirname, "..");
const configPath = process.env.AGENT_DASHBOARD_CONFIG || path.join(workspace, "config.json");
let fileConfig = {};
try {
  fileConfig = JSON.parse(fs.readFileSync(configPath, "utf8"));
} catch (error) {
  if (error.code !== "ENOENT") throw error;
}
let tunnelConfig = null;
try {
  tunnelConfig = JSON.parse(fs.readFileSync(path.join(workspace, "tunnel.json"), "utf8"));
} catch (error) {
  if (error.code !== "ENOENT") throw error;
}

export const config = {
  host: "127.0.0.1",
  port: Number(process.env.PORT || fileConfig.port || 4317),
  tunnel: validateTunnelConfig(tunnelConfig?.ui || tunnelConfig, Number(process.env.PORT || fileConfig.port || 4317)),
  ingestionPort: Number(process.env.INGESTION_PORT || fileConfig.ingestionPort || 4319),
  ingestionTunnel: validateTunnelConfig(tunnelConfig?.ingestion, Number(process.env.INGESTION_PORT || fileConfig.ingestionPort || 4319)),
  deviceRegistryFile: path.join(workspace, "data", "devices.json"),
  deviceName: fileConfig.deviceName || os.hostname(),
  collectLocal: fileConfig.collectLocal !== false,
  pollIntervalMs: Number(fileConfig.pollIntervalMs || 2000),
  dataFile: path.join(workspace, "data", "state.json"),
  codexStateDb: process.env.CODEX_STATE_DB || path.join(os.homedir(), ".codex", "state_5.sqlite"),
  codexHistoryDb: process.env.CODEX_HISTORY_DB || path.join(os.homedir(), ".codex", "thread_history_1.sqlite"),
  codexSessionIndex: process.env.CODEX_SESSION_INDEX || path.join(os.homedir(), ".codex", "session_index.jsonl"),
  copilotStorage: process.env.COPILOT_STORAGE || path.join(
    process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming"),
    "Code", "User", "globalStorage", "github.copilot-chat"
  ),
  claudeRoot: process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), ".claude"),
  defaults: {
    minimumRunning: Number(fileConfig.minimumRunning || 3),
    reminderCooldownMinutes: Number(fileConfig.reminderCooldownMinutes || 15),
    windowsNotifications: fileConfig.windowsNotifications !== false
  }
};
if (config.ingestionPort === config.port || !Number.isInteger(config.ingestionPort) || config.ingestionPort < 1 || config.ingestionPort > 65535) throw new Error("Choose a separate valid ingestionPort.");
if (config.tunnel.enabled && config.ingestionTunnel.enabled && config.tunnel.id === config.ingestionTunnel.id) throw new Error("UI and ingestion must use separate tunnels.");
