# Remote access with Microsoft Dev Tunnels

The dashboard automatically hosts a previously created persistent dev tunnel whenever the project starts. The browser URL belongs to that tunnel and port, so restarting or reconnecting reuses the same URL.

For multi-device operation, configure distinct `ui` (4317) and `ingestion` (4319) entries as shown in `tunnel.example.json`. Both host automatically. `GET /api/tunnel` retains the UI-only response; `GET /api/tunnels` reports both. Use [device enrollment](multi-device.md) to connect collectors to the ingestion URL. Legacy single-tunnel configuration is still accepted for the UI.

## Your installation

The machine-specific tunnel ID is stored in the ignored `tunnel.json` file. The default dashboard port is `4317`. To get the current browser URL, read `url` from [the local tunnel status endpoint](http://127.0.0.1:4317/api/tunnel) once it reports `hosting`.

Open that URL on another device and sign in with the same Microsoft account that owns the tunnel. Microsoft may show its first-visit tunnel page before opening the dashboard. The dashboard computer must be awake, connected to the internet, and running the project.

The friendly tunnel ID and browser hostname can differ; use the URL emitted by `devtunnel host` or `GET /api/tunnel` instead of constructing a URL from the tunnel name.

## Start and reconnect

```powershell
npm start
```

Or start in the background on Windows:

```powershell
npm run start:background
```

The saved `tunnel.json` enables remote hosting. It contains the ID and port, not credentials. The CLI manages its own sign-in. Copy `tunnel.example.json` when setting up a different machine, signed into the owning account. Only one machine should host a tunnel at a time.

The server checks owner-only access and exactly one HTTP port before hosting. It always uses the explicit saved ID and retries failed hosts with a delay capped at 60 seconds. Graceful server shutdown stops the tunnel process.

This is automatic connection on **project startup**, not a Windows startup task. No system startup or sign-in settings have been installed.

## URL lifetime and sign-in

A persistent tunnel survives host-process restarts, but Microsoft supports expiration periods of up to 30 days. The project renews the saved tunnel to 30 days on startup and every 24 hours while running. If the project stays offline long enough for the tunnel to expire, its browser URL may be lost; it is not a permanently reserved domain.

If authentication expires, run:

```powershell
devtunnel user show
devtunnel user login --entra
```

The project retries the saved tunnel. It never enables anonymous access or automatically recreates an expired tunnel under a different URL.

## Diagnostics

```powershell
Invoke-RestMethod http://127.0.0.1:4317/api/tunnel
Get-Content data/server.log -Tail 30
Get-Content data/server-error.log -Tail 30
```

The status endpoint returns `starting`, `connecting`, `hosting`, or `retrying`, plus the observed URL and a diagnostic message. The background launcher writes logs under `data/`.

Verify external HTML, API, and event streaming with:

```powershell
node scripts/verify-tunnel.mjs
```

This creates a scoped connect token in memory only for the test. The token is never printed or saved. The test also checks that unauthenticated requests hit the authentication gate. SSE keep-alives and an unlimited tunnel-port request timeout support live updates through the proxy.

## Setup for another installation

```powershell
devtunnel user login --entra
devtunnel create YOUR-UNIQUE-ID --description 'Local Agent Dashboard' --expiration 30d
devtunnel port create YOUR-ID.CLUSTER --port-number 4317 --protocol http --request-timeout 0
```

Save the returned qualified ID in `tunnel.json`, following `tunnel.example.json`, then start the project. Do not add access rules: by default only the owner can connect. Remote users signed into the owner account can view task output and use the same clear/tracking/settings controls as the local dashboard.

The server still binds to `127.0.0.1`; remote traffic passes through Microsoft's authenticated HTTPS relay. To disable remote access, set `enabled` to `false` in `tunnel.json` and restart.

## References

- [Microsoft Dev Tunnels CLI reference](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands)
- [Microsoft Dev Tunnels security](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security)
