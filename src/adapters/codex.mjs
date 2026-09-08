import fs from "node:fs";
import { DatabaseSync } from "node:sqlite";

const normalizePath = (value) => {
  const text = String(value || "");
  return text.startsWith("\\\\?\\") ? text.slice(4) : text;
};

export function parseCodexRollout(file) {
  try {
    const normalizedFile = normalizePath(file);
    const stat = fs.statSync(normalizedFile);
    const bytes = Math.min(stat.size, 1024 * 1024);
    const descriptor = fs.openSync(normalizedFile, "r");
    const buffer = Buffer.alloc(bytes);
    fs.readSync(descriptor, buffer, 0, bytes, stat.size - bytes);
    fs.closeSync(descriptor);
    let activeTurn = null;
    let latestOutput = "";
    let latestOutputAt = null;
    for (const line of buffer.toString("utf8").split(/\r?\n/)) {
      if (!line.trim().startsWith("{")) continue;
      try {
        const item = JSON.parse(line);
        const payload = item.payload || {};
        if (item.type === "event_msg" && payload.type === "task_started") {
          activeTurn = { turnId: payload.turn_id, startedAt: payload.started_at ? new Date(Number(payload.started_at) * 1000).toISOString() : item.timestamp };
          latestOutput = "";
          latestOutputAt = null;
        }
        if (item.type === "event_msg" && ["task_complete", "turn_aborted"].includes(payload.type) && activeTurn?.turnId === payload.turn_id) {
          activeTurn = null;
        }
        if (activeTurn && item.type === "response_item" && payload.type === "message" && payload.role === "assistant") {
          const text = Array.isArray(payload.content)
            ? payload.content.filter((part) => ["output_text", "text"].includes(part.type)).map((part) => part.text).join("\n").trim()
            : "";
          if (text) { latestOutput = text; latestOutputAt = item.timestamp || latestOutputAt; }
        }
        if (activeTurn && item.type === "event_msg" && payload.type === "agent_message" && payload.message) {
          latestOutput = String(payload.message).trim();
          latestOutputAt = item.timestamp || latestOutputAt;
        }
      } catch {
        // The first/last line may be partial when reading a tail of a live file.
      }
    }
    return activeTurn ? { ...activeTurn, latestOutput, latestOutputAt } : null;
  } catch {
    return null;
  }
}

export function readCodexNames(file) {
  const names = new Map();
  try {
    for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
      if (!line.trim()) continue;
      try {
        const item = JSON.parse(line);
        if (item.id && item.thread_name) names.set(item.id, item.thread_name);
      } catch {
        // Ignore a partially written trailing line.
      }
    }
  } catch {
    // The database title remains a valid fallback when the index is absent.
  }
  return names;
}

export function readCodexTasks(config) {
  if (!fs.existsSync(config.codexStateDb) || !fs.existsSync(config.codexHistoryDb)) {
    return { available: false, tasks: [], detail: "Codex history database was not found" };
  }

  let history;
  let state;
  try {
    history = new DatabaseSync(config.codexHistoryDb, { readOnly: true });
    state = new DatabaseSync(config.codexStateDb, { readOnly: true });
    const displayNames = readCodexNames(config.codexSessionIndex);
    const turns = history.prepare(`
      SELECT thread_id, turn_id, started_at
      FROM thread_turns
      WHERE status = 'inProgress'
      ORDER BY started_at DESC
    `).all();
    const threadQuery = state.prepare(`
      SELECT id, name, title, cwd, model, reasoning_effort
      FROM threads WHERE id = ?
    `);
    const latestOutputQuery = history.prepare(`
      SELECT item_json, created_at_ms
      FROM thread_items
      WHERE thread_id = ? AND turn_id = ? AND item_type = 'agentMessage'
      ORDER BY created_at_ms DESC, rollout_ordinal DESC
      LIMIT 1
    `);
    const tasks = turns.map((turn) => {
      const thread = threadQuery.get(turn.thread_id) || {};
      const outputRow = latestOutputQuery.get(turn.thread_id, turn.turn_id);
      let latestOutput = "";
      if (outputRow?.item_json) {
        try {
          latestOutput = String(JSON.parse(outputRow.item_json).text || "").trim();
        } catch {
          // Ignore malformed history entries and keep the task visible.
        }
      }
      return {
        id: `codex:${turn.thread_id}:${turn.turn_id}`,
        externalId: turn.thread_id,
        source: "codex",
        title: displayNames.get(turn.thread_id) || thread.name || thread.title || "Untitled Codex task",
        workspace: normalizePath(thread.cwd),
        status: "running",
        confidence: "automatic",
        startedAt: turn.started_at ? new Date(Number(turn.started_at) * 1000).toISOString() : null,
        latestOutput: latestOutput.slice(0, 2000),
        latestOutputAt: outputRow?.created_at_ms ? new Date(Number(outputRow.created_at_ms)).toISOString() : null,
        model: thread.model || null,
        reasoningEffort: thread.reasoning_effort || null
      };
    });
    const hasProjectedTurns = history.prepare("SELECT 1 FROM thread_turns WHERE thread_id = ? LIMIT 1");
    const unprojectedThreads = state.prepare(`
      SELECT id, name, title, cwd, model, reasoning_effort, rollout_path
      FROM threads
      WHERE archived = 0 AND rollout_path <> ''
      ORDER BY MAX(COALESCE(updated_at_ms, 0), COALESCE(recency_at_ms, 0), COALESCE(updated_at, 0) * 1000) DESC
      LIMIT 30
    `).all();
    for (const thread of unprojectedThreads) {
      if (hasProjectedTurns.get(thread.id)) continue;
      const rollout = parseCodexRollout(thread.rollout_path);
      if (!rollout) continue;
      tasks.push({
        id: `codex:${thread.id}:${rollout.turnId}`,
        externalId: thread.id,
        source: "codex",
        title: displayNames.get(thread.id) || thread.name || thread.title || "Untitled Codex task",
        workspace: normalizePath(thread.cwd),
        status: "running",
        confidence: "automatic",
        startedAt: rollout.startedAt,
        latestOutput: String(rollout.latestOutput || "").slice(0, 2000),
        latestOutputAt: rollout.latestOutputAt,
        model: thread.model || null,
        reasoningEffort: thread.reasoning_effort || null
      });
    }
    return { available: true, tasks, detail: `${tasks.length} active turn${tasks.length === 1 ? "" : "s"}` };
  } catch (error) {
    return { available: false, tasks: [], detail: error.message };
  } finally {
    history?.close();
    state?.close();
  }
}
