#!/usr/bin/env bash
# Runs the unit suite. Integration tests need Docker and are run separately.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/build.sh "$@"
exec tests/Mailcoded.Core.Tests/bin/Debug/net10.0/Mailcoded.Core.Tests
