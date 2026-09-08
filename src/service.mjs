import { readCodexTasks } from "./adapters/codex.mjs";
import { inspectCopilot } from "./adapters/copilot.mjs";
import { readClaudeTasks } from "./adapters/claude.mjs";

export class DashboardService {
  constructor(config, store) {
    this.config = config;
    this.store = store;
    this.cached = null;
  }

  refresh() {
    const persisted = this.store.snapshot();
    const codex = readCodexTasks(this.config);
    const copilot = inspectCopilot(this.config);
    const claude = readClaudeTasks(this.config);
    if (codex.available) this.store.reconcileAutomatic("codex", codex.tasks);
    if (claude.available) this.store.reconcileAutomatic("claude", claude.tasks);
    const current = this.store.snapshot();
    const reported = current.tasks;
    const tasks = [...codex.tasks, ...claude.tasks, ...reported];
    const runningCount = tasks.filter((task) => task.status === "running").length;
    const minimum = persisted.settings.minimumRunning;
    this.cached = {
      generatedAt: new Date().toISOString(),
      runningCount,
      minimumRunning: minimum,
      missingCount: Math.max(minimum - runningCount, 0),
      healthy: runningCount >= minimum,
      settings: current.settings,
      completions: current.completions,
      sources: {
        codex: { available: codex.available, detail: codex.detail, automatic: true },
        copilot: { available: copilot.available, detail: copilot.detail, automatic: false, processDetected: copilot.processDetected },
        claude: { available: claude.available, detail: claude.detail, automatic: true }
      },
      tasks
    };
    return this.cached;
  }
}
