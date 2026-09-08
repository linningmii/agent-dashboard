import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { DeviceRegistry } from "../src/device-registry.mjs";
import { createIngestionServer } from "../src/ingestion.mjs";
import { createCollectorClient, validateCollectorUrl } from "../src/collector-client.mjs";
import { DashboardService } from "../src/service.mjs";
import { Store } from "../src/store.mjs";
import { execFile } from "node:child_process";
import { promisify } from "node:util";

function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "agent-devices-"));
  let time = 100000;
  const registry = new DeviceRegistry(path.join(dir, "devices.json"), { now: () => time, localName: "Hub" });
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const enroll = name => registry.enroll({ code: registry.pair().code, name });
  return { registry, dir, enroll, advance: n => { time += n; } };
}
function report(sessionId, sequence = 1, tasks = [{ id: "same-thread", title: "Work", source: "codex", status: "running" }], completions = []) {
  return { sessionId, sequence, sources: { codex: { available: true, automatic: true } }, tasks, completions };
}
test("pairing codes are single use and expire; state exposes no credentials", t => {
  const f = fixture(t); const pair = f.registry.pair();
  f.registry.enroll({ code: pair.code, name: "Laptop" });
  assert.throws(() => f.registry.enroll({ code: pair.code, name: "Again" }), { status: 401 });
  const expired = f.registry.pair(); f.advance(600001);
  assert.throws(() => f.registry.enroll({ code: expired.code, name: "Expired" }), { status: 401 });
  assert.equal(JSON.stringify(f.registry.view()).includes("tokenHash"), false);
  assert.equal(JSON.stringify(f.registry.view()).includes(pair.code), false);
});
test("two devices with identical task IDs count separately; offline is not completion", t => {
  const f = fixture(t);
  for (const name of ["Laptop", "Desktop"]) {
    const device = f.enroll(name); const session = f.registry.openSession(device.deviceId, device.token);
    f.registry.report(device.deviceId, device.token, report(session.sessionId));
  }
  let view = f.registry.view();
  assert.equal(view.tasks.length, 2); assert.equal(new Set(view.tasks.map(task => task.id)).size, 2);
  f.advance(45001); view = f.registry.view();
  assert.equal(view.devices.every(device => device.status === "offline"), true);
  assert.equal(view.tasks.every(task => task.status === "stale"), true);
  assert.equal(view.completions.length, 0);
});
test("replays cannot extend liveness; new sessions fence old collectors", t => {
  const f = fixture(t); const d = f.enroll("Laptop"); const s = f.registry.openSession(d.deviceId, d.token);
  f.registry.report(d.deviceId, d.token, report(s.sessionId, 2));
  assert.throws(() => f.registry.report(d.deviceId, d.token, report(s.sessionId, 1)), { status: 409 });
  f.advance(45001); f.registry.report(d.deviceId, d.token, report(s.sessionId, 2));
  assert.equal(f.registry.view().devices[0].status, "offline");
  f.registry.openSession(d.deviceId, d.token);
  assert.throws(() => f.registry.report(d.deviceId, d.token, report(s.sessionId, 3)), { status: 409 });
});
test("acknowledged completions never reappear on retries or after hub restart", t => {
  const f = fixture(t); const d = f.enroll("Laptop"); const s = f.registry.openSession(d.deviceId, d.token);
  const done = { id: "event-1", taskId: "same-thread", title: "Done", source: "codex", completedAt: new Date().toISOString(), latestOutput: "Final output" };
  f.registry.report(d.deviceId, d.token, report(s.sessionId, 1, [], [done]));
  const completion = f.registry.view().completions[0];
  assert.equal(completion.deviceName, "Laptop"); f.registry.clearCompletion(completion.id);
  const reloaded = new DeviceRegistry(path.join(f.dir, "devices.json"));
  reloaded.report(d.deviceId, d.token, report(s.sessionId, 2, [], [done]));
  assert.equal(reloaded.view().completions.length, 0);
  assert.equal(reloaded.local().id, f.registry.local().id);
});
test("device credentials cannot impersonate other devices and revocation stops reports", t => {
  const f = fixture(t); const a = f.enroll("A"); const b = f.enroll("B");
  assert.throws(() => f.registry.openSession(b.deviceId, a.token), { status: 401 });
  f.registry.revoke(a.deviceId);
  assert.throws(() => f.registry.openSession(a.deviceId, a.token), { status: 401 });
  assert.throws(() => f.registry.revoke("constructor"), { status: 404 });
});
test("invalid reports do not replace the last valid task snapshot", t => {
  const f = fixture(t); const d = f.enroll("Laptop"); const s = f.registry.openSession(d.deviceId, d.token);
  f.registry.report(d.deviceId, d.token, report(s.sessionId));
  const bad = report(s.sessionId, 2); bad.tasks[0].source = "not-declared";
  assert.throws(() => f.registry.report(d.deviceId, d.token, bad), { status: 400 });
  assert.equal(f.registry.view().tasks.length, 1);
  assert.throws(() => f.registry.report(d.deviceId, d.token, { ...report(s.sessionId, 2), completions: [null] }), { status: 400 });
});
test("collector credentials are not sent to insecure or redirected destinations", async () => {
  assert.throws(() => validateCollectorUrl("http://remote.example", "id.jpe1"));
  assert.throws(() => validateCollectorUrl("https://id-4319.jpe1.devtunnels.ms", "id.usw2"));
  const client = createCollectorClient({ url: "http://127.0.0.1:4319" }, { request() { throw new Error("Request must not execute"); } });
  await assert.rejects(client.send("https://unrelated.test/", "POST", {}, "device-secret"), /configured ingestion origin/);
});
test("ingestion supports the collector protocol but cannot serve UI or administrative routes", async t => {
  const f = fixture(t); const server = createIngestionServer(f.registry);
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const url = "http://127.0.0.1:" + server.address().port;
  const client = createCollectorClient({ url });
  const d = await client.send("/v1/devices/register", "POST", { code: f.registry.pair().code, name: "Remote" });
  const session = await client.send("/v1/devices/" + d.deviceId + "/sessions", "POST", {}, d.token);
  await client.send("/v1/devices/" + d.deviceId + "/snapshot", "PUT", report(session.sessionId), d.token);
  assert.equal(f.registry.view().tasks[0].deviceName, "Remote");
  for (const route of ["/", "/api/status", "/api/devices/pair", "/api/completions"]) assert.equal((await fetch(url + route)).status, 404);
  assert.equal((await fetch(url + "/health", { headers: { Origin: "https://unrelated.test" } })).status, 403);
  assert.equal((await fetch(url + "/v1/devices/" + d.deviceId + "/snapshot", { method: "PUT", headers: { "content-type": "application/json" }, body: "{}" })).status, 401);
  assert.equal((await fetch(url + "/v1/devices/" + d.deviceId + "/snapshot", { method: "PUT", headers: { "content-type": "application/json", Authorization: "Bearer " + d.token }, body: "{}" })).status, 400);
});
test("hub counts local plus remote devices toward one global minimum", t => {
  const f = fixture(t); const d = f.enroll("Remote"); const session = f.registry.openSession(d.deviceId, d.token);
  f.registry.report(d.deviceId, d.token, report(session.sessionId));
  const store = new Store(path.join(f.dir, "state.json"), { minimumRunning: 3 });
  const local = { tasks: [{ id: "local-1", source: "codex", status: "running" }, { id: "local-2", source: "codex", status: "running" }], sources: { codex: { available: true } }, completions: [] };
  const service = new DashboardService({}, store, f.registry, () => local);
  let view = service.refresh(); assert.equal(view.runningCount, 3); assert.equal(view.healthy, true); assert.equal(view.devices.length, 2);
  f.advance(45001); view = service.refresh(); assert.equal(view.runningCount, 2); assert.equal(view.healthy, false); assert.equal(view.completions.length, 0);
});
test("actual enrollment and collector commands deliver an isolated local snapshot", async t => {
  const f = fixture(t); const server = createIngestionServer(f.registry);
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const collectorFile = path.join(f.dir, "collector.json");
  const env = { ...process.env, AGENT_PAIR_CODE: f.registry.pair().code, AGENT_COLLECTOR_CONFIG: collectorFile,
    AGENT_COLLECTOR_STATE: path.join(f.dir, "collector-state.json"), CODEX_STATE_DB: path.join(f.dir, "missing-state.db"),
    CODEX_HISTORY_DB: path.join(f.dir, "missing-history.db"), COPILOT_STORAGE: path.join(f.dir, "missing-copilot"), CLAUDE_CONFIG_DIR: path.join(f.dir, "missing-claude") };
  const execute = promisify(execFile);
  await execute(process.execPath, ["scripts/enroll-device.mjs", "--url", "http://127.0.0.1:" + server.address().port, "--name", "CLI test device"], { env, timeout: 30000 });
  const credentials = JSON.parse(fs.readFileSync(collectorFile, "utf8"));
  const result = await execute(process.execPath, ["--experimental-sqlite", "scripts/collector.mjs", "--once"], { env, timeout: 30000 });
  assert.equal(result.stdout.includes(credentials.token), false);
  assert.equal(f.registry.view().devices[0].name, "CLI test device");
  assert.equal(f.registry.view().devices[0].status, "online");
});
