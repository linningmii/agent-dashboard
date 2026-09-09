# Remote access

The API runs two distinct listeners:

| Listener | Default port | Access |
|---|---|---|
| Dashboard UI + administration | 4317 | Private Dev Tunnel login, or service access key / session cookie |
| Collector ingestion | 4319 | One-time enrollment code, then device-scoped token |

## Microsoft Dev Tunnels

Copy tunnel.example.json to ignored tunnel.json and configure one persistent tunnel per port. Both use owner-only Microsoft authentication. The API verifies that each tunnel has exactly its configured HTTP port, no expanded access rules, and no request timeout before hosting it. Collector device tokens remain required regardless of dashboard authentication mode.

The tunnel worker starts on API startup, retries exits, and renews expiration to 30 days at startup and daily. URLs survive process restarts but can be lost if the tunnel expires after a long outage. The machine must remain awake and connected. Obtain both observed URLs from authenticated GET /api/tunnels; GET /api/tunnel remains the UI-only compatibility endpoint. Do not construct hostnames from friendly IDs.

```powershell
devtunnel user login --entra
devtunnel create YOUR-UI-ID --expiration 30d
devtunnel port create YOUR-UI-ID.CLUSTER --port-number 4317 --protocol http --request-timeout 0
devtunnel create YOUR-INGESTION-ID --expiration 30d
devtunnel port create YOUR-INGESTION-ID.CLUSTER --port-number 4319 --protocol http --request-timeout 0
```

### Use Microsoft login without a dashboard key

Set these fields in ignored `config.json`, retaining other existing configuration:

```json
{
  "bindAddress": "127.0.0.1",
  "uiAuthentication": "dev-tunnel"
}
```

Restart the API. Open its private UI URL and sign in with **the same Microsoft account that owns the tunnel**, using the same identity provider/tenant. An account merely belonging to the same organization does not have access. Other devices use that account in their browsers; they need neither an access key nor the Dev Tunnels CLI just to view the dashboard. The Windows/macOS/Linux OS account does not need to match.

The tunnel performs Microsoft authentication before forwarding requests. The dashboard does not receive or independently validate a Microsoft user identity, and it does not issue an additional dashboard session cookie. There is no separate app registration. A dashboard Sign out button is omitted because it cannot end the tunnel’s Microsoft session; use a separate browser profile or sign out of Microsoft to change accounts.

This deployment mode trusts the host computer: direct `http://127.0.0.1:4317` access is allowed without a key, including local processes. The API refuses this mode on non-loopback bind addresses or without an enabled UI tunnel. The managed tunnel preserves Host and Origin; the API accepts only the exact current UI tunnel host or its local port, rejects unrelated browser origins and cross-site requests, and never trusts identity/forwarded headers as proof of login. Do not forward this local listener through another public proxy/tunnel.

The UI tunnel’s private access configuration is rechecked every minute while hosted; a failed check stops the host and retries validation. ACL changes can take effect before the next check, so keep it owner-only. Shared users/organizations and anonymous access are deliberately unsupported by this mode; supporting additional users requires an explicit access-policy change.

Configuration precedence: `--ui-auth dev-tunnel`, then `DASHBOARD_UI_AUTH`, then `config.json` field `uiAuthentication`, then default `access-key`. Switch to `access-key` for hosting without the managed private tunnel. Existing `data/ui-access-key` is retained but ignored in tunnel mode; no new key is generated.

API clients using a private tunnel send `X-Tunnel-Authorization: tunnel CONNECT_TOKEN`. In access-key mode they additionally send `Authorization: Bearer SERVICE_KEY`, or browsers enter the host’s `data/ui-access-key` once per seven-day session. Collector clients always use `Authorization: Bearer DEVICE_TOKEN` and, for private tunnels, `X-Tunnel-Authorization: tunnel CONNECT_TOKEN`. Tokens are never put into URLs.

Access-key sessions are stored as hashed tokens with expiry on the server. Logout revokes that session, including copied cookies; changing the access key and restarting the API invalidates all existing sessions. Switching to tunnel mode also clears access-key sessions. Upgrading from the old stateless cookie format requires signing in again. Active SSE connections stop after revocation (within the next event or 15-second heartbeat).

Dashboard login and collector enrollment have separate limits of ten requests per minute per network peer. Forwarded-IP headers are not trusted. When clients arrive through a proxy or tunnel, they share that proxy’s peer bucket for the same operation; enforce additional per-user/client limits at a trusted edge for shared deployments. Enrollment traffic cannot consume the dashboard-login bucket.

## Ordinary Linux hosting

A tunnel is optional. Use `uiAuthentication: access-key`, configure --bind for the internal listener address and --ingestion-url with the public HTTPS ingestion origin, then put an HTTPS reverse proxy in front of the two ports. Route each public origin only to its corresponding internal port, disable SSE response buffering, and allow streaming connections. The application validates access keys/device credentials independently of the proxy. Do not expose unencrypted listener ports to an untrusted network.

The React build is served from the published API's wwwroot, or --web-root. If developing separately with Vite, /api is proxied to the API listener. No separate frontend server is needed for deployment.

## Verify and troubleshoot

```powershell
pwsh scripts/verify-deployment.ps1 -Remote
Get-Content data/hub.log -Tail 30
Get-Content data/collector-error.log -Tail 30
```

The verifier detects the configured authentication mode and checks authenticated HTML, UI/ingestion separation, device-token enforcement, synthetic device registration/reporting, completion/replay, and streamed updates. In tunnel mode it sends no dashboard key and checks rejection of anonymous tunnel access and unrelated browser origins. It revokes its temporary test device and acknowledges only its test notification. It keeps credentials in memory.

For expired Dev Tunnels sign-in run devtunnel user login --entra. For an expired/revoked device, enroll it again. OS startup services are not installed automatically.

[Microsoft CLI reference](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands) · [Microsoft tunnel security](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security)
