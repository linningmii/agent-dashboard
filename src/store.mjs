import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

export class Store {
  constructor(file, defaults) {
    this.file = file;
    this.defaults = defaults;
    this.state = this.#load();
  }

  #load() {
    try {
      const parsed = JSON.parse(fs.readFileSync(this.file, "utf8"));
      return {
        settings: { ...this.defaults, ...parsed.settings },
        tasks: parsed.tasks || [],
        completions: parsed.completions || [],
        observedAutomatic: parsed.observedAutomatic || {}
      };
    } catch (error) {
      if (error.code !== "ENOENT") throw error;
      return { settings: { ...this.defaults }, tasks: [], completions: [], observedAutomatic: {} };
    }
  }

  #save() {
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    const temporary = `${this.file}.${process.pid}.tmp`;
    fs.writeFileSync(temporary, JSON.stringify(this.state, null, 2));
    fs.renameSync(temporary, this.file);
  }

  snapshot() {
    const now = Date.now();
    let changed = false;
    for (const task of this.state.tasks) {
      if (task.status === "running" && task.expiresAt && new Date(task.expiresAt).getTime() <= now) {
        task.status = "stale";
        task.updatedAt = new Date().toISOString();
        changed = true;
      }
    }
    if (changed) this.#save();
    return structuredClone(this.state);
  }

  updateSettings(input) {
    if (Number.isInteger(input.minimumRunning) && input.minimumRunning >= 1 && input.minimumRunning <= 20) {
      this.state.settings.minimumRunning = input.minimumRunning;
    }
    if (Number.isFinite(input.reminderCooldownMinutes) && input.reminderCooldownMinutes >= 1 && input.reminderCooldownMinutes <= 1440) {
      this.state.settings.reminderCooldownMinutes = input.reminderCooldownMinutes;
    }
    if (typeof input.windowsNotifications === "boolean") {
      this.state.settings.windowsNotifications = input.windowsNotifications;
    }
    this.#save();
    return this.snapshot().settings;
  }

  createTask(input) {
    const now = new Date();
    const leaseMinutes = Math.min(Math.max(Number(input.leaseMinutes) || 240, 5), 1440);
    const task = {
      id: crypto.randomUUID(),
      source: ["codex", "copilot", "claude"].includes(input.source) ? input.source : "copilot",
      title: String(input.title || "Untitled task").trim().slice(0, 200),
      workspace: String(input.workspace || "").trim().slice(0, 500),
      latestOutput: String(input.latestOutput || "").trim().slice(0, 2000),
      latestOutputAt: input.latestOutput ? now.toISOString() : null,
      status: "running",
      confidence: "reported",
      createdAt: now.toISOString(),
      updatedAt: now.toISOString(),
      expiresAt: new Date(now.getTime() + leaseMinutes * 60_000).toISOString()
    };
    this.state.tasks.unshift(task);
    this.#save();
    return structuredClone(task);
  }

  updateTask(id, input) {
    const task = this.state.tasks.find((candidate) => candidate.id === id);
    if (!task) return null;
    const previousStatus = task.status;
    if (["running", "completed", "paused"].includes(input.status)) task.status = input.status;
    if (typeof input.title === "string" && input.title.trim()) task.title = input.title.trim().slice(0, 200);
    if (typeof input.latestOutput === "string") {
      task.latestOutput = input.latestOutput.trim().slice(0, 2000);
      task.latestOutputAt = new Date().toISOString();
    }
    task.updatedAt = new Date().toISOString();
    if (task.status === "running") {
      const leaseMinutes = Math.min(Math.max(Number(input.leaseMinutes) || 240, 5), 1440);
      task.expiresAt = new Date(Date.now() + leaseMinutes * 60_000).toISOString();
    }
    if (task.status === "completed" && previousStatus !== "completed") this.#recordCompletion(task);
    this.#save();
    return structuredClone(task);
  }

  heartbeat(id, leaseMinutes = 5, latestOutput) {
    const task = this.state.tasks.find((candidate) => candidate.id === id);
    if (!task) return null;
    task.status = "running";
    task.updatedAt = new Date().toISOString();
    task.expiresAt = new Date(Date.now() + Math.min(Math.max(Number(leaseMinutes) || 5, 1), 1440) * 60_000).toISOString();
    if (typeof latestOutput === "string") {
      task.latestOutput = latestOutput.trim().slice(0, 2000);
      task.latestOutputAt = new Date().toISOString();
    }
    this.#save();
    return structuredClone(task);
  }

  #recordCompletion(task, completedAt = new Date().toISOString()) {
    if (this.state.completions.some((item) => item.taskId === task.id)) return;
    this.state.completions.unshift({
      id: crypto.randomUUID(),
      taskId: task.id,
      source: task.source,
      title: task.title,
      workspace: task.workspace || "",
      startedAt: task.startedAt || task.createdAt || null,
      completedAt,
      latestOutput: task.latestOutput || "",
      confidence: task.confidence || "automatic"
    });
    this.state.completions = this.state.completions.slice(0, 100);
  }

  reconcileAutomatic(source, tasks) {
    const previous = this.state.observedAutomatic[source];
    const current = Object.fromEntries(tasks.map((task) => [task.id, task]));
    let changed = false;
    if (previous?.initialized) {
      for (const [id, task] of Object.entries(previous.tasks || {})) {
        if (!current[id]) {
          this.#recordCompletion(task);
          changed = true;
        }
      }
    }
    const next = { initialized: true, tasks: current };
    if (JSON.stringify(previous) !== JSON.stringify(next)) changed = true;
    this.state.observedAutomatic[source] = next;
    if (changed) this.#save();
  }

  clearCompletion(id) {
    const before = this.state.completions.length;
    this.state.completions = id
      ? this.state.completions.filter((item) => item.id !== id)
      : [];
    if (this.state.completions.length !== before) this.#save();
    return before - this.state.completions.length;
  }
}
