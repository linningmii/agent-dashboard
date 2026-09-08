# Agent Dashboard

A local, live control plane for keeping a minimum number of agentic tasks in flight. Adapters support Codex, GitHub Copilot, and Claude Code.

See [the design document](docs/design.md) for architecture, adapter behavior, data flow, security, and extension guidance.

## Multiple devices

One central dashboard merges tasks from its own computer and enrolled collectors. Each task carries a device name; offline devices stop contributing to the global minimum after 45 seconds. The browser/UI tunnel and device-ingestion tunnel are separate and authenticated.

On the hub, use **Connect device**. On another computer, clone this repo, run the enrollment command shown in the dialog, then run `npm run collector`. Collectors send outbound reports, so additional computers do not need their own inbound tunnels. See [multi-device architecture and setup](docs/multi-device.md).

## What works

- Codex tasks are discovered automatically from the local Codex SQLite history. Active turns are counted only when their status is `inProgress`.
- Each running task shows a live elapsed timer. Codex rows also show the latest agent message from local history.
- GitHub Copilot installation/process presence is discovered automatically. Because Copilot's local stores do not expose a stable active-task status, running tasks are confirmed through time-limited dashboard leases.
- Claude Code tasks are detected from live `claude.exe` processes and paired with recently updated local JSONL sessions for titles, workspaces, and latest output. Manual leases remain available as a fallback.
- Live browser updates use Server-Sent Events.
- Recently completed tasks remain in a persisted inbox until cleared. Clearing acknowledges the notification and does not delete source history.
- The default target is three parallel tasks. Falling below it triggers an in-page alert and a Windows toast, subject to a configurable cooldown.
- State remains on `127.0.0.1` and is persisted under `data/`. No cloud service or API key is required.

## Run

Requires Node.js 22.5 or newer.

```powershell
npm start
```

Open [http://127.0.0.1:4317](http://127.0.0.1:4317).

To change defaults, copy `config.example.json` to `config.json` and edit it. Settings changed in the dashboard are persisted separately.

## Access from other devices

With `tunnel.json` configured, `npm start` also hosts the saved UI and ingestion Dev Tunnels. Use `npm run start:background` to launch the hub and both tunnels in the background on Windows. Access is restricted to the tunnel owner's Microsoft account. See [remote access setup and URL discovery](docs/remote-access.md).

The computer must remain awake and the project must be running. The service renews expiration while active; a tunnel left offline for 30 days can expire.

## Copilot reporting API

The UI's **Track a task** button creates a lease. Scripts can do the same:

```powershell
$task = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:4317/api/tasks `
  -ContentType application/json `
  -Body '{"source":"copilot","title":"Implement checkout flow","workspace":"C:\\repo","latestOutput":"Inspecting payment tests","leaseMinutes":60}'

Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:4317/api/tasks/$($task.id)/heartbeat" `
  -ContentType application/json -Body '{"leaseMinutes":5,"latestOutput":"Two tests remain"}'

Invoke-RestMethod -Method Patch -Uri "http://127.0.0.1:4317/api/tasks/$($task.id)" `
  -ContentType application/json -Body '{"status":"completed"}'
```

Leases prevent abandoned task reports from being counted forever.

## API

- `GET /api/status` — current aggregate and task list
- `GET /api/tunnel` — remote connection state and observed browser URL
- `GET /api/tunnels` — UI and device-ingestion tunnel states
- `POST /api/devices/pair` — issue a single-use device pairing code
- `DELETE /api/devices/:id` — revoke a remote collector
- `GET /api/events` — live SSE snapshots
- `PUT /api/settings` — minimum count and reminder cooldown
- `POST /api/tasks` — create a reported Copilot task
- `PATCH /api/tasks/:id` — update status or title
- `POST /api/tasks/:id/heartbeat` — renew a running-task lease
- `DELETE /api/completions/:id` — acknowledge one completion
- `DELETE /api/completions` — acknowledge all completions

## Current limitation

Codex's database schema and Copilot's storage are app implementation details, not guaranteed public integration contracts. The adapters fail soft and the reporting API remains usable if an app update changes local storage.
