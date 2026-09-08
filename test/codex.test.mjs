import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { parseCodexRollout, readCodexNames, readCodexTasks } from "../src/adapters/codex.mjs";

test("reads only active Codex turns", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "codex-adapter-"));
  const statePath = path.join(directory, "state.sqlite");
  const historyPath = path.join(directory, "history.sqlite");
  const state = new DatabaseSync(statePath);
  state.exec("CREATE TABLE threads (id TEXT PRIMARY KEY, name TEXT, title TEXT, cwd TEXT, model TEXT, reasoning_effort TEXT, archived INTEGER DEFAULT 0, rollout_path TEXT DEFAULT '', updated_at_ms INTEGER DEFAULT 0, recency_at_ms INTEGER DEFAULT 0, updated_at INTEGER DEFAULT 0); INSERT INTO threads (id,title,cwd,model,reasoning_effort) VALUES ('one','Active task','C:/repo','gpt-test','high')");
  state.close();
  const history = new DatabaseSync(historyPath);
  history.exec("CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT, status TEXT, started_at INTEGER); CREATE TABLE thread_items (thread_id TEXT, turn_id TEXT, item_type TEXT, created_at_ms INTEGER, rollout_ordinal INTEGER, item_json TEXT); INSERT INTO thread_turns VALUES ('one','turn-a','inProgress',100),('one','turn-b','completed',90); INSERT INTO thread_items VALUES ('one','turn-a','agentMessage',110000,1,'{\"text\":\"Working on it\"}')");
  history.close();
  const indexPath = path.join(directory, "session_index.jsonl");
  fs.writeFileSync(indexPath, JSON.stringify({ id: "one", thread_name: "Sidebar name" }));
  const result = readCodexTasks({ codexStateDb: statePath, codexHistoryDb: historyPath, codexSessionIndex: indexPath });
  assert.equal(result.available, true);
  assert.equal(result.tasks.length, 1);
  assert.equal(result.tasks[0].title, "Sidebar name");
  assert.equal(result.tasks[0].latestOutput, "Working on it");
});

test("keeps the latest indexed Codex display name", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "codex-index-"));
  const file = path.join(directory, "session_index.jsonl");
  fs.writeFileSync(file, [
    JSON.stringify({ id: "one", thread_name: "Old name" }),
    JSON.stringify({ id: "one", thread_name: "Current name" })
  ].join("\n"));
  assert.equal(readCodexNames(file).get("one"), "Current name");
});

test("detects an active turn from an unprojected rollout", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "codex-rollout-"));
  const file = path.join(directory, "rollout.jsonl");
  fs.writeFileSync(file, [
    JSON.stringify({ timestamp: "2026-01-01T00:00:00Z", type: "event_msg", payload: { type: "task_started", turn_id: "turn-a", started_at: 1767225600 } }),
    JSON.stringify({ timestamp: "2026-01-01T00:00:01Z", type: "event_msg", payload: { type: "agent_message", message: "Working" } })
  ].join("\n"));
  const result = parseCodexRollout(file);
  assert.equal(result.turnId, "turn-a");
  assert.equal(result.latestOutput, "Working");
});

test("does not report a completed rollout turn", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "codex-rollout-"));
  const file = path.join(directory, "rollout.jsonl");
  fs.writeFileSync(file, [
    JSON.stringify({ type: "event_msg", payload: { type: "task_started", turn_id: "turn-a", started_at: 1 } }),
    JSON.stringify({ type: "event_msg", payload: { type: "task_complete", turn_id: "turn-a" } })
  ].join("\n"));
  assert.equal(parseCodexRollout(file), null);
});
