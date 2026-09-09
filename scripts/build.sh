#!/usr/bin/env sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
dotnet restore AgentDashboard.slnx --configfile NuGet.Config --artifacts-path artifacts/unix
dotnet test AgentDashboard.slnx --no-restore --artifacts-path artifacts/unix -c Release
(cd web && npm run build)
dotnet publish src/Dashboard.Api --no-restore --artifacts-path artifacts/unix -c Release -o artifacts/release/hub
dotnet publish src/Dashboard.Collector --no-restore --artifacts-path artifacts/unix -c Release -o artifacts/release/collector
