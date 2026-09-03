#!/usr/bin/env bash
# docs/DEPENDENCIES.md: warn above 12 MB uncompressed per RID, fail above 20 MB.
set -euo pipefail

dir="${1:?usage: size-gate.sh <publish-dir>}"
warn=$((12 * 1024 * 1024))
fail=$((20 * 1024 * 1024))
status=0

for f in "$dir"/mailcoded-daemon "$dir"/mailcoded-daemon.exe \
         "$dir"/mailcoded "$dir"/mailcoded.exe \
         "$dir"/mailcoded-mcp "$dir"/mailcoded-mcp.exe \
         "$dir"/mailcoded-tui "$dir"/mailcoded-tui.exe; do
  [ -f "$f" ] || continue
  size=$(wc -c < "$f")
  mb=$(awk "BEGIN{printf \"%.1f\", $size/1048576}")
  if [ "$size" -gt "$fail" ]; then
    echo "FAIL $(basename "$f"): ${mb} MB exceeds the 20 MB gate"
    status=1
  elif [ "$size" -gt "$warn" ]; then
    echo "WARN $(basename "$f"): ${mb} MB exceeds the 12 MB warning threshold"
  else
    echo "OK   $(basename "$f"): ${mb} MB"
  fi
done

exit $status
