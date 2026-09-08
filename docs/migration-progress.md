# Migration progress

Target: [architecture one-pager](architecture-onepager.md). Work is in progress; the original Node service remains the live deployment until cutover is verified.

## Implemented so far

- .NET 10 solution: Contracts, Core, API, Adapters, and Collector, with nullable reference types and warnings treated as errors.
- C# API: distinct UI/ingestion ports, per-device authentication, replay protection, expiry, SQLite persistence, JSON state import, SSE, and scheduled reminder events.
- API-hosted UI authentication using a local access key and HTTP-only signed session cookie, independent of tunnel provider.
- Standalone .NET collector with enrollment, outbound HTTPS reporting, persisted completion outbox, manual reporting, and OS-dependent path defaults. The API has no dependency on adapter projects or installed agent apps.
- React + strict TypeScript/Vite UI and OpenAPI-generated types/client, retaining the existing design and global minimum behavior.
- Enzyme-only npm configuration and lockfile. Installation succeeded using the existing Entra login. No npmjs.org package downloads are required or permitted.
- Windows and Ubuntu/WSL builds and 16 C# tests pass, including live HTTP listener isolation and migration-import checks. React sign-in and SSE rendering were verified, and the Windows .NET collector enrolled and delivered a real local snapshot to the isolated API.
- GitHub Actions run 34202626860 passed backend tests and collector publishing on Linux, Windows, and macOS for commit 628a39d. These are fixture/runtime checks, not native installed-agent certification.
- React completion pagination, collapse, and full output details were exercised against the isolated C# API.

## Remaining before completion

- Finish native HTTP/collector integration and runtime verification on Linux and Windows; add cross-platform CI, including macOS adapter fixtures. Native macOS agent-app verification has not yet been performed.
- Audit and improve adapter parity: Codex legacy/current schemas and current-turn selection; Claude transcript states are estimated and must not count as active generation. Validate metadata and final outputs against the current installation.
- Finish React browser checks for task filtering, completion acknowledgement, and mobile layout. Login/SSE, pagination, collapse, details, and the generated client/build already work.
- Migrate live state safely, enroll a separate collector for the original host, preserve the two existing tunnel URLs, and verify live data and reminders.
- Replace default launch/build/package instructions and scripts with the .NET/React workflow, remove obsolete runtime files after successful cutover, and document rollback.
- Update architecture and operations docs to distinguish the final implementation from the old Node system. Commit/push only validated changes.

## Package sources

Use the corporate Enzyme feed configured in root and web `.npmrc`. `scripts/install-web.ps1` obtains a short-lived Azure DevOps token from the existing `az` login, passes it through an environment variable, and deletes the temporary credential-reference configuration afterward. It does not alter the user's global npm configuration or print/save the token. `web/package-lock.json` resolves packages only through `o365exchange.pkgs.visualstudio.com`.

NuGet uses Microsoft's public dotnet Azure Artifacts feed because direct NuGet.org TLS requests are unavailable on this machine.

## Build isolation

Windows and WSL must use separate `--artifacts-path` directories (`artifacts/windows` and `artifacts/linux`). Sharing `obj` across OS restores causes package path conflicts. The preview API uses 4417/4419 and `data/migration-preview`; it must not replace or expose the live state until migration validation is complete.
