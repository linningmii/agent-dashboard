import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { createTunnelHost, validateTunnelConfig } from "../src/tunnel.mjs";

const config = { enabled: true, id: "dashboard.jpe1", port: 4317 };
function fixture(t, overrides = {}) {
  const calls = [];
  const child = new EventEmitter();
  child.stdout = new EventEmitter(); child.stderr = new EventEmitter();
  child.kill = () => { child.killed = true; };
  let launches = 0;
  const run = async (_command, args) => {
    calls.push(args);
    const data = args[0] === "show" ? { tunnel: { accessControl: [], ...overrides.tunnel } }
      : args[1] === "list" ? { ports: overrides.ports || [{ portNumber: 4317, protocol: "http" }] }
      : args[1] === "show" ? { port: { portNumber: 4317, protocol: "http", accessControl: [], requestTimeoutSeconds: 0, ...overrides.port } }
      : {};
    return { stdout: JSON.stringify(data) };
  };
  const host = createTunnelHost(config, { run, launch: (_cmd, args, options) => {
    launches++; assert.deepEqual(args, ["host", config.id]); assert.equal(options.windowsHide, true); return child;
  }, log() {} });
  t.after(() => host.stop());
  return { host, child, calls, launches: () => launches };
}
test("tunnel config requires a stable qualified ID and the local port", () => {
  assert.deepEqual(validateTunnelConfig(null, 4317), { enabled: false });
  assert.throws(() => validateTunnelConfig({ enabled: true, id: "", port: 4317 }, 4317));
  assert.throws(() => validateTunnelConfig({ ...config, port: 80 }, 4317));
  assert.deepEqual(validateTunnelConfig(config, 4317), config);
});
test("hosts only the saved private tunnel, renews it, and stops its child", async t => {
  const f = fixture(t); f.host.start(); f.host.start(); await new Promise(setImmediate);
  assert.equal(f.launches(), 1);
  assert.ok(f.calls.some(args => args[0] === "update" && args.includes("30d")));
  f.child.stdout.emit("data", "Hosting port 4317 at https://dashboard-4317.jpe1.devtunnels.ms/\n");
  assert.equal(f.host.snapshot().state, "hosting");
  assert.equal(f.host.snapshot().url, "https://dashboard-4317.jpe1.devtunnels.ms/");
  f.host.stop(); assert.equal(f.child.killed, true);
  f.child.emit("close", 0); assert.equal(f.host.snapshot().state, "stopped");
});
test("does not host a tunnel with anonymous or expanded access", async t => {
  for (const overrides of [{ tunnel: { accessControl: [{ type: "anonymous" }] } }, { port: { accessControl: [{ type: "users" }] } }, { ports: [{ portNumber: 4317, protocol: "http" }, { portNumber: 8080, protocol: "http" }] }]) {
    const f = fixture(t, overrides); f.host.start(); await new Promise(setImmediate);
    assert.equal(f.launches(), 0); assert.equal(f.host.snapshot().state, "retrying"); f.host.stop();
  }
});
test("an exited host schedules reconnection without creating another tunnel", async t => {
  const f = fixture(t); f.host.start(); await new Promise(setImmediate);
  f.child.emit("close", 1);
  assert.equal(f.host.snapshot().state, "retrying");
  assert.equal(f.calls.some(args => args[0] === "create"), false);
});
