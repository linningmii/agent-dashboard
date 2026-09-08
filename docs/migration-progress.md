# Migration verification

The production service has been cut over to the three-part .NET/React architecture. The original Node runtime is removed from the working tree and recoverable at Git commit ef36684.

## Evidence

- C# API, standalone C# collector, strict TypeScript/React UI, and generated OpenAPI client build successfully.
- 19 backend tests pass locally on Windows, including actual HTTP listener authentication/isolation, real collector CLI enrollment/report/complete, import idempotency, offline/replay rules, adapter fixtures, reminder scheduling, and tunnel URL parsing.
- [GitHub Actions run 34206720460](https://github.com/linningmii/agent-dashboard/actions/runs/34206720460) passed the final implementation on Linux, Windows, and macOS, including all 19 tests, actual API/collector CLI integration, and packaged collector execution.
- The Windows collector reads the live Codex installation and continuously reports through a separate process. Historical unmatched starts are excluded as stale; Claude's uncertain activity does not count as running.
- React sign-in, SSE, search, completion pagination/collapse/details, and mobile layout were inspected. On mobile, output is 16px and there is no horizontal overflow. Completion clearing was exercised via the API using isolated synthetic records.
- All 16 legacy unread completions and the original device identity were preserved at cutover. Legacy JSON was copied to an ignored backup directory and retained unchanged.
- Both original private Dev Tunnel URLs are hosted by the C# service. The deployed verifier passed authenticated React HTML, separate ingress, synthetic registration/reporting, completion/replay, and live SSE through the tunnels.
- A clean Enzyme-only npm ci installed 97 packages successfully. Strict TypeScript checks and the Vite production build passed afterward. Every resolved package URL in the lockfile uses the Enzyme host.

## Completion and verification boundaries

- The API and collector were republished from the tested code and restarted independently. Re-running the background launcher confirms existing processes without launching duplicates.
- The live OpenAPI export and regenerated TypeScript definitions match the committed contract.
- Deployment verification passed again after restart, through both unchanged tunnel URLs. The original inbox remained intact.
- The React form created and completed a synthetic task and saved preferences against the isolated C# API.
- Native vendor app installations on macOS/Linux are not available here; [adapter support](adapters.md) distinguishes cross-platform fixtures/runtime tests from live installed-agent validation. No claim is made that every vendor app version has been certified.

## Build policy and rollback

Use root/web .npmrc and scripts/install-web.ps1 or scripts/install-web.sh. Authentication comes from the current Azure CLI account; tokens are not committed or printed. NuGet uses Microsoft's dotnet-public Azure Artifacts feed. Use separate artifacts/windows and artifacts/linux when building from Windows and WSL.

See [migration.md](migration.md) for cutover, separate collector enrollment, retained state, and rollback. The service is application-authenticated: the owner reads data/ui-access-key to sign in. Reminders now use cross-platform SSE and browser notifications, rather than a Windows-only server toast.
