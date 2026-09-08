import { icon, mountIcons } from "./icons.js";

const $ = selector => document.querySelector(selector);
const agentNames = { codex: "Codex", copilot: "GitHub Copilot", claude: "Claude Code" };
const pageSize = 5;
let latestSnapshot = null;
let previousHealthy = null;
let visibleCompletions = pageSize;
let sourceFilter = "all";
let searchQuery = "";
let settingsDirty = false;
let pendingSettings = false;
let toastTimer;
let detailSelection = null;
let requestSequence = 0;
let lastAppliedSequence = 0;
let lastServerTime = -Infinity;
let runningMarkup = "";
let completionMarkup = "";

mountIcons();

const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[character]));
const timestamp = value => Number.isFinite(Date.parse(value)) ? Date.parse(value) : null;
const plural = (count, word) => count + " " + word + (count === 1 ? "" : "s");

function duration(start, end = Date.now()) {
  const startMs = timestamp(start);
  const endMs = typeof end === "number" ? end : timestamp(end);
  if (startMs === null || endMs === null) return "—";
  const seconds = Math.max(0, Math.floor((endMs - startMs) / 1000));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  return (hours ? hours + "h " : "") + minutes + "m " + String(seconds % 60).padStart(2, "0") + "s";
}

function relativeTime(value) {
  const time = timestamp(value);
  if (time === null) return "Time unavailable";
  const minutes = Math.max(0, Math.floor((Date.now() - time) / 60000));
  if (minutes < 1) return "Just now";
  if (minutes < 60) return minutes + "m ago";
  if (minutes < 1440) return Math.floor(minutes / 60) + "h ago";
  return Math.floor(minutes / 1440) + "d ago";
}

function workspaceName(value) {
  return String(value || "No workspace").replace(/[\\/]+$/, "").split(/[\\/]/).pop() || "No workspace";
}

function displaySource(source) { return agentNames[source] || "Agent"; }
function agentMark(source) {
  const known = Object.hasOwn(agentNames, source) ? source : "codex";
  return '<span class="agent-icon ' + known + '">' + icon(known) + '</span>';
}
function emptyState(symbol, title, message) {
  return '<div class="empty-state"><span data-icon="' + symbol + '">' + icon(symbol) + '</span><h3>' + escapeHtml(title) + '</h3><p>' + escapeHtml(message) + '</p></div>';
}
function matches(task) {
  return (sourceFilter === "all" || task.source === sourceFilter) &&
    (!searchQuery || [task.title, task.workspace, displaySource(task.source), task.latestOutput].join(" ").toLocaleLowerCase().includes(searchQuery));
}
function completions() {
  return [...(latestSnapshot?.completions || [])].sort((a, b) => (timestamp(b.completedAt) || 0) - (timestamp(a.completedAt) || 0));
}

function showToast(message) {
  clearTimeout(toastTimer);
  $("#toast").textContent = message;
  $("#toast").classList.add("show");
  toastTimer = setTimeout(() => $("#toast").classList.remove("show"), 4500);
}

function setConnection(state) {
  $("#connection").dataset.state = state;
  $("#connection-label").textContent = { live: "Live · connected", connecting: "Connecting", offline: "Disconnected · retrying" }[state];
  $(".section-caption").innerHTML = '<span class="tiny-dot"></span>' + (state === "live" ? "Live activity" : "Last known activity");
}

function render(snapshot, sequence = ++requestSequence) {
  const generated = timestamp(snapshot.generatedAt) ?? Date.now();
  if (generated < lastServerTime || (generated === lastServerTime && sequence < lastAppliedSequence)) return;
  lastServerTime = generated;
  lastAppliedSequence = sequence;
  latestSnapshot = snapshot;
  const running = (snapshot.tasks || []).filter(task => task.status === "running");
  const target = Math.max(1, Number(snapshot.minimumRunning) || 3);
  const total = Number(snapshot.runningCount) || 0;
  const missing = Math.max(target - total, 0);
  const aboveMinimum = Math.max(total - target, 0);
  const progressState = missing ? "below" : aboveMinimum ? "above" : "met";
  $("#hero").dataset.progress = progressState;
  $("#running-count").textContent = total;
  $("#minimum-count").textContent = target;
  $("#capacity-message").textContent = missing
    ? "Start " + plural(missing, "more task") + " to reach your minimum of " + target + "."
    : aboveMinimum
      ? plural(aboveMinimum, "task") + " above your minimum. Keep the momentum."
      : "Minimum met. More parallel tasks are welcome.";
  $("#capacity-status").textContent = { below: "Below minimum", met: "Minimum met", above: "Above minimum" }[progressState];
  $("#capacity-percent").textContent = Math.round(total / target * 100) + "% of minimum";
  $("#capacity-target").textContent = "Minimum: " + target;
  const visibleSegments = Math.min(Math.max(total, target), 20);
  $("#capacity-slots").innerHTML = Array.from({ length: visibleSegments }, (_, index) => '<span class="capacity-slot ' + (index < total ? 'filled' : '') + (index >= target ? ' extra' : '') + '" aria-hidden="true">' + (visibleSegments <= 8 ? icon(index < total ? "activity" : "plus") : "") + '</span>').join("");
  $("#capacity-slots").setAttribute("aria-label", plural(total, "task") + " running; minimum " + target + "; " + (missing ? plural(missing, "more task") + " needed" : "minimum met"));
  $("#minimum-gap-legend").hidden = missing === 0;
  $("#above-minimum-legend").hidden = aboveMinimum === 0;
  $("#running-overflow").hidden = total <= visibleSegments;
  $("#running-overflow").textContent = "+" + (total - visibleSegments) + " more running";
  for (const source of Object.keys(agentNames)) {
    const info = snapshot.sources?.[source];
    const count = running.filter(task => task.source === source).length;
    $("#" + source + "-count").textContent = count;
    const description = !info?.available ? "Not available" : source === "copilot" ? "Manually tracked" : source === "claude" ? "Process detection" : "Auto-detected";
    $("#" + source + "-detail").textContent = description;
    document.querySelector('[data-source="' + source + '"]').title = info?.detail || description;
  }
  if (!settingsDirty && !pendingSettings) syncSettings(snapshot.settings);
  renderLists();
  if (detailSelection) renderDetails();
  if (previousHealthy === true && !snapshot.healthy) {
    const message = plural(total, "task") + " running. Start " + plural(missing, "more task") + " to reach your minimum of " + target + ".";
    showToast(message);
    if ("Notification" in window && Notification.permission === "granted") {
      try { new Notification("Below your task minimum", { body: message }); } catch { /* In-page reminder remains available. */ }
    }
  }
  previousHealthy = snapshot.healthy;
}

function syncSettings(settings) {
  if (!settings) return;
  $("#minimum-input").value = settings.minimumRunning;
  const value = String(settings.reminderCooldownMinutes);
  if (![...$("#cooldown-input").options].some(option => option.value === value)) $("#cooldown-input").add(new Option(value + " minutes", value));
  $("#cooldown-input").value = value;
  syncStepper();
}
function syncStepper() {
  const value = Number($("#minimum-input").value);
  $("#target-minus").disabled = value <= 1;
  $("#target-plus").disabled = value >= 20;
}

function renderLists() {
  if (!latestSnapshot) return;
  const running = (latestSnapshot.tasks || []).filter(task => task.status === "running");
  const filtered = running.filter(matches);
  const allCompletions = completions();
  const filteredCompletions = allCompletions.filter(matches);
  const visible = filteredCompletions.slice(0, visibleCompletions);
  $("#active-count").textContent = filtered.length;
  $("#completed-count").textContent = allCompletions.length;
  $("#nav-completed-count").textContent = allCompletions.length;
  $("#clear-all-button").disabled = allCompletions.length === 0;
  $("#all-sources-button").dataset.filter = sourceFilter;
  $("#filter-label").textContent = sourceFilter === "all" ? "All agents" : displaySource(sourceFilter);
  $("#all-sources-button").title = sourceFilter === "all" ? "Showing every agent. Select an agent card above to filter." : "Clear agent filter";
  document.querySelectorAll("[data-source]").forEach(button => button.setAttribute("aria-pressed", String(button.dataset.source === sourceFilter)));
  const nextRunning = filtered.length ? filtered.map(task => runningCard(task)).join("") : emptyState("activity", running.length ? "No matching tasks" : "A little room to begin", running.length ? "Try another agent or search term." : "Start work with an agent or track a task to see it here.");
  const nextCompleted = visible.length ? visible.map(task => completionCard(task)).join("") : emptyState("checks", allCompletions.length ? "No matching completions" : "You’re all caught up", allCompletions.length ? "Try another agent or search term." : "Finished tasks will wait here for your review.");
  // Keep focused controls stable across identical live snapshots.
  if (runningMarkup !== nextRunning) { $("#task-list").innerHTML = nextRunning; runningMarkup = nextRunning; }
  if (completionMarkup !== nextCompleted) { $("#completed-list").innerHTML = nextCompleted; completionMarkup = nextCompleted; }
  const remaining = Math.max(filteredCompletions.length - visible.length, 0);
  $("#completed-pagination").hidden = filteredCompletions.length <= pageSize;
  $("#completed-range").textContent = "Showing " + visible.length + " of " + filteredCompletions.length;
  $("#show-more-button").hidden = remaining === 0;
  $("#show-more-button").textContent = "Show " + Math.min(pageSize, remaining) + " more";
  $("#show-less-button").hidden = visibleCompletions <= pageSize;
  updateTimes();
}

function runningCard(task) {
  const id = escapeHtml(task.id);
  const start = escapeHtml(task.startedAt || task.createdAt || "");
  return '<article class="task-card"><div class="task-card-top"><span class="task-agent">' + agentMark(task.source) + escapeHtml(displaySource(task.source)) + '<span class="tracking-label">' + (task.confidence === "reported" ? 'Manually tracked' : task.source === "claude" ? 'Process detected' : 'Auto-detected') + '</span></span><span class="running-pill"><span class="tiny-dot"></span>Running</span></div>' +
    '<h3><button type="button" class="task-title" data-detail="' + id + '" data-kind="running">' + escapeHtml(task.title) + '</button></h3><p class="task-workspace" title="' + escapeHtml(task.workspace) + '">' + icon("folder") + '<span>' + escapeHtml(workspaceName(task.workspace)) + '</span></p>' +
    '<div class="output-preview"><div class="output-label">' + icon("message") + 'Latest update</div><p class="latest-output ' + (task.latestOutput ? '' : 'empty-output') + '">' + escapeHtml(task.latestOutput || "Waiting for the first update…") + '</p></div>' +
    '<div class="task-card-footer"><span class="task-runtime">' + icon("clock") + '<span class="elapsed" data-started="' + start + '"></span></span><div class="task-actions">' + (task.confidence === "reported" ? '<button class="button button-quiet" type="button" data-complete="' + id + '">' + icon("check") + 'Complete</button>' : '') + '<button class="button button-quiet" type="button" data-detail="' + id + '" data-kind="running">View update' + icon("arrow") + '</button></div></div></article>';
}

function completionCard(task) {
  const id = escapeHtml(task.id);
  return '<article class="completion-row"><span class="completion-check">' + icon("check") + '</span><div><h3><button class="task-title" type="button" data-detail="' + id + '" data-kind="completed">' + escapeHtml(task.title) + '</button></h3><div class="completion-meta"><span>' + escapeHtml(displaySource(task.source)) + '</span><span>·</span><time data-relative="' + escapeHtml(task.completedAt) + '" datetime="' + escapeHtml(task.completedAt) + '"></time>' + (task.startedAt ? '<span>·</span><span>' + duration(task.startedAt, task.completedAt) + '</span>' : '') + '</div><p class="latest-output">' + escapeHtml(task.latestOutput || "Ready for your review.") + '</p></div><button class="clear-completion" type="button" data-clear-completion="' + id + '" aria-label="Clear completion: ' + escapeHtml(task.title) + '">' + icon("check") + 'Clear</button></article>';
}

function updateTimes() {
  document.querySelectorAll("[data-started]").forEach(element => { element.textContent = duration(element.dataset.started); });
  document.querySelectorAll("[data-relative]").forEach(element => { element.textContent = relativeTime(element.dataset.relative); });
}
setInterval(updateTimes, 1000);

function renderDetails() {
  const list = detailSelection.kind === "completed" ? latestSnapshot?.completions : latestSnapshot?.tasks;
  let task = list?.find(item => item.id === detailSelection.id);
  if (detailSelection.kind === "running" && (!task || task.status !== "running")) {
    const completion = latestSnapshot?.completions?.find(item => item.taskId === detailSelection.id);
    if (completion) {
      task = completion;
      detailSelection = { id: completion.id, kind: "completed" };
    }
  }
  task ||= detailSelection.task;
  if (!task) return;
  detailSelection.task = task;
  $("#detail-title").textContent = task.title;
  $("#detail-source").textContent = displaySource(task.source) + (detailSelection.kind === "completed" ? " · COMPLETED" : " · RUNNING");
  $("#detail-workspace").textContent = task.workspace || "No workspace provided";
  $("#detail-time").textContent = detailSelection.kind === "completed" ? "Finished " + relativeTime(task.completedAt) + " · Runtime " + duration(task.startedAt, task.completedAt) : "Running for " + duration(task.startedAt || task.createdAt);
  $("#detail-output").textContent = task.latestOutput || "No update captured yet.";
}

function setCompletedExpanded(expanded) {
  $("#completed-content").hidden = !expanded;
  $("#toggle-completed-button").setAttribute("aria-expanded", String(expanded));
}
$("#toggle-completed-button").addEventListener("click", () => setCompletedExpanded($("#completed-content").hidden));
$("#show-more-button").addEventListener("click", () => {
  visibleCompletions += pageSize;
  renderLists();
  if ($("#show-more-button").hidden) $("#show-less-button").focus();
});
$("#show-less-button").addEventListener("click", () => { visibleCompletions = pageSize; renderLists(); $("#show-more-button").focus(); });
$("#inbox-link").addEventListener("click", () => setCompletedExpanded(true));
function updateNavigation() {
  const section = ["#completed-panel", "#settings-panel"].includes(location.hash) ? location.hash : "#overview";
  document.querySelectorAll(".nav-link").forEach(link => {
    const active = link.getAttribute("href") === section;
    link.classList.toggle("active", active);
    if (active) link.setAttribute("aria-current", "location"); else link.removeAttribute("aria-current");
  });
  $(".breadcrumb strong").textContent = { "#overview": "Overview", "#completed-panel": "Completed", "#settings-panel": "Preferences" }[section];
}
window.addEventListener("hashchange", updateNavigation);
updateNavigation();
$("#all-sources-button").addEventListener("click", () => { sourceFilter = "all"; visibleCompletions = pageSize; renderLists(); });
document.querySelectorAll("[data-source]").forEach(button => button.addEventListener("click", () => {
  sourceFilter = sourceFilter === button.dataset.source ? "all" : button.dataset.source;
  visibleCompletions = pageSize;
  renderLists();
}));
$("#task-search").addEventListener("input", event => { searchQuery = event.target.value.trim().toLocaleLowerCase(); visibleCompletions = pageSize; renderLists(); });

async function api(path, options = {}) {
  const response = await fetch(path, { ...options, signal: AbortSignal.timeout(15000), headers: { "content-type": "application/json", ...(options.headers || {}) } });
  const body = await response.json();
  if (!response.ok) throw new Error(body.error || "The request could not be completed.");
  return body;
}
async function loadSnapshot() {
  const sequence = ++requestSequence;
  const snapshot = await api("/api/status");
  render(snapshot, sequence);
}
async function perform(button, operation) {
  button.disabled = true;
  try { await operation(); await loadSnapshot(); }
  catch (error) { showToast(error.message || "Please try again."); }
  finally { button.disabled = false; }
}

for (const list of [$("#task-list"), $("#completed-list")]) list.addEventListener("click", async event => {
  const button = event.target.closest("button");
  if (!button) return;
  if (button.dataset.detail) {
    detailSelection = { id: button.dataset.detail, kind: button.dataset.kind };
    renderDetails();
    $("#detail-dialog").showModal();
  } else if (button.dataset.complete) {
    await perform(button, () => api("/api/tasks/" + encodeURIComponent(button.dataset.complete), { method: "PATCH", body: JSON.stringify({ status: "completed" }) }));
  } else if (button.dataset.clearCompletion) {
    await perform(button, () => api("/api/completions/" + encodeURIComponent(button.dataset.clearCompletion), { method: "DELETE" }));
    $("#toggle-completed-button").focus();
  }
});
$("#clear-all-button").addEventListener("click", async event => {
  await perform(event.currentTarget, async () => { await api("/api/completions", { method: "DELETE" }); visibleCompletions = pageSize; });
  $("#clear-all-button").disabled = !(latestSnapshot?.completions || []).length;
});

$("#add-button").addEventListener("click", () => { $("#task-error").textContent = ""; $("#task-dialog").showModal(); $("#task-title").focus(); });
for (const id of ["close-dialog", "cancel-task"]) $("#" + id).addEventListener("click", () => $("#task-dialog").close());
$("#close-detail").addEventListener("click", () => $("#detail-dialog").close());
$("#detail-dialog").addEventListener("close", () => { detailSelection = null; });
$("#task-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (!$("#task-title").value.trim()) { $("#task-error").textContent = "Give this task a name."; $("#task-title").focus(); return; }
  const submit = event.target.querySelector('[type="submit"]');
  submit.disabled = true;
  $("#task-error").textContent = "";
  try {
    await api("/api/tasks", { method: "POST", body: JSON.stringify({ source: $("#task-source").value, title: $("#task-title").value.trim(), workspace: $("#task-workspace").value, latestOutput: $("#task-output").value, leaseMinutes: Number($("#lease-input").value) }) });
    event.target.reset();
    $("#task-dialog").close();
    sourceFilter = "all"; searchQuery = ""; $("#task-search").value = "";
    await loadSnapshot();
    showToast("Task added to your workspace.");
  } catch (error) { $("#task-error").textContent = error.message; }
  finally { submit.disabled = false; }
});

function markSettingsDirty() { settingsDirty = true; $("#settings-feedback").textContent = "Unsaved changes"; syncStepper(); }
$("#settings-form").addEventListener("input", markSettingsDirty);
$("#cooldown-input").addEventListener("change", markSettingsDirty);
for (const [id, delta] of [["target-minus", -1], ["target-plus", 1]]) $("#" + id).addEventListener("click", () => {
  $("#minimum-input").value = Math.min(20, Math.max(1, (Number($("#minimum-input").value) || 3) + delta));
  markSettingsDirty();
});
$("#settings-form").addEventListener("submit", async event => {
  event.preventDefault();
  const submit = event.target.querySelector('[type="submit"]');
  const fields = [$("#minimum-input"), $("#cooldown-input"), $("#target-minus"), $("#target-plus")];
  const input = { minimumRunning: Number($("#minimum-input").value), reminderCooldownMinutes: Number($("#cooldown-input").value) };
  pendingSettings = true; submit.disabled = true; fields.forEach(field => { field.disabled = true; });
  try {
    const settings = await api("/api/settings", { method: "PUT", body: JSON.stringify(input) });
    settingsDirty = false;
    syncSettings(settings);
    $("#settings-feedback").textContent = "Preferences saved";
    await loadSnapshot();
  } catch (error) { $("#settings-feedback").textContent = error.message; }
  finally { pendingSettings = false; submit.disabled = false; fields.forEach(field => { field.disabled = false; }); syncStepper(); }
});

function syncBrowserNotifications() {
  if (!("Notification" in window)) return;
  const permission = Notification.permission;
  $("#browser-notifications").hidden = false;
  $("#browser-notifications").disabled = permission !== "default";
  $("#notification-label").textContent = { default: "Enable browser reminders", granted: "Browser reminders enabled", denied: "Browser reminders blocked" }[permission];
}
$("#browser-notifications").addEventListener("click", async () => {
  try { await Notification.requestPermission(); syncBrowserNotifications(); }
  catch { showToast("Browser reminders aren’t available here."); }
});
syncBrowserNotifications();

loadSnapshot().catch(() => setConnection("offline"));
const events = new EventSource("/api/events");
events.onopen = () => setConnection("live");
events.addEventListener("snapshot", event => { try { render(JSON.parse(event.data)); setConnection("live"); } catch { setConnection("offline"); } });
events.onerror = () => setConnection("offline");
