#!/usr/bin/env sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec node --experimental-strip-types "$root/scripts/install-web.ts" "$@"
