# Collector adapter support

.NET collectors build, run protocol tests, and publish on Windows, Linux, and macOS. Platform support below describes supported observations, not certification of every vendor app version.

| Adapter | Windows | macOS | Linux | Counting rule |
|---|---|---|---|---|
| Codex | Live local installation checked; SQLite/JSONL fixtures | Same SQLite/JSONL parser and OS path fixtures in CI | Same parser/fixtures in CI and Ubuntu runtime | Latest explicit turn, reconciled between projected history and current rollout lifecycle |
| GitHub Copilot | Manual leases | Manual leases | Manual leases | Explicit user/reporting lease only |
| Claude Code | Transcript parser + manual leases | Same parser fixtures in CI | Same parser fixtures in CI | Unfinished transcript is estimated/unknown; only manual leases count as running |

The collector never treats a live CLI process as proof the model is generating. Claude transcript estimates appear under **Activity to confirm**. A terminal end_turn message yields a completion event. Copilot availability checks its storage but does not pretend to know active IDE lifecycle. Add a reliable vendor hook/API integration separately when available.

## Paths

Codex defaults to the user's .codex directory (or CODEX_HOME): state_5.sqlite, thread_history_1.sqlite, session_index.jsonl, and rollout paths referenced by the state database. Claude defaults to .claude/projects (or CLAUDE_CONFIG_DIR).

Copilot VS Code storage defaults to:

- Windows: AppData/Roaming/Code/User/globalStorage/github.copilot-chat
- macOS: Library/Application Support/Code/User/globalStorage/github.copilot-chat
- Linux: .config/Code/User/globalStorage/github.copilot-chat (honors XDG_CONFIG_HOME)

Override CODEX_STATE_DB, CODEX_HISTORY_DB, CODEX_SESSION_INDEX, COPILOT_STORAGE, or CLAUDE_CONFIG_DIR for alternate installations. Overrides are local collector configuration, not paths the API reads.

## Limits and correctness

Codex names prefer session-index thread_name, database name, then the original title. Only one active turn per thread can count. The collector reconciles projected history with the currently referenced rollout file, because history can lag or still refer to an older rollout. A newer explicit task_started supersedes older projected state; a completion or abort closes that turn even before history catches up. A projected terminal status cannot be resurrected by a lagging rollout start. Completion IDs are shared between both sources to avoid duplicates after catch-up. Rollout parsing uses a file cache; changes to the referenced path, size, or modification time refresh it. Old unmatched starts without activity for 24 hours are stale. Captured assistant text excludes reasoning/tool payloads.

Codex inspects the most recent 30 projected/completed turns per thread; Claude scans 50 recent top-level transcripts. Tasks that start and finish outside those windows can be missed during long outages. Already queued completions remain durable until acknowledged. Vendor-local schemas are implementation details and can change. Read failures report source unavailability, never fabricate completions.

Native installed-agent behavior on macOS/Linux still depends on the vendor's app availability and version. CI validates synthetic lifecycle data, filesystem handling, API isolation, and packaged collector execution on all three systems. It does not launch paid/vendor agent sessions.
