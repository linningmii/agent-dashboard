# Agent Dashboard

Monitor Codex, GitHub Copilot, and Claude Code across devices. Maintain **at least three parallel tasks** by default; more than three is healthy.

## Three components

| Component | Technology | Responsibility |
|---|---|---|
| Dashboard UI | React + strict TypeScript / Vite | Device filters, live task updates, completion inbox, reminders |
| API service | C# / ASP.NET Core (.NET 10) | Authentication, aggregation, SQLite persistence, ingestion and SSE |
| Collector | C# / .NET 10 | Read local agents and send authenticated outbound reports |

The API host needs no installed agents. Run a separate collector on every monitored computer, including the API host if needed. Linux, Windows, and macOS are supported; see the [adapter support matrix](docs/adapters.md) for detection limitations.

## Build

Requires .NET 10 SDK and Node.js 22.12+ for the frontend build. The installer tries **npmjs first**, then the **Enzyme backup** when public registry access is denied or unavailable. A successful public install needs no Microsoft account or Azure CLI.

```powershell
npm run setup:web
pwsh ./scripts/build.ps1
```

On Linux/macOS:

```sh
sh scripts/install-web.sh
sh scripts/build.sh
```

On corporate devices where npmjs is blocked, select Enzyme directly with `npm run setup:web -- --registry enzyme`. Enzyme requires feed access through existing npm credentials, `ENZYME_NPM_TOKEN`, or an Azure CLI sign-in (`az login`). Save `{ "registry": "enzyme" }` in the ignored root `npm-install.local.json` to make this the device default. See [package sources](docs/package-sources.md) for overrides, authentication, and lockfile updates.

NuGet uses Microsoft's dotnet-public Azure Artifacts feed. The build runs backend tests, checks TypeScript, builds the UI, and publishes the API and collector under artifacts/release. There are no JavaScript backend or collector processes.

## Run

```sh
dotnet artifacts/release/hub/Dashboard.Api.dll
```

Open [http://127.0.0.1:4317](http://127.0.0.1:4317). By default, the service creates **data/ui-access-key** and uses a seven-day session cookie after sign-in. With an owner-only Microsoft Dev Tunnel, set `"uiAuthentication": "dev-tunnel"` in ignored `config.json` to use the tunnel’s Microsoft login without another key prompt. This mode requires the API to bind to loopback. See [remote access](docs/remote-access.md) for setup and account requirements. Do not commit keys or collector credentials.

The API uses two distinct ports: **4317** for UI/dashboard APIs and **4319** for device ingestion. Optional tunnel.json settings retain the two persistent Dev Tunnel URLs. See [remote access](docs/remote-access.md).

## Connect a device

In the dashboard, choose **Connect device**, run its .NET enrollment command on the other computer, and paste the one-time code. Start its collector:

```sh
dotnet artifacts/release/collector/Dashboard.Collector.dll run
```

Collectors need no inbound port. They support HTTPS endpoints with optional Microsoft Dev Tunnels authentication. Missing heartbeats mark devices offline after 45 seconds; offline tasks do not count and are not mistaken for completed tasks.

On Windows, after building and enrolling this host, start both background processes using:

```powershell
pwsh ./scripts/start-dotnet.ps1 -Collector
```

For manual agent tracking, use the dashboard or the collector's report/complete commands. See [multi-device setup](docs/multi-device.md).

## Development and verification

- API: dotnet run --project src/Dashboard.Api
- Collector: dotnet run --project src/Dashboard.Collector -- run
- UI development: npm --prefix web run dev (proxies /api to port 4317)
- Tests: dotnet test AgentDashboard.slnx
- Refresh generated API types: pwsh scripts/export-openapi.ps1, then npm --prefix web run generate:api
- Verify deployed private tunnels: pwsh scripts/verify-deployment.ps1 -Remote

The [architecture one-pager](docs/architecture-onepager.md), [design](docs/design.md), [migration/rollback guide](docs/migration.md), and [interface notes](docs/interface.md) document the system. GitHub Actions verifies backend tests, collector publishing, installer tests, and public-registry UI builds on Linux, Windows, and macOS.
