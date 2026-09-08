import fs from "node:fs";
import path from "node:path";
import { config } from "../src/config.mjs";
import { Store } from "../src/store.mjs";
import { collectLocal } from "../src/local-collector.mjs";
import { createCollectorClient } from "../src/collector-client.mjs";

const root = path.resolve(import.meta.dirname, "..");
const settings = JSON.parse(fs.readFileSync(process.env.AGENT_COLLECTOR_CONFIG || path.join(root, "collector.json"), "utf8"));
const client = createCollectorClient(settings);
const store = new Store(process.env.AGENT_COLLECTOR_STATE || path.join(root, "data/collector-state.json"), config.defaults);
const once = process.argv.includes("--once");
let sessionId = null; let sequence = 0; let pending = null; let stopped = false; let timer;
let delay = 10000;
async function poll() {
  if (stopped) return;
  try {
    if (!sessionId) {
      const session = await client.send("/v1/devices/" + settings.deviceId + "/sessions", "POST", {}, settings.token);
      sessionId = session.sessionId;
    }
    // Retry the same batch after a lost response; sequence deduplication prevents duplicate completions.
    if (!pending) {
      const captured = collectLocal(config, store);
      const trim = task => ({ ...task, title: String(task.title || "Untitled task").slice(0, 2000), workspace: String(task.workspace || "").slice(0, 1000), latestOutput: String(task.latestOutput || "").slice(0, 4000) });
      pending = { sessionId, sequence: ++sequence, sources: captured.sources, tasks: captured.tasks.filter(task => task.status !== "completed").map(trim), completions: captured.completions.map(trim) };
    }
    await client.send("/v1/devices/" + settings.deviceId + "/snapshot", "PUT", pending, settings.token);
    for (const completion of pending.completions) store.clearCompletion(completion.id);
    pending = null; delay = 10000;
    console.log(new Date().toISOString() + " — device snapshot delivered");
  } catch (error) {
    console.error(error.status ? "Collector HTTP " + error.status + ". Check device credentials and tunnel sign-in." : "Collector disconnected. Check the ingestion URL, network, and devtunnel user show.");
    if (error.status === 409) { console.error("This device session was replaced. Stop duplicate collectors before restarting."); stopped = true; process.exitCode = 1; return; }
    delay = Math.min(delay * 2, 30000);
    if (once) process.exitCode = 1;
  }
  if (!stopped && !once) timer = setTimeout(poll, delay);
}
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => { stopped = true; clearTimeout(timer); });
await poll();
