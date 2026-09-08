import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { parseClaudeSession } from "../src/adapters/claude.mjs";

test("parses Claude session title, workspace, and latest output", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "claude-adapter-"));
  const file = path.join(directory, "session-1.jsonl");
  fs.writeFileSync(file, [
    JSON.stringify({ type: "last-prompt", lastPrompt: "Build the feature", sessionId: "session-1", timestamp: "2026-01-01T00:00:00Z", cwd: "C:/repo" }),
    JSON.stringify({ type: "assistant", sessionId: "session-1", timestamp: "2026-01-01T00:01:00Z", cwd: "C:/repo", message: { content: [{ type: "text", text: "Implementation is underway" }] } }),
    JSON.stringify({ type: "ai-title", aiTitle: "Feature implementation", sessionId: "session-1" })
  ].join("\n"));
  const session = parseClaudeSession(file);
  assert.equal(session.title, "Feature implementation");
  assert.equal(session.cwd, "C:/repo");
  assert.equal(session.latestOutput, "Implementation is underway");
});
