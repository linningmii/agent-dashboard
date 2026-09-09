# Frontend package sources

npm has one default registry per invocation. The project installer runs `npm ci` against npmjs first and retries with Enzyme for network, TLS, access-denied (403), rate-limit, or service-availability errors. It does not retry dependency conflicts, missing packages/versions, invalid npmjs credentials, filesystem permissions, or integrity failures against a different feed. TLS and package integrity verification remain enabled.

```sh
npm run setup:web
npm run build:web
```

Node.js 22.12+ is required. The installer uses built-in TypeScript support and needs no bootstrap package download. `scripts/install-web.ps1` and `scripts/install-web.sh` invoke the same installer on Windows, macOS, and Linux. Plain `npm ci --prefix web` uses the configured registry without automatic fallback.

## Choose a source

| Mode | Behavior |
|---|---|
| `auto` (default) | npmjs first; Enzyme only after an eligible failure |
| `npmjs` | Public registry only; never attempts Enzyme authentication |
| `enzyme` | Enzyme only; never attempts npmjs |

```sh
npm run setup:web -- --registry enzyme
npm run setup:web -- --registry npmjs
npm run setup:web -- --registry auto
```

Selection precedence is command-line `--registry`, then `AGENT_NPM_REGISTRY`, then the ignored root `npm-install.local.json`, then `auto`. On a corporate machine that must avoid npmjs requests, create this local file:

```json
{ "registry": "enzyme" }
```

PowerShell also accepts `pwsh scripts/install-web.ps1 -Registry enzyme`. The old `-Clean` option remains accepted; clean locked installation is now the default.

## Enzyme authentication

Public installs require neither Azure CLI nor Microsoft credentials. Enzyme requires an account with feed access. The installer uses `ENZYME_NPM_TOKEN` if provided; otherwise it first tries existing scoped npm credentials. After an Enzyme authentication rejection, it obtains a temporary token from the current Azure CLI sign-in. Run `az login` beforehand if using this option. It does not initiate an interactive login automatically.

The token is passed through the child process environment. Temporary npm configuration contains only a feed-scoped environment reference and is cleaned up after success or failure. Tokens are not included in command arguments or lockfile URLs. An authentication failure stops installation with instructions; there is no anonymous mirror fallback.

## Reproducible installs and updates

The committed `web/package-lock.json` uses npmjs tarball URLs. When Enzyme is selected, npm registry-host substitution sends those requests to the Enzyme registry path. Both sources must provide bytes matching the same committed integrity hashes. Ordinary installation leaves the lockfile unchanged.

After intentionally changing dependencies in `web/package.json`, update the lockfile with:

```sh
npm run setup:web -- --update-lock
```

This runs `npm install` and normalizes Enzyme download URLs to their public equivalents without changing pinned versions or integrity hashes during normalization. Other package sources remain unchanged. Review the lockfile diff before committing. Lifecycle scripts are disabled during installation; this frontend is built explicitly with `npm run build:web`.

## Verification

`npm run test:installer` tests source selection, fallback boundaries, credential handling, and lockfile portability without contacting either registry. GitHub Actions also performs public-only clean installs and UI builds on Linux, Windows, and macOS. Enzyme requires private credentials and is verified separately on a corporate device.

This configuration applies to npm packages. NuGet sources remain configured separately in `NuGet.Config`.

Verification on September 9, 2026: all 11 installer tests passed, a clean Enzyme installation with a fresh cache completed with 97 HTTP fetches and no npmjs request, and strict TypeScript checks plus the Vite production build passed. All 147 lockfile URL replacements preserved the original package versions, integrity hashes, and other metadata. The temporary authentication configuration was removed after installation. [CI run 34251004723](https://github.com/linningmii/agent-dashboard/actions/runs/34251004723) also passed real public-only clean installs, installer tests, UI builds, and unchanged-lockfile checks on Windows, Linux, and macOS without Microsoft credentials. Automatic fallback and authentication failures are tested with simulated command results; this corporate device selects Enzyme directly.
