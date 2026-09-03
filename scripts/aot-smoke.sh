#!/usr/bin/env bash
# M0 acceptance: an initialize/shutdown exchange must complete against the published AOT daemon.
set -euo pipefail

dir="${1:?usage: aot-smoke.sh <publish-dir>}"
daemon="$dir/mailcoded-daemon"
[ -x "$daemon" ] || daemon="$dir/mailcoded-daemon.exe"
[ -x "$daemon" ] || { echo "daemon binary not found in $dir"; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
out="$work/out"
err="$work/err"

frame() {
  local body="$1"
  printf 'Content-Length: %d\r\n\r\n%s' "${#body}" "$body"
}

{
  frame '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientName":"ci","clientVersion":"0","protocolVersion":1}}'
  frame '{"jsonrpc":"2.0","id":2,"method":"shutdown","params":{}}'
} | "$daemon" --store "$work/store.db" > "$out" 2> "$err" || true

fail() {
  echo "smoke test failed: $1"
  echo "--- stdout ---"; cat "$out" || true
  echo "--- stderr ---"; cat "$err" || true
  exit 1
}

grep -q '"protocolVersion"' "$out" || fail "no initialize response"
grep -q '"id":2' "$out" || fail "no shutdown response"

echo "stdio smoke test passed"

# The TUI is the only shipped client that speaks the wire; --check proves it does so in AOT.
tui="$dir/mailcoded-tui"
[ -x "$tui" ] || tui="$dir/mailcoded-tui.exe"

if [ -x "$tui" ]; then
  MAILCODED_DAEMON="$daemon" "$tui" --check --store "$work/client.db" > "$work/check" 2>&1 \
    || { echo "client check failed"; cat "$work/check"; exit 1; }
  grep -q '^ok$' "$work/check" || { echo "client check did not report ok"; cat "$work/check"; exit 1; }
  echo "client check passed"
fi
