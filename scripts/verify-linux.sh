#!/usr/bin/env sh
set -eu
# Requires an installed .NET 10 SDK. No automatic SDK download.
if [ -n "${DOTNET_ROOT:-}" ]; then export PATH="$DOTNET_ROOT:$PATH"; fi
command -v dotnet >/dev/null 2>&1 || { echo '.NET 10 SDK is required'; exit 1; }
cd "$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
dotnet restore AgentDashboard.slnx --configfile NuGet.Config --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json --artifacts-path artifacts/linux
dotnet test AgentDashboard.slnx --no-restore --artifacts-path artifacts/linux --verbosity minimal
