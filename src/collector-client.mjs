import { execFile } from "node:child_process";
import { promisify } from "node:util";

export function validateCollectorUrl(value, tunnelId) {
  const url = new URL(value);
  if (url.username || url.password || url.search || url.hash || url.pathname !== "/") throw new Error("Use the ingestion origin without a path or credentials");
  const local = url.protocol === "http:" && ["127.0.0.1", "localhost", "[::1]"].includes(url.hostname);
  const remote = url.protocol === "https:" && /^[a-z0-9-]+\.[a-z0-9]+\.devtunnels\.ms$/i.test(url.hostname);
  if (!local && !remote) throw new Error("Use an HTTPS Dev Tunnel or HTTP loopback URL");
  if (remote && (!/^[a-z0-9][a-z0-9-]*\.[a-z0-9]+$/i.test(tunnelId || "") || !url.hostname.endsWith("." + tunnelId.split(".").pop() + ".devtunnels.ms"))) throw new Error("The ingestion tunnel ID and cluster are required");
  return url;
}

export function createCollectorClient(settings, { request = fetch, execute = promisify(execFile), now = Date.now } = {}) {
  const origin = validateCollectorUrl(settings.url, settings.tunnelId);
  let tunnelToken = null; let tokenExpires = 0;
  async function connectToken() {
    if (tunnelToken && tokenExpires > now()) return tunnelToken;
    try {
      const result = await execute(process.platform === "win32" ? "devtunnel.exe" : "devtunnel", ["token", settings.tunnelId, "--scopes", "connect", "--json"], { timeout: 60000, windowsHide: true });
      const parsed = JSON.parse(result.stdout);
      tunnelToken = typeof parsed.token === "string" ? parsed.token : parsed.accessToken || parsed.token?.token || parsed.token?.accessToken;
      if (typeof tunnelToken !== "string") throw new Error();
      tokenExpires = now() + 60 * 60 * 1000;
      return tunnelToken;
    } catch { throw new Error("Dev Tunnel sign-in failed. Run devtunnel user login --entra on this device."); }
  }
  return {
    async send(route, method, body, token) {
      const headers = { "content-type": "application/json", Accept: "application/json" };
      if (origin.protocol === "https:") headers["X-Tunnel-Authorization"] = "tunnel " + await connectToken();
      if (token) headers.Authorization = "Bearer " + token;
      const destination = new URL(route, origin);
      if (destination.origin !== origin.origin) throw new Error("Collector routes must stay on the configured ingestion origin");
      const response = await request(destination, { method, headers, body: JSON.stringify(body), redirect: "error", signal: AbortSignal.timeout(30000) });
      if ([401, 403].includes(response.status)) { tunnelToken = null; tokenExpires = 0; }
      if (!response.ok) throw Object.assign(new Error("Collector request failed (" + response.status + "). Check pairing, sign-in, and ingestion URL."), { status: response.status });
      return response.json();
    }
  };
}
