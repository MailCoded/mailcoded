#!/usr/bin/env bash
# Builds the solution, working around two things this project hits on NixOS/WSL:
#   - nuget.org is often unreachable, so fall back to the local package cache;
#   - the multi-node MSBuild worker fails silently (it cannot resolve the workload
#     locator SDKs), so build on a single node.
set -euo pipefail
cd "$(dirname "$0")/.."

if ! dotnet restore Mailcoded.slnx; then
  echo "== nuget.org unreachable; restoring from the local package cache" >&2
  dotnet restore Mailcoded.slnx --configfile build/NuGet.offline.config
fi

dotnet build Mailcoded.slnx --no-restore -m:1 "$@"
