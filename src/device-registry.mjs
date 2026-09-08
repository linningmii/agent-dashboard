import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { createHash, randomBytes, randomUUID, timingSafeEqual } from "node:crypto";

const hash = value => createHash("sha256").update(value).digest("hex");
const equal = (a, b) => typeof a === "string" && typeof b === "string" && a.length === b.length && timingSafeEqual(Buffer.from(a), Buffer.from(b));
const fail = (status, message) => { throw Object.assign(new Error(message), { status }); };
const object = value => value && typeof value === "object" && !Array.isArray(value);
function text(value, label, maximum, required = false) {
  if (value == null && !required) return "";
  if (typeof value !== "string" || value.length > maximum || (required && !value.trim())) fail(400, "Invalid " + label);
  return value.trim();
}
function iso(value) {
  if (value == null) return null;
  if (typeof value !== "string" || !Number.isFinite(Date.parse(value))) fail(400, "Invalid task timestamp");
  return new Date(value).toISOString();
}
export const taskKey = (deviceId, taskId) => "device:" + deviceId + ":" + encodeURIComponent(taskId);

export function normalizeReport(input) {
  if (!object(input) || !Number.isSafeInteger(input.sequence) || input.sequence < 1 || typeof input.sessionId !== "string") fail(400, "Invalid report sequence or session");
  if (!object(input.sources) || Object.keys(input.sources).length > 20 || !Array.isArray(input.tasks) || input.tasks.length > 500 || !Array.isArray(input.completions) || input.completions.length > 100) fail(400, "Invalid report collections");
  const sources = {};
  for (const [id, info] of Object.entries(input.sources)) {
    if (!/^[a-z][a-z0-9-]{0,39}$/.test(id) || !object(info) || typeof info.available !== "boolean") fail(400, "Invalid source");
    sources[id] = { available: info.available, automatic: info.automatic === true, detail: text(info.detail, "source detail", 500) };
  }
  function task(item) {
    if (!object(item) || !Object.hasOwn(sources, item.source) || !["running", "paused", "stale", "completed"].includes(item.status)) fail(400, "Invalid task source or status");
    return {
      id: text(item.id, "task ID", 300, true), source: item.source, status: item.status,
      title: text(item.title, "task title", 2000, true), workspace: text(item.workspace, "workspace", 1000),
      confidence: item.confidence === "reported" ? "reported" : "automatic",
      startedAt: iso(item.startedAt || item.createdAt), latestOutput: text(item.latestOutput, "latest output", 4000),
      latestOutputAt: iso(item.latestOutputAt), model: text(item.model, "model", 100)
    };
  }
  const tasks = input.tasks.map(task);
  if (new Set(tasks.map(item => item.id)).size !== tasks.length) fail(400, "Duplicate task IDs in report");
  const completions = input.completions.map(item => {
    if (!object(item)) fail(400, "Invalid completion");
    return { ...task({ ...item, status: "completed", id: item.taskId }), eventId: text(item.id, "completion ID", 300, true),
      completedAt: iso(item.completedAt) || fail(400, "Missing completion time") };
  });
  return { sessionId: input.sessionId, sequence: input.sequence, sources, tasks, completions };
}

export class DeviceRegistry {
  constructor(file, { now = Date.now, leaseMs = 45000, localName = os.hostname() } = {}) {
    this.file = file; this.now = now; this.leaseMs = leaseMs;
    try { this.state = JSON.parse(fs.readFileSync(file, "utf8")); }
    catch (error) {
      if (error.code !== "ENOENT") throw error;
      this.state = { local: { id: randomUUID(), name: localName }, devices: {}, pairings: [], completions: [] };
      this.save();
    }
  }
  save() {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const temp = this.file + "." + process.pid + ".tmp";
    fs.writeFileSync(temp, JSON.stringify(this.state, null, 2), { mode: 0o600 });
    fs.renameSync(temp, this.file);
  }
  local() { return { ...this.state.local, local: true, status: "online" }; }
  pair() {
    const code = randomBytes(24).toString("base64url");
    const expiresAt = this.now() + 10 * 60000;
    this.state.pairings = this.state.pairings.filter(item => item.expiresAt > this.now()).slice(-19);
    this.state.pairings.push({ hash: hash(code), expiresAt }); this.save();
    return { code, expiresAt: new Date(expiresAt).toISOString() };
  }
  enroll(input) {
    if (!object(input)) fail(400, "Invalid enrollment");
    const code = text(input.code, "pairing code", 100, true);
    const name = text(input.name, "device name", 100, true);
    const pairing = this.state.pairings.find(item => item.expiresAt > this.now() && equal(item.hash, hash(code)));
    if (!pairing) fail(401, "Pairing code expired or invalid");
    const id = randomUUID(); const token = randomBytes(32).toString("base64url");
    this.state.devices[id] = { id, name, tokenHash: hash(token), enrolledAt: this.now(), lastSeenAt: null, sources: {}, tasks: [], sequence: 0, sessionId: null, seenCompletions: [], revoked: false };
    this.state.pairings = this.state.pairings.filter(item => item !== pairing); this.save();
    return { deviceId: id, token, heartbeatSeconds: 10, offlineAfterSeconds: this.leaseMs / 1000 };
  }
  authenticate(id, token) {
    const device = Object.hasOwn(this.state.devices, id) ? this.state.devices[id] : null;
    if (!device || device.revoked || typeof token !== "string" || !equal(device.tokenHash, hash(token))) fail(401, "Invalid device credentials");
    return device;
  }
  openSession(id, token) {
    const device = this.authenticate(id, token);
    device.sessionId = randomUUID(); device.sequence = 0;
    // A new collector process does not extend liveness until a valid snapshot arrives.
    this.save(); return { sessionId: device.sessionId, heartbeatSeconds: 10 };
  }
  report(id, token, input) {
    const device = this.authenticate(id, token);
    const report = normalizeReport(input);
    if (device.sessionId !== report.sessionId) fail(409, "Collector session replaced; restart the collector");
    if (report.sequence < device.sequence) fail(409, "Out-of-order report");
    if (report.sequence === device.sequence) return { accepted: true, duplicate: true, sequence: device.sequence };
    const seen = new Set(device.seenCompletions);
    for (const item of report.completions) {
      if (seen.has(item.eventId)) continue;
      seen.add(item.eventId);
      this.state.completions.unshift({ ...item, id: taskKey(id, "completion:" + item.eventId), taskId: taskKey(id, item.id),
        deviceId: id, deviceName: device.name, managedLocally: false });
    }
    this.state.completions = this.state.completions.sort((a, b) => Date.parse(b.completedAt) - Date.parse(a.completedAt)).slice(0, 100);
    device.seenCompletions = [...seen];
    device.tasks = report.tasks; device.sources = report.sources; device.sequence = report.sequence;
    device.lastSeenAt = this.now(); this.save();
    return { accepted: true, sequence: device.sequence };
  }
  view() {
    const devices = []; const tasks = [];
    for (const device of Object.values(this.state.devices)) {
      if (device.revoked) continue;
      const online = device.lastSeenAt !== null && this.now() - device.lastSeenAt < this.leaseMs;
      const runningCount = online ? device.tasks.filter(task => task.status === "running" && (task.confidence === "reported" || device.sources[task.source]?.available)).length : 0;
      devices.push({ id: device.id, name: device.name, local: false, status: device.lastSeenAt === null ? "pending" : online ? "online" : "offline",
        runningCount, lastSeenAt: device.lastSeenAt === null ? null : new Date(device.lastSeenAt).toISOString(), sources: device.sources });
      for (const task of device.tasks) tasks.push({ ...task, id: taskKey(device.id, task.id), deviceId: device.id, deviceName: device.name,
        status: !online || (task.confidence !== "reported" && !device.sources[task.source]?.available) ? "stale" : task.status, managedLocally: false });
    }
    return { devices, tasks, completions: structuredClone(this.state.completions) };
  }
  revoke(id) {
    const device = Object.hasOwn(this.state.devices, id) ? this.state.devices[id] : null; if (!device) fail(404, "Device not found");
    device.revoked = true; device.tokenHash = null; device.sessionId = null; device.tasks = []; this.save();
  }
  clearCompletion(id) {
    const before = this.state.completions.length;
    this.state.completions = id ? this.state.completions.filter(item => item.id !== id) : [];
    if (this.state.completions.length !== before) this.save();
    return before - this.state.completions.length;
  }
}
