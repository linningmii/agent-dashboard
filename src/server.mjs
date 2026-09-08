import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { config } from "./config.mjs";
import { Store } from "./store.mjs";
import { DashboardService } from "./service.mjs";
import { Notifier } from "./notifier.mjs";
import { createTunnelHost } from "./tunnel.mjs";
import { DeviceRegistry } from "./device-registry.mjs";
import { createIngestionServer } from "./ingestion.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const publicRoot = path.join(root, "public");
const store = new Store(config.dataFile, config.defaults);
const registry = new DeviceRegistry(config.deviceRegistryFile, { localName: config.deviceName });
const service = new DashboardService(config, store, registry);
const notifier = new Notifier(root);
const tunnel = createTunnelHost(config.tunnel);
const ingestionTunnel = createTunnelHost(config.ingestionTunnel, { log: message => console.log("Ingestion: " + message) });
const clients = new Set();
let lastSerialized = "";

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".svg": "image/svg+xml"
};

function sendJson(response, status, body) {
  response.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" });
  response.end(JSON.stringify(body));
}

async function readJson(request) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > 64 * 1024) throw new Error("Request body is too large");
    chunks.push(chunk);
  }
  const raw = Buffer.concat(chunks).toString("utf8");
  return raw ? JSON.parse(raw) : {};
}

function publish(snapshot) {
  const serialized = JSON.stringify(snapshot);
  const stateKey = JSON.stringify({ ...snapshot, generatedAt: undefined });
  if (stateKey === lastSerialized) return;
  lastSerialized = stateKey;
  for (const client of clients) client.write(`event: snapshot\ndata: ${serialized}\n\n`);
}

function refresh() {
  const snapshot = service.refresh();
  notifier.maybeNotify(snapshot);
  publish(snapshot);
  return snapshot;
}

function serveStatic(urlPath, response) {
  const requested = urlPath === "/" ? "index.html" : urlPath.slice(1);
  const file = path.resolve(publicRoot, requested);
  if (!file.startsWith(publicRoot + path.sep) && file !== path.join(publicRoot, "index.html")) return false;
  try {
    const data = fs.readFileSync(file);
    response.writeHead(200, { "content-type": contentTypes[path.extname(file)] || "application/octet-stream", "cache-control": "no-cache" });
    response.end(data);
    return true;
  } catch (error) {
    if (error.code === "ENOENT") return false;
    throw error;
  }
}

export function createServer() {
  return http.createServer(async (request, response) => {
    const url = new URL(request.url, `http://${request.headers.host || "localhost"}`);
    response.setHeader("x-content-type-options", "nosniff");
    response.setHeader("content-security-policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:");
    try {
      if (!["GET", "HEAD"].includes(request.method) && !request.headers["content-type"]?.startsWith("application/json")) {
        return sendJson(response, 415, { error: "JSON content type required" });
      }
      if (request.method === "GET" && url.pathname === "/api/status") return sendJson(response, 200, refresh());
      if (request.method === "GET" && url.pathname === "/api/tunnel") return sendJson(response, 200, tunnel.snapshot());
      if (request.method === "GET" && url.pathname === "/api/tunnels") return sendJson(response, 200, { ui: tunnel.snapshot(), ingestion: ingestionTunnel.snapshot() });
      if (request.method === "POST" && url.pathname === "/api/devices/pair") {
        const pairing = registry.pair();
        return sendJson(response, 201, { ...pairing, ingestionUrl: ingestionTunnel.snapshot().url, tunnelId: config.ingestionTunnel.id || null });
      }
      const revokeMatch = url.pathname.match(/^\/api\/devices\/([a-z0-9-]+)$/);
      if (request.method === "DELETE" && revokeMatch) {
        registry.revoke(revokeMatch[1]); refresh(); return sendJson(response, 200, { revoked: true });
      }
      if (request.method === "GET" && url.pathname === "/api/events") {
        response.writeHead(200, {
          "content-type": "text/event-stream",
          "cache-control": "no-cache, no-transform",
          connection: "keep-alive"
        });
        clients.add(response);
        response.write(`event: snapshot\ndata: ${JSON.stringify(refresh())}\n\n`);
        const keepAlive = setInterval(() => response.write(": keep-alive\n\n"), 15000);
        request.on("close", () => { clearInterval(keepAlive); clients.delete(response); });
        return;
      }
      if (request.method === "PUT" && url.pathname === "/api/settings") {
        const settings = store.updateSettings(await readJson(request));
        refresh();
        return sendJson(response, 200, settings);
      }
      if (request.method === "POST" && url.pathname === "/api/tasks") {
        const task = store.createTask(await readJson(request));
        refresh();
        return sendJson(response, 201, task);
      }
      const taskMatch = url.pathname.match(/^\/api\/tasks\/([^/]+)$/);
      if (request.method === "PATCH" && taskMatch) {
        const task = store.updateTask(decodeURIComponent(taskMatch[1]), await readJson(request));
        if (!task) return sendJson(response, 404, { error: "Task not found" });
        refresh();
        return sendJson(response, 200, task);
      }
      const heartbeatMatch = url.pathname.match(/^\/api\/tasks\/([^/]+)\/heartbeat$/);
      if (request.method === "POST" && heartbeatMatch) {
        const body = await readJson(request);
        const task = store.heartbeat(decodeURIComponent(heartbeatMatch[1]), body.leaseMinutes, body.latestOutput);
        if (!task) return sendJson(response, 404, { error: "Task not found" });
        refresh();
        return sendJson(response, 200, task);
      }
      const completionMatch = url.pathname.match(/^\/api\/completions(?:\/([^/]+))?$/);
      if (request.method === "DELETE" && completionMatch) {
        const completionId = completionMatch[1] ? decodeURIComponent(completionMatch[1]) : null;
        const removed = store.clearCompletion(completionId) + registry.clearCompletion(completionId);
        refresh();
        return sendJson(response, 200, { removed });
      }
      if (request.method === "GET" && serveStatic(url.pathname, response)) return;
      sendJson(response, 404, { error: "Not found" });
    } catch (error) {
      sendJson(response, error.status || (error instanceof SyntaxError ? 400 : 500), { error: error.message });
    }
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const server = createServer();
  const ingestion = createIngestionServer(registry, refresh);
  const startupError = error => {
    console.error("Dashboard listener failed:", error.code || error.message);
    tunnel.stop(); ingestionTunnel.stop(); process.exit(1);
  };
  server.on("error", startupError);
  ingestion.on("error", startupError);
  ingestion.listen(config.ingestionPort, config.host, () => {
    console.log("Device ingestion running at http://" + config.host + ":" + config.ingestionPort);
    ingestionTunnel.start();
  });
  server.listen(config.port, config.host, () => {
    console.log(`Agent Dashboard running at http://${config.host}:${config.port}`);
    refresh();
    tunnel.start();
  });
  const timer = setInterval(refresh, config.pollIntervalMs);
  const shutdown = () => {
    clearInterval(timer);
    tunnel.stop();
    ingestionTunnel.stop();
    ingestion.close();
    for (const client of clients) client.end();
    server.close(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
