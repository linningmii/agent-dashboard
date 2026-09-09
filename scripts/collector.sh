#!/usr/bin/env sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
exec dotnet artifacts/release/collector/Dashboard.Collector.dll "$@"
