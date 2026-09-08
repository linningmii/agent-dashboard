import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

export function validateTunnelConfig(input, port) {
  if (!input || input.enabled === false) return { enabled: false };
  if (!/^[a-z0-9][a-z0-9-]*\.[a-z0-9]+$/i.test(input.id || "")) {
    throw new Error("tunnel.json requires the persistent tunnel ID, including its cluster suffix.");
  }
  if (input.port !== port) throw new Error("The tunnel port must match the dashboard port.");
  return { enabled: true, id: input.id, port };
}

export function createTunnelHost(options, { log = console.log, run = execFileAsync, launch = spawn } = {}) {
  let child = null;
  let retryTimer = null;
  let renewalTimer = null;
  let stopped = false;
  let started = false;
  let attempts = 0;
  const state = { enabled: options.enabled, state: options.enabled ? "starting" : "disabled", id: options.id || null, url: null, error: null };
  const command = process.platform === "win32" ? "devtunnel.exe" : "devtunnel";
  const invoke = async args => {
    const result = await run(command, args, { timeout: 60000, maxBuffer: 1024 * 1024, windowsHide: true });
    return JSON.parse(result.stdout);
  };
  function retry(message) {
    if (stopped) return;
    state.state = "retrying";
    state.error = message;
    const delay = Math.min(60000, 5000 * 2 ** Math.min(attempts++, 4));
    log("Dev tunnel: " + message + "; retrying in " + delay / 1000 + "s.");
    clearTimeout(retryTimer);
    retryTimer = setTimeout(connect, delay);
  }
  async function renew() {
    await invoke(["update", options.id, "--expiration", "30d", "--json"]);
  }
  async function connect() {
    if (stopped || !options.enabled) return;
    state.state = "starting";
    try {
      // Only host the dedicated, previously created, owner-only tunnel and port.
      const { tunnel } = await invoke(["show", options.id, "--json"]);
      const { ports } = await invoke(["port", "list", options.id, "--json"]);
      const { port } = await invoke(["port", "show", options.id, "--port-number", String(options.port), "--json"]);
      if (!tunnel || !Array.isArray(tunnel.accessControl) || tunnel.accessControl.length ||
          !Array.isArray(ports) || ports.length !== 1 || ports[0].portNumber !== options.port ||
          ports[0].protocol !== "http" || !port || port.portNumber !== options.port ||
          !Array.isArray(port.accessControl) || port.accessControl.length || port.requestTimeoutSeconds !== 0) {
        throw new Error("Tunnel must have owner-only access and exactly the configured HTTP port");
      }
      await renew();
      if (stopped) return;
      child = launch(command, ["host", options.id], { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
      state.state = "connecting";
      let outputTail = "";
      const onOutput = data => {
        const output = data.toString();
        log(output.trimEnd());
        outputTail = (outputTail + output).slice(-8192);
        const match = outputTail.match(/https:\/\/[a-z0-9-]+\.[a-z0-9]+\.devtunnels\.ms\/?/i);
        if (match) {
          state.url = match[0]; state.state = "hosting"; state.error = null; attempts = 0;
        }
      };
      child.stdout.on("data", onOutput);
      child.stderr.on("data", onOutput);
      child.once("error", () => { state.error = "Unable to launch the devtunnel CLI"; });
      child.once("close", code => {
        child = null;
        clearInterval(renewalTimer);
        retry("Tunnel host exited (" + code + "). Check devtunnel user show for sign-in status");
      });
      renewalTimer = setInterval(() => renew().catch(() => {
        log("Dev tunnel expiration renewal failed. Check devtunnel user show and sign in again if needed.");
      }), 24 * 60 * 60 * 1000);
    } catch (error) {
      // Never include CLI error objects: they can contain authentication details.
      retry(error.code === "ENOENT" ? "Install the devtunnel CLI" : "Tunnel preflight failed. Check the saved ID, port, owner-only access, and devtunnel sign-in");
    }
  }
  return {
    start() { if (!started && options.enabled) { started = true; void connect(); } },
    stop() {
      stopped = true;
      clearTimeout(retryTimer); clearInterval(renewalTimer);
      child?.kill(); state.state = "stopped";
    },
    snapshot() { return { ...state }; }
  };
}
