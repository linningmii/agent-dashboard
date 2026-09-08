import { collectLocal } from "./local-collector.mjs";

export class DashboardService {
  constructor(config, store, registry, collector = collectLocal) {
    this.config = config; this.store = store; this.registry = registry; this.collector = collector;
  }
  refresh() {
    const local = this.registry.local();
    const captured = this.config.collectLocal === false
      ? { tasks: this.store.snapshot().tasks, completions: this.store.snapshot().completions, sources: {} }
      : this.collector(this.config, this.store);
    const attach = task => ({ ...task, deviceId: local.id, deviceName: local.name, managedLocally: true });
    const remote = this.registry.view();
    const localTasks = captured.tasks.map(attach);
    const tasks = [...localTasks, ...remote.tasks];
    const devices = [{ ...local, sources: captured.sources, runningCount: localTasks.filter(task => task.status === "running").length }, ...remote.devices];
    const settings = this.store.snapshot().settings;
    const runningCount = tasks.filter(task => task.status === "running").length;
    const sources = {};
    const sourceIds = new Set(["codex", "copilot", "claude", ...devices.flatMap(device => Object.keys(device.sources))]);
    for (const id of sourceIds) {
      const reporting = devices.filter(device => device.status === "online" && device.sources[id]?.available);
      sources[id] = { available: reporting.length > 0, automatic: reporting.some(device => device.sources[id].automatic),
        detail: reporting.length + " online device(s) reporting " + id };
    }
    return { generatedAt: new Date().toISOString(), localDeviceId: local.id, devices, tasks, sources,
      runningCount, minimumRunning: settings.minimumRunning, missingCount: Math.max(settings.minimumRunning - runningCount, 0),
      healthy: runningCount >= settings.minimumRunning, settings,
      completions: [...captured.completions.map(attach), ...remote.completions].sort((a, b) => Date.parse(b.completedAt) - Date.parse(a.completedAt)) };
  }
}
