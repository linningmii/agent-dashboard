# Remote access

The API runs two distinct listeners:

| Listener | Default port | Access |
|---|---|---|
| Dashboard UI + administration | 4317 | Service access key / session cookie |
| Collector ingestion | 4319 | One-time enrollment code, then device-scoped token |

## Microsoft Dev Tunnels

Copy tunnel.example.json to ignored tunnel.json and configure one persistent tunnel per port. Both retain owner-only Microsoft authentication in addition to application authentication. The API verifies that each tunnel has exactly its configured HTTP port, no expanded access rules, and no request timeout before hosting it.

The tunnel worker starts on API startup, retries exits, and renews expiration to 30 days at startup and daily. URLs survive process restarts but can be lost if the tunnel expires after a long outage. The machine must remain awake and connected. Obtain both observed URLs from authenticated GET /api/tunnels; GET /api/tunnel remains the UI-only compatibility endpoint. Do not construct hostnames from friendly IDs.

```powershell
devtunnel user login --entra
devtunnel create YOUR-UI-ID --expiration 30d
devtunnel port create YOUR-UI-ID.CLUSTER --port-number 4317 --protocol http --request-timeout 0
devtunnel create YOUR-INGESTION-ID --expiration 30d
devtunnel port create YOUR-INGESTION-ID.CLUSTER --port-number 4319 --protocol http --request-timeout 0
```

Open the UI URL, sign into the tunnel owner's Microsoft account, then enter the API service's access key. On the host, the default key is in data/ui-access-key. API clients may send Authorization: Bearer SERVICE_KEY. Collector clients use Authorization: Bearer DEVICE_TOKEN and, for private tunnels, X-Tunnel-Authorization: tunnel CONNECT_TOKEN. Tokens are never put into URLs.

## Ordinary Linux hosting

A tunnel is optional. Configure --bind for the internal listener address and --ingestion-url with the public HTTPS ingestion origin, then put an HTTPS reverse proxy in front of the two ports. Route each public origin only to its corresponding internal port, disable SSE response buffering, and allow streaming connections. The application validates access keys/device credentials independently of the proxy. Do not expose unencrypted listener ports to an untrusted network.

The React build is served from the published API's wwwroot, or --web-root. If developing separately with Vite, /api is proxied to the API listener. No separate frontend server is needed for deployment.

## Verify and troubleshoot

```powershell
pwsh scripts/verify-deployment.ps1 -Remote
Get-Content data/hub.log -Tail 30
Get-Content data/collector-error.log -Tail 30
```

The verifier checks authenticated HTML, UI/ingestion separation, synthetic device registration/reporting, completion/replay, and streamed updates. It revokes its temporary test device and acknowledges only its test notification. It keeps credentials in memory.

For expired Dev Tunnels sign-in run devtunnel user login --entra. For an expired/revoked device, enroll it again. OS startup services are not installed automatically.

[Microsoft CLI reference](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands) · [Microsoft tunnel security](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security)
