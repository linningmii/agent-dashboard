import path from "node:path";
import { spawn } from "node:child_process";

export class Notifier {
  constructor(root) {
    this.script = path.join(root, "scripts", "notify.ps1");
    this.lastSentAt = 0;
    this.wasBelow = false;
  }

  maybeNotify(snapshot) {
    const below = snapshot.runningCount < snapshot.settings.minimumRunning;
    const cooldownMs = snapshot.settings.reminderCooldownMinutes * 60_000;
    const due = Date.now() - this.lastSentAt >= cooldownMs;
    const shouldNotify = below && (!this.wasBelow || due);
    if (shouldNotify) {
      this.lastSentAt = Date.now();
      if (snapshot.settings.windowsNotifications && process.platform === "win32") {
        const missing = snapshot.settings.minimumRunning - snapshot.runningCount;
        const child = spawn("powershell.exe", [
          "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
          "-File", this.script,
          "-Title", "Agent capacity is low",
          "-Message", `${snapshot.runningCount}/${snapshot.settings.minimumRunning} tasks running. Start ${missing} more.`
        ], { detached: true, stdio: "ignore", windowsHide: true });
        child.unref();
      }
    }
    this.wasBelow = below;
    return shouldNotify;
  }
}
