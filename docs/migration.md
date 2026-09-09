# Node to .NET / React migration

The architecture is now three processes/artifacts: API service, device collector, and React UI assets. The former Node runtime remains recoverable from Git commit ef36684; it is not the production entry point.

## Preserved data

On first start the API imports data/devices.json and data/state.json into data/hub.sqlite in one SQLite transaction. It preserves settings, remote device IDs/token hashes, completion IDs, acknowledgements, and manual task leases. Import is one-time. Original JSON files are retained unchanged as rollback inputs.

The original hub's collector must be separated from the API. Stop the old Node server before migrating:

```sh
dotnet artifacts/release/collector/Dashboard.Collector.dll migrate-host
```

This retains the original local device ID, issues an ignored collector.json credential, and seeds collector.sqlite with in-flight observations. Do not run it twice or over existing credentials. Existing remote collectors can continue using the v1 routes and their imported credentials. Upgrade them individually; the new .NET collector can read the existing collector.json format.

## Cutover

1. Build and test the API, collector, and UI. Back up the legacy state JSON and existing configuration.
2. Stop only this project's old server and two tunnel-host child processes.
3. Run migrate-host, then start the .NET API and the separate collector. Keep ports 4317/4319 and the saved tunnel IDs.
4. Verify every imported unread completion, global counts, device ID, and both tunnel URLs. Run verify-deployment.ps1 -Remote.
5. Use `data/ui-access-key` in default access-key mode. For owner-only Dev Tunnels, `uiAuthentication: dev-tunnel` uses Microsoft tunnel login without a second key prompt; see [remote access](remote-access.md).

Reminders now originate in the cross-platform API and reach connected browsers over SSE, including periodic reminders. Browser permission is required for OS notifications. The old server-side Windows toast helper was removed. Closed-page push notifications remain unsupported.

## Rollback

Stop the .NET API, collector, and their tunnel-host processes. Use a separate checkout of ef36684 with the backed-up state.json, devices.json, and tunnel.json, then run its Node service. Keep hub.sqlite and collector.sqlite intact for a later retry. Notifications/updates created after cutover exist only in SQLite and are not automatically backported to legacy JSON. Do not run both versions against the same ports or device identity.

## Package policy

Run `npm run setup:web` (or install-web.ps1/install-web.sh) for a locked install from npmjs with Enzyme fallback on access or connectivity failures. Root/web .npmrc and the committed lockfile use public npm URLs; npm substitutes the registry when Enzyme is selected, preserving package versions and integrity hashes. Corporate devices can select Enzyme directly using the ignored npm-install.local.json or `--registry enzyme`. See [package sources](package-sources.md) for authentication and overrides. Deployed .NET binaries need neither npm nor Node. NuGet still uses Microsoft's dotnet-public Azure Artifacts feed.
