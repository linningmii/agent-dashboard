import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Store } from "../src/store.mjs";

function fixture() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "agent-dashboard-"));
  return new Store(path.join(directory, "state.json"), { minimumRunning: 3, reminderCooldownMinutes: 15, windowsNotifications: false });
}

test("creates and completes reported tasks", () => {
  const store = fixture();
  const task = store.createTask({ source: "copilot", title: "Test task", latestOutput: "Starting", leaseMinutes: 60 });
  assert.equal(task.status, "running");
  assert.equal(task.latestOutput, "Starting");
  assert.equal(store.snapshot().tasks.length, 1);
  assert.equal(store.heartbeat(task.id, 5, "Halfway").latestOutput, "Halfway");
  assert.equal(store.updateTask(task.id, { status: "completed" }).status, "completed");
});

test("validates settings boundaries", () => {
  const store = fixture();
  store.updateSettings({ minimumRunning: 8, reminderCooldownMinutes: 30 });
  assert.equal(store.snapshot().settings.minimumRunning, 8);
  store.updateSettings({ minimumRunning: 99, reminderCooldownMinutes: 0 });
  assert.equal(store.snapshot().settings.minimumRunning, 8);
  assert.equal(store.snapshot().settings.reminderCooldownMinutes, 30);
});

test("records and clears automatic completion notifications", () => {
  const store = fixture();
  const task = { id: "codex:thread:turn", source: "codex", title: "Finished work", workspace: "C:/repo", status: "running", confidence: "automatic", startedAt: new Date(Date.now() - 60_000).toISOString(), latestOutput: "Done" };
  store.reconcileAutomatic("codex", [task]);
  assert.equal(store.snapshot().completions.length, 0);
  store.reconcileAutomatic("codex", []);
  const completion = store.snapshot().completions[0];
  assert.equal(completion.title, "Finished work");
  assert.equal(store.clearCompletion(completion.id), 1);
  assert.equal(store.snapshot().completions.length, 0);
});

test("records a reported task when completed", () => {
  const store = fixture();
  const task = store.createTask({ source: "copilot", title: "Reported work" });
  store.updateTask(task.id, { status: "completed" });
  assert.equal(store.snapshot().completions[0].taskId, task.id);
});
