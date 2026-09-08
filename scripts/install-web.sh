#!/usr/bin/env sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")/../web" && pwd)"
# A temporary reference file, not a token file. Enzyme credentials never enter Git or stdout.
config=$(mktemp)
trap 'rm -f "$config"' EXIT
export ENZYME_NPM_TOKEN=$(az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv)
test -n "$ENZYME_NPM_TOKEN"
printf '%s\n' 'registry=https://o365exchange.pkgs.visualstudio.com/_packaging/Enzyme/npm/registry/' '//o365exchange.pkgs.visualstudio.com/_packaging/Enzyme/npm/registry/:_authToken=${ENZYME_NPM_TOKEN}' > "$config"
NPM_CONFIG_USERCONFIG="$config" npm ci --ignore-scripts --audit=false --fund=false
