import http from "node:http";
import { readJson, sendJson } from "./http.mjs";

export function createIngestionServer(registry, changed = () => {}) {
  return http.createServer(async (request, response) => {
    try {
      const url = new URL(request.url, "http://localhost");
      if (request.headers.origin) return sendJson(response, 403, { error: "Collector requests only" });
      if (request.method === "GET" && url.pathname === "/health") return sendJson(response, 200, { service: "agent-dashboard-ingestion", version: 1 });
      if (request.method === "POST" && url.pathname === "/v1/devices/register") {
        const result = registry.enroll(await readJson(request)); changed(); return sendJson(response, 201, result);
      }
      const match = url.pathname.match(/^\/v1\/devices\/([a-z0-9-]+)\/(sessions|snapshot)$/);
      if (!match) return sendJson(response, 404, { error: "Not found" });
      const token = request.headers.authorization?.match(/^Bearer (\S+)$/)?.[1];
      registry.authenticate(match[1], token);
      if (match[2] === "sessions" && request.method === "POST") return sendJson(response, 200, registry.openSession(match[1], token));
      if (match[2] === "snapshot" && request.method === "PUT") {
        const result = registry.report(match[1], token, await readJson(request, 4 * 1024 * 1024));
        changed(); return sendJson(response, 200, result);
      }
      sendJson(response, 405, { error: "Method not allowed" });
    } catch (error) { sendJson(response, error.status || (error instanceof SyntaxError ? 400 : 500), { error: error.status || error instanceof SyntaxError ? error.message : "Ingestion failed" }); }
  });
}
