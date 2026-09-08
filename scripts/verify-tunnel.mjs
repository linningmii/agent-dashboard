// Verifies the configured tunnel using an ephemeral connect token kept only in memory.
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { config } from "../src/config.mjs";

const local = "http://127.0.0.1:" + config.port;
const state = await (await fetch(local + "/api/tunnel")).json();
if (state.state !== "hosting" || !/^https:\/\/[a-z0-9-]+\.[a-z0-9]+\.devtunnels\.ms\/?$/i.test(state.url)) {
  throw new Error("The configured tunnel is not hosting yet.");
}
const remote = new URL(state.url);
const unauthenticated = await fetch(remote, { redirect: "manual", headers: { Accept: "application/json" }, signal: AbortSignal.timeout(30000) });
console.log("Unauthenticated access:", unauthenticated.status);
if (![302, 303, 307, 401, 403].includes(unauthenticated.status)) throw new Error("Expected an authentication gate.");

const result = await promisify(execFile)(process.platform === "win32" ? "devtunnel.exe" : "devtunnel", ["token", config.tunnel.id, "--scopes", "connect", "--json"], { timeout: 60000, windowsHide: true });
const parsed = JSON.parse(result.stdout);
const token = typeof parsed.token === "string" ? parsed.token : parsed.accessToken || parsed.token?.token || parsed.token?.accessToken;
if (typeof token !== "string") throw new Error("Unrecognized token response keys: " + Object.keys(parsed).join(", "));
const headers = { "X-Tunnel-Authorization": "tunnel " + token, Accept: "application/json" };
const response = await fetch(new URL("/api/status", remote), { headers, signal: AbortSignal.timeout(30000) });
if (!response.ok) throw new Error("Authenticated status failed: " + response.status);
const status = await response.json();
console.log("Authenticated dashboard:", response.status, "running:", status.runningCount, "target:", status.minimumRunning);
const page = await fetch(remote, { headers, signal: AbortSignal.timeout(30000) });
if (!page.ok || !(await page.text()).includes("Your work, in motion")) throw new Error("Dashboard page was not forwarded.");
console.log("Dashboard HTML: verified");
const controller = new AbortController();
const timer = setTimeout(() => controller.abort(), 55000);
try {
  const events = await fetch(new URL("/api/events", remote), { headers, signal: controller.signal });
  console.log("SSE headers:", events.status, events.headers.get("content-type"), events.headers.get("content-encoding"));
  if (!events.ok) throw new Error("SSE request failed: " + events.status);
  const reader = events.body.getReader();
  const decoder = new TextDecoder();
  let content = "";
  while (!(content.includes("event: snapshot") && content.includes(": keep-alive"))) {
    const chunk = await reader.read();
    if (chunk.done) throw new Error("SSE connection closed early.");
    content += decoder.decode(chunk.value, { stream: true });
    console.log("SSE chunk:", chunk.value.length, "bytes; snapshot:", content.includes("event: snapshot"), "keep-alive:", content.includes(": keep-alive"));
  }
  await reader.cancel();
  console.log("Remote live stream: snapshot and keep-alive verified");
} finally { clearTimeout(timer); controller.abort(); }
console.log("URL:", remote.origin);
