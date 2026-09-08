import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

function readProcesses() {
  if (process.platform !== "win32") return [];
  try {
    const output = execFileSync("powershell.exe", [
      "-NoProfile", "-NonInteractive", "-Command",
      "@(Get-Process -Name 'claude' -ErrorAction SilentlyContinue | Select-Object Id,@{n='startedAt';e={$_.StartTime.ToUniversalTime().ToString('o')}}) | ConvertTo-Json -Compress"
    ], { encoding: "utf8", timeout: 2000, windowsHide: true }).trim();
    if (!output) return [];
    const parsed = JSON.parse(output);
    return Array.isArray(parsed) ? parsed : [parsed];
  } catch {
    return [];
  }
}

function recentSessionFiles(projectsRoot, limit) {
  if (!fs.existsSync(projectsRoot)) return [];
  try {
    return fs.readdirSync(projectsRoot, { recursive: true, withFileTypes: true })
      .filter((entry) => entry.isFile() && entry.name.endsWith(".jsonl"))
      .map((entry) => {
        const file = path.join(entry.parentPath, entry.name);
        return { file, modifiedAt: fs.statSync(file).mtimeMs };
      })
      .sort((a, b) => b.modifiedAt - a.modifiedAt)
      .slice(0, Math.max(limit * 3, 12));
  } catch {
    return [];
  }
}

export function parseClaudeSession(file) {
  try {
    const stat = fs.statSync(file);
    const bytes = Math.min(stat.size, 512 * 1024);
    const descriptor = fs.openSync(file, "r");
    const buffer = Buffer.alloc(bytes);
    fs.readSync(descriptor, buffer, 0, bytes, stat.size - bytes);
    fs.closeSync(descriptor);
    const lines = buffer.toString("utf8").split(/\r?\n/);
    let title = "";
    let lastPrompt = "";
    let latestOutput = "";
    let latestOutputAt = null;
    let cwd = "";
    let sessionId = path.basename(file, ".jsonl");
    let firstTimestamp = null;
    for (const line of lines) {
      if (!line.trim().startsWith("{")) continue;
      try {
        const item = JSON.parse(line);
        sessionId = item.sessionId || item.session_id || sessionId;
        cwd = item.cwd || cwd;
        firstTimestamp ||= item.timestamp || null;
        if (item.type === "ai-title") title = item.aiTitle || title;
        if (item.type === "last-prompt") lastPrompt = item.lastPrompt || lastPrompt;
        if (item.type === "assistant" && Array.isArray(item.message?.content)) {
          const text = item.message.content.filter((part) => part.type === "text").map((part) => part.text).join("\n").trim();
          if (text) { latestOutput = text; latestOutputAt = item.timestamp || latestOutputAt; }
        }
      } catch {
        // A partial final JSONL line is expected while Claude is writing.
      }
    }
    return { sessionId, title: title || lastPrompt || "Claude Code task", cwd, latestOutput, latestOutputAt, firstTimestamp, modifiedAt: stat.mtimeMs };
  } catch {
    return null;
  }
}

export function readClaudeTasks(config) {
  if (readClaudeTasks.cache && Date.now() - readClaudeTasks.cacheAt < 10_000) return readClaudeTasks.cache;
  const installed = fs.existsSync(config.claudeRoot);
  const processes = readProcesses();
  const sessions = recentSessionFiles(path.join(config.claudeRoot, "projects"), processes.length)
    .map(({ file }) => parseClaudeSession(file))
    .filter(Boolean);
  const unused = [...sessions];
  const tasks = processes.map((processInfo) => {
    const startedMs = new Date(processInfo.startedAt).getTime();
    let index = unused.findIndex((session) => session.modifiedAt >= startedMs - 10 * 60_000);
    const session = index >= 0 ? unused.splice(index, 1)[0] : null;
    return {
      id: `claude:${processInfo.Id}:${session?.sessionId || "unknown"}`,
      externalId: session?.sessionId || String(processInfo.Id),
      source: "claude",
      title: session?.title || "Claude Code task",
      workspace: session?.cwd || "",
      status: "running",
      confidence: "automatic",
      startedAt: processInfo.startedAt || session?.firstTimestamp || new Date().toISOString(),
      latestOutput: String(session?.latestOutput || "").slice(0, 2000),
      latestOutputAt: session?.latestOutputAt || null
    };
  });
  const result = {
    available: installed,
    tasks,
    detail: tasks.length ? `${tasks.length} active process${tasks.length === 1 ? "" : "es"}` : installed ? "Claude Code is installed; no active process detected" : "Claude Code was not found"
  };
  readClaudeTasks.cache = result;
  readClaudeTasks.cacheAt = Date.now();
  return result;
}
