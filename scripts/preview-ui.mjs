// Isolated UI preview with synthetic tasks. Never reads or writes real task data.
// Run: node scripts/preview-ui.mjs, then open http://127.0.0.1:4318.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";

const publicRoot = path.resolve(import.meta.dirname, "../public");
const now = Date.now();
let settings = { minimumRunning: 3, reminderCooldownMinutes: 15, windowsNotifications: false };
let tasks = [
  { id: "preview-codex", source: "codex", title: "Refine the checkout experience", workspace: "C:/workspace/storefront", latestOutput: "The checkout flow is in place. I’m checking keyboard navigation and the final payment confirmation before running the tests.", confidence: "automatic", status: "running", startedAt: new Date(now - 1118000).toISOString() },
  { id: "preview-copilot", source: "copilot", title: "Make search feel instant", workspace: "C:/workspace/search-service", latestOutput: "Indexing is complete. Testing the empty state and a few longer queries to make sure results stay responsive.", confidence: "reported", status: "running", createdAt: new Date(now - 363000).toISOString() },
  { id: "preview-claude", source: "claude", title: "Polish the documentation", workspace: "C:/workspace/developer-docs", latestOutput: "Updated the getting-started guide. Cross-checking code examples against the current release.", confidence: "automatic", status: "running", startedAt: new Date(now - 246000).toISOString() }
];
let completions = Array.from({ length: 12 }, (_, i) => ({
  id: "preview-completion-" + i, taskId: "preview-task-" + i, source: ["codex", "copilot", "claude"][i % 3],
  title: ["Review the authentication flow", "Update the component library", "Improve the onboarding guide", "Simplify the settings screen", "Resolve the flaky integration test", "Add keyboard shortcuts"][i % 6],
  workspace: "C:/workspace/product", startedAt: new Date(now - (i + 2) * 1200000).toISOString(), completedAt: new Date(now - (i + 1) * 600000).toISOString(),
  latestOutput: i === 0 ? 'Review complete. All checks passed. Literal text stays literal: <img src=x onerror="alert(1)">.' : "Changes are ready for review. The relevant checks passed and the documentation is up to date."
}));
const clients = new Set();
function snapshot() {
  const runningCount = tasks.filter(task => task.status === "running").length;
  return { generatedAt: new Date().toISOString(), tasks, completions, runningCount, minimumRunning: settings.minimumRunning, missingCount: Math.max(0, settings.minimumRunning - runningCount), healthy: runningCount >= settings.minimumRunning, settings,
    sources: { codex: { available: true, detail: "Preview: Codex" }, copilot: { available: true, detail: "Preview: Copilot" }, claude: { available: true, detail: "Preview: Claude" } } };
}
const publish = () => { for (const client of clients) client.write('event: snapshot\ndata: ' + JSON.stringify(snapshot()) + '\n\n'); };
const server = http.createServer(async (request, response) => {
  const url = new URL(request.url, "http://127.0.0.1:4318");
  response.setHeader("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:");
  response.setHeader("Cache-Control", "no-store");
  const json = (body, status = 200) => { response.writeHead(status, { "Content-Type": "application/json" }); response.end(JSON.stringify(body)); };
  if (url.pathname === "/api/events") {
    response.writeHead(200, { "Content-Type": "text/event-stream" }); clients.add(response); publish();
    request.on("close", () => clients.delete(response)); return;
  }
  if (url.pathname === "/api/status") return json(snapshot());
  const chunks = []; for await (const chunk of request) chunks.push(chunk);
  const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : {};
  if (request.method === "PUT" && url.pathname === "/api/settings") {
    settings = { ...settings, ...body }; publish(); return json(settings);
  }
  if (request.method === "POST" && url.pathname === "/api/tasks") {
    const task = { ...body, id: "preview-added-" + Date.now(), status: "running", confidence: "reported", createdAt: new Date().toISOString() };
    tasks.unshift(task); publish(); return json(task, 201);
  }
  if (request.method === "PATCH" && url.pathname.startsWith("/api/tasks/")) {
    const task = tasks.find(task => task.id === decodeURIComponent(url.pathname.split("/").pop()));
    if (!task) return json({ error: "Not found" }, 404);
    Object.assign(task, body); completions.unshift({ ...task, completedAt: new Date().toISOString(), startedAt: task.startedAt || task.createdAt }); publish(); return json(task);
  }
  if (request.method === "DELETE" && url.pathname.startsWith("/api/completions")) {
    const id = url.pathname.split("/")[3]; const before = completions.length;
    completions = id ? completions.filter(item => item.id !== decodeURIComponent(id)) : []; publish(); return json({ removed: before - completions.length });
  }
  const assets = { "/": "index.html", "/styles.css": "styles.css", "/app.js": "app.js", "/icons.js": "icons.js", "/favicon.svg": "favicon.svg" };
  const file = assets[url.pathname]; if (!file) return json({ error: "Not found" }, 404);
  response.setHeader("Content-Type", { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".svg": "image/svg+xml" }[path.extname(file)]);
  response.end(fs.readFileSync(path.join(publicRoot, file)));
});
server.listen(4318, "127.0.0.1", () => console.log("Synthetic UI preview: http://127.0.0.1:4318"));
const timer = setInterval(publish, 2000);
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => { clearInterval(timer); for (const client of clients) client.end(); server.close(); });
