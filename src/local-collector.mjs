import { readCodexTasks } from "./adapters/codex.mjs";
import { inspectCopilot } from "./adapters/copilot.mjs";
import { readClaudeTasks } from "./adapters/claude.mjs";

export function collectLocal(config, store) {
  const codex = readCodexTasks(config);
  const copilot = inspectCopilot(config);
  const claude = readClaudeTasks(config);
  if (codex.available) store.reconcileAutomatic("codex", codex.tasks);
  if (claude.available) store.reconcileAutomatic("claude", claude.tasks);
  const saved = store.snapshot();
  return { tasks: [...codex.tasks, ...claude.tasks, ...saved.tasks], completions: saved.completions,
    sources: { codex: { available: codex.available, detail: codex.detail, automatic: true },
      copilot: { available: copilot.available, detail: copilot.detail, automatic: false },
      claude: { available: claude.available, detail: claude.detail, automatic: true } } };
}
