# Migration verification

The production service has been cut over to the three-part .NET/React architecture. The original Node runtime is removed from the working tree and recoverable at Git commit ef36684.

**Migration complete on `migration/dotnet-react`.** The final implementation and portable package-source update are verified locally and on all three CI operating systems. The branch is pushed separately from `main`; the adapter validation limits below remain explicit.

## Evidence

- C# API, standalone C# collector, strict TypeScript/React UI, and generated OpenAPI client build successfully.
- 19 backend tests pass locally on Windows, including actual HTTP listener authentication/isolation, real collector CLI enrollment/report/complete, import idempotency, offline/replay rules, adapter fixtures, reminder scheduling, and tunnel URL parsing.
- [GitHub Actions run 34206720460](https://github.com/linningmii/agent-dashboard/actions/runs/34206720460) passed the final implementation on Linux, Windows, and macOS, including all 19 tests, actual API/collector CLI integration, and packaged collector execution.
- The Windows collector reads the live Codex installation and continuously reports through a separate process. Historical unmatched starts are excluded as stale; Claude's uncertain activity does not count as running.
- React sign-in, SSE, search, completion pagination/collapse/details, and mobile layout were inspected. On mobile, output is 16px and there is no horizontal overflow. Completion clearing was exercised via the API using isolated synthetic records.
- All 16 legacy unread completions and the original device identity were preserved at cutover. Legacy JSON was copied to an ignored backup directory and retained unchanged.
- Both original private Dev Tunnel URLs are hosted by the C# service. The deployed verifier passed authenticated React HTML, separate ingress, synthetic registration/reporting, completion/replay, and live SSE through the tunnels.
- At migration cutover, a clean Enzyme-only npm ci installed 97 packages successfully. Strict TypeScript checks and the Vite production build passed afterward. The later [package-source update](package-sources.md) uses portable public lockfile URLs with Enzyme available as a backup or explicit corporate source.

## Completion and verification boundaries

- The API and collector were republished from the tested code and restarted independently. Re-running the background launcher confirms existing processes without launching duplicates.
- The live OpenAPI export and regenerated TypeScript definitions match the committed contract.
- Deployment verification passed again after restart, through both unchanged tunnel URLs. The original inbox remained intact.
- The React form created and completed a synthetic task and saved preferences against the isolated C# API.
- Native vendor app installations on macOS/Linux are not available here; [adapter support](adapters.md) distinguishes cross-platform fixtures/runtime tests from live installed-agent validation. No claim is made that every vendor app version has been certified.

## September 9 completion audit

- Rebuilt the current worktree with `pwsh scripts/build.ps1 -Output artifacts/verification-final`: all 19 backend tests, strict TypeScript checks, Vite build, and API/collector publishing passed.
- Exported the running OpenAPI document and regenerated the TypeScript client; neither artifact differs from the committed contract.
- Re-ran `pwsh scripts/verify-deployment.ps1 -Remote`: authenticated React delivery, separate listeners, device registration/reporting, completion acknowledgement/replay, and live SSE passed through both original tunnel URLs.
- All 16 legacy unread completions and the original device ID remain present. Both retained legacy JSON files still match their cutover backups byte for byte.
- The existing API and collector are separate live processes. The launcher recognized both and did not create duplicates.
- [Final implementation CI run 34251004723](https://github.com/linningmii/agent-dashboard/actions/runs/34251004723), commit `a13aaa7`, passed all six jobs: backend and frontend on Windows, Linux, and macOS. Each backend job passed 19 tests and packaged collector execution. Each frontend job passed 11 installer tests, a real public-only clean install, the UI build, and unchanged-lockfile verification. No Microsoft credentials were used by the frontend jobs.
- A clean local Enzyme download/build also passed, with 97 HTTP fetches and no npmjs request. All 147 normalized lockfile URLs preserve versions, integrity hashes, and other metadata. Both package sources now work with the same committed lockfile.
- Rechecked the React UI against an isolated published API: sign-in, live connection, task creation/completion, final output, source filtering without changing the global count, acknowledgement, saved preferences, newest-five pagination, Show more, and inbox collapse all passed. At 390px width, there was no horizontal overflow and captured output used 16px text. No browser console errors were recorded.
- Republished the verified API and collector into the live release, retained the previous release under artifacts, and restarted both processes. All checked application assemblies match the verified build byte for byte, the collector reconnected, and every pre-restart completion remains present. Both original private tunnels passed the deployment verifier again after restart.

### Requirement audit

| Requirement | Current evidence |
|---|---|
| React/TypeScript UI, C# API, separate C# collector | Local publish, deployed assembly/asset comparison, independent live processes |
| Linux host and Windows/macOS/Linux collectors | Real API/CLI integration tests and packaged collector execution on all three CI systems |
| Device authentication, ingestion isolation, heartbeats, offline counts, replay safety | Hub/HTTP tests plus authenticated remote deployment verification |
| Legacy settings, identity, unread history, rollback inputs | Import tests; 16 preserved unread entries and matching legacy backup hashes; previous release retained |
| Live counts, runtime/output, filters, completion inbox, preferences | Typed React build and isolated browser checks described above |
| Reminders only below the minimum, including repeated reminders | Reminder tests at/below/above threshold; connected-browser delivery over SSE |
| Generated versioned contract | Live OpenAPI export and regenerated TypeScript match committed files |
| Portable npm installation with corporate backup | Public clean installs on all three CI systems; clean authenticated Enzyme install locally; fallback/authentication unit tests |
| Existing private tunnel addresses | Remote verifier passed before and after final deployment using the original two URLs |

Vendor-app lifecycle coverage remains as documented in [adapters.md](adapters.md): Codex is live-checked on Windows, Copilot uses explicit manual reports, and uncertain Claude transcript activity is not counted as confirmed running. OS CI validates adapters with fixtures; it does not claim live vendor-app certification on macOS/Linux.

## Microsoft tunnel sign-in follow-up

The deployed host now selects `uiAuthentication: dev-tunnel` in ignored config.json. Owner-only Microsoft tunnel login opens the dashboard directly; the default access-key mode remains available for other hosting. The API stays on loopback and collector device tokens remain mandatory.

Verification: 44 backend tests pass, strict TypeScript and Vite build pass, and exported OpenAPI/generated types remain unchanged. The remote deployment verifier passed without an application key, including same-origin browser headers, rejection of anonymous tunnel access and unrelated origins, device-token enforcement, completion/replay, and SSE. An actual browser completed Microsoft SSO and reached the private dashboard with a live connection, no key form, no application Sign out button, and no console errors. All pre-restart completion IDs were preserved and the existing collector reconnected.

## Build policy and rollback

Use `npm run setup:web`, scripts/install-web.ps1, or scripts/install-web.sh. Installs default to npmjs with Enzyme fallback; corporate devices can select Enzyme directly. Public installs require no Azure authentication. Enzyme accepts existing npm credentials, an environment token, or the current Azure CLI account. NuGet uses Microsoft's dotnet-public Azure Artifacts feed. Use separate artifacts/windows and artifacts/linux when building from Windows and WSL.

See [migration.md](migration.md) for cutover, separate collector enrollment, retained state, and rollback. The default access-key mode uses `data/ui-access-key`; the optional `dev-tunnel` mode uses the private tunnel’s Microsoft login without a second dashboard prompt. See [remote access](remote-access.md). Reminders use cross-platform SSE and browser notifications.
