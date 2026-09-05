#!/usr/bin/env bash
# Builds via build.sh first: a bare `dotnet run` hits the multi-node MSBuild failure it works around.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build.sh >/dev/null
dotnet run --no-build --project tools/Mailcoded.Protocol.TypeScript -- --out packages/protocol/src/generated.ts
