#!/usr/bin/env bash
# Builds the solution, falling back to an offline restore when nuget.org is unreachable.
set -euo pipefail
cd "$(dirname "$0")/.."

if ! dotnet restore Mailcoded.slnx; then
  echo "== nuget.org unreachable; restoring from the local package cache" >&2
  dotnet restore Mailcoded.slnx --configfile build/NuGet.offline.config
fi

dotnet build Mailcoded.slnx --no-restore "$@"
