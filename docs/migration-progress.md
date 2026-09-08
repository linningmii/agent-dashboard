# Migration verification

The production service has been cut over to the three-part .NET/React architecture. The original Node runtime is removed from the working tree and recoverable at Git commit ef36684.

## Evidence

- C# API, standalone C# collector, strict TypeScript/React UI, and generated OpenAPI client build successfully.
- 19 backend tests pass locally on Windows, including actual HTTP listener authentication/isolation, real collector CLI enrollment/report/complete, import idempotency, offline/replay rules, adapter fixtures, reminder scheduling, and tunnel URL parsing.
- The earlier cross-platform matrix (GitHub Actions run 34202626860) passed Linux/Windows/macOS. Updated final-code matrix results are pending.
- The Windows collector reads the live Codex installation and continuously reports through a separate process. Historical unmatched starts are excluded as stale; Claude's uncertain activity does not count as running.
- React sign-in, SSE, search, completion pagination/collapse/details, and mobile layout were inspected. On mobile, output is 16px and there is no horizontal overflow. Completion clearing was exercised via the API using isolated synthetic records.
- All 16 legacy unread completions and the original device identity were preserved at cutover. Legacy JSON was copied to an ignored backup directory and retained unchanged.
- Both original private Dev Tunnel URLs are hosted by the C# service. The deployed verifier passed authenticated React HTML, separate ingress, synthetic registration/reporting, completion/replay, and live SSE through the tunnels.
- Enzyme package installation succeeded. The committed lockfile resolves packages only through the Enzyme host. A clean-install recheck is still running.

## Remaining verification

- Finish the clean Enzyme install check and rebuild from its result.
- Run the final cross-platform CI matrix and inspect its results, including the packaged collector CLI on Linux/macOS/Windows.
- Refresh/recheck the generated OpenAPI contract, publish the latest config-path handling, and audit final instructions and artifacts.
- Native vendor app installations on macOS/Linux are not available here; [adapter support](adapters.md) distinguishes cross-platform fixtures/runtime tests from live installed-agent validation.

## Build policy and rollback

Use root/web .npmrc and scripts/install-web.ps1 or scripts/install-web.sh. Authentication comes from the current Azure CLI account; tokens are not committed or printed. NuGet uses Microsoft's dotnet-public Azure Artifacts feed. Use separate artifacts/windows and artifacts/linux when building from Windows and WSL.

See [migration.md](migration.md) for cutover, separate collector enrollment, retained state, and rollback. The service is application-authenticated: the owner reads data/ui-access-key to sign in. Reminders now use cross-platform SSE and browser notifications, rather than a Windows-only server toast.
