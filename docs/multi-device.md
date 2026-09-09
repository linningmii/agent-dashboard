# Devices and collectors

Run one central API and one collector per monitored device. The central machine needs a collector too if it runs agents. Each device has a persistent identity; different devices' task lists merge toward one global minimum.

## Enroll

1. Build the project or obtain the published collector for your OS. Install the .NET 10 runtime for framework-dependent packages.
2. Open the central dashboard and choose **Connect device**.
3. Run the command shown there on the target computer. For a source checkout:

```sh
dotnet run --project src/Dashboard.Collector -- enroll --url https://YOUR-INGESTION-ORIGIN --name "Work laptop"
```

For a private Dev Tunnel, include --tunnel-id ID.CLUSTER and sign into its owner account with the Microsoft Dev Tunnels CLI first. Paste the code at the prompt. Enrollment writes ignored collector.json. Do not share that file across devices.

4. Start reporting:

```sh
dotnet run --project src/Dashboard.Collector -- run
```

Published packages use dotnet Dashboard.Collector.dll followed by the same commands. Collectors need no inbound tunnel.

## Reporting modes

- **run:** continuously collect/report, retrying network failures. --once sends one report and exits.
- **inspect:** print adapter observations for diagnostics. Output may include private task text.
- **report --source copilot --title "Task" --minutes 240:** create a local manual lease; prints its task ID.
- **complete --id TASK_ID --output "Finished":** queue that manually tracked task's completion.
- **enroll:** exchange a one-time code for a device credential.
- **migrate-host:** local migration command for converting the original Node hub's device into a standalone collector; see [migration](migration.md).

Use --config and --state for explicit paths, or AGENT_COLLECTOR_CONFIG and AGENT_COLLECTOR_STATE. Default files are collector.json and data/collector.sqlite relative to the working directory. Choose a private, persistent directory per device. On Unix credential files are owner read/write only; on Windows they inherit the user's directory ACL.

## Status and completion

Collectors send new snapshots about every ten seconds. The API uses server receipt time for the 45-second heartbeat window. Offline tasks stop contributing to the count. A collector restart opens a new session and invalidates an older collector using the same identity.

Completions carry explicit event IDs, device labels, final output, and timestamps. Acknowledged events never reappear on retransmission. The outbox survives restart; it sends at most 100 queued events per report. Reports support up to 500 simultaneous task entries; excess entries produce an explicit error rather than a silently truncated count.

The **Show tasks from** selector combines with agent filters and search. **Disconnect** revokes the selected collector. Existing unread completions remain until cleared. Central manual task creation supports selecting a device; its running leases only count while that device is online.

## Transport

The UI/dashboard API and collector-ingestion API have separate endpoints and private Dev Tunnels. Ingestion accepts registration, sessions, snapshots, and health only. The UI endpoint owns device management and combined state. With `uiAuthentication: dev-tunnel`, browsers sign into the UI tunnel owner’s Microsoft account without a dashboard key. In default `access-key` mode, the service key is also required. Collectors always use their own device tokens and sign into the ingestion tunnel owner’s account when that private tunnel is used. See [remote access](remote-access.md).

The endpoint contract is in [contracts/openapi.json](../contracts/openapi.json). [Adapter support](adapters.md) documents per-platform detection and uncertainty.
