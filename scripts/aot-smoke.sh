#!/usr/bin/env bash
# M0 acceptance: an initialize/shutdown exchange must complete against the published AOT daemon.
set -euo pipefail

dir="${1:?usage: aot-smoke.sh <publish-dir>}"
daemon="$dir/mailcoded-daemon"
[ -x "$daemon" ] || daemon="$dir/mailcoded-daemon.exe"
[ -x "$daemon" ] || { echo "daemon binary not found in $dir"; exit 1; }

store="$(mktemp -d)/store.db"

frame() {
  local body="$1"
  printf 'Content-Length: %d\r\n\r\n%s' "${#body}" "$body"
}

{
  frame '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientName":"ci","clientVersion":"0","protocolVersion":1}}'
  frame '{"jsonrpc":"2.0","id":2,"method":"shutdown","params":{}}'
} | "$daemon" --store "$store" > /tmp/mailcoded-smoke.out 2>/tmp/mailcoded-smoke.err || true

if ! grep -q '"protocolVersion"' /tmp/mailcoded-smoke.out; then
  echo "smoke test failed: no initialize response"
  echo "--- stdout ---"; cat /tmp/mailcoded-smoke.out
  echo "--- stderr ---"; cat /tmp/mailcoded-smoke.err
  exit 1
fi

echo "stdio smoke test passed"
