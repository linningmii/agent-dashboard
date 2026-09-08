// Sends one synthetic device through the real ingestion tunnel, then revokes it.
// Pairing and device credentials stay in memory and are never printed.
import assert from "node:assert/strict";
import { createCollectorClient } from "../src/collector-client.mjs";
const hub = "http://127.0.0.1:4317";
const admin = async (route, options) => {
  const result = await fetch(hub + route, { ...options, headers: { "content-type": "application/json" }, signal: AbortSignal.timeout(15000) });
  assert.equal(result.ok, true); return result.json();
};
const tunnels = await admin("/api/tunnels");
assert.equal(tunnels.ingestion.state, "hosting");
const gate = await fetch(tunnels.ingestion.url + "/health", { redirect: "manual", headers: { Accept: "application/json" }, signal: AbortSignal.timeout(30000) });
assert.ok([302, 401, 403].includes(gate.status));
console.log("Ingestion tunnel requires Microsoft authentication:", gate.status);
const client = createCollectorClient({ url: tunnels.ingestion.url, tunnelId: tunnels.ingestion.id });
const pairing = await admin("/api/devices/pair", { method: "POST", body: "{}" });
const device = await client.send("/v1/devices/register", "POST", { code: pairing.code, name: "Temporary ingestion verification" });
try {
  const session = await client.send("/v1/devices/" + device.deviceId + "/sessions", "POST", {}, device.token);
  const payload = { sessionId: session.sessionId, sequence: 1, sources: { codex: { available: true } },
    tasks: [{ id: "verification-only", source: "codex", title: "Synthetic tunnel verification", status: "running", latestOutput: "Checking registration and heartbeat delivery" }], completions: [] };
  await client.send("/v1/devices/" + device.deviceId + "/snapshot", "PUT", payload, device.token);
  let state = await admin("/api/status");
  assert.ok(state.devices.some(item => item.id === device.deviceId && item.status === "online"));
  assert.ok(state.tasks.some(item => item.deviceId === device.deviceId && item.status === "running"));
  console.log("Remote registration, session, snapshot, and aggregate: verified");
  await client.send("/v1/devices/" + device.deviceId + "/snapshot", "PUT", { ...payload, sequence: 2, tasks: [] }, device.token);
  state = await admin("/api/status");
  assert.equal(state.completions.some(item => item.deviceId === device.deviceId), false);
  await assert.rejects(client.send("/api/status", "GET"), { status: 404 });
  console.log("Ingestion does not expose dashboard reads: verified");
} finally {
  await admin("/api/devices/" + device.deviceId, { method: "DELETE" });
  console.log("Temporary verification device disconnected; no task history retained.");
}
