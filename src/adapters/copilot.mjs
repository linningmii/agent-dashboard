import fs from "node:fs";
import { execFileSync } from "node:child_process";

export function inspectCopilot(config) {
  const storageDetected = fs.existsSync(config.copilotStorage);
  if (inspectCopilot.cache && Date.now() - inspectCopilot.cacheAt < 15_000) {
    return { ...inspectCopilot.cache, available: storageDetected || inspectCopilot.cache.processDetected };
  }
  let processDetected = false;
  try {
    if (process.platform === "win32") {
      const output = execFileSync("powershell.exe", [
        "-NoProfile", "-NonInteractive", "-Command",
        "@(Get-Process -Name '*copilot*' -ErrorAction SilentlyContinue).Count"
      ], { encoding: "utf8", timeout: 1500, windowsHide: true });
      processDetected = Number(output.trim()) > 0;
    }
  } catch {
    // Process discovery is advisory; reported tasks remain authoritative.
  }
  const result = {
    available: storageDetected || processDetected,
    processDetected,
    detail: processDetected
      ? "Copilot is running; active task reporting is manual"
      : storageDetected
        ? "Copilot is installed; no active process detected"
        : "Copilot storage was not found"
  };
  inspectCopilot.cache = result;
  inspectCopilot.cacheAt = Date.now();
  return result;
}
