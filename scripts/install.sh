#!/usr/bin/env bash
# Installs mailcoded, mailcoded-daemon, mailcoded-mcp and mailcoded-tui onto PATH.
#
# The payload goes to $PREFIX/libexec/mailcoded and only symlinks land in $PREFIX/bin.
# NOT $PREFIX/share/mailcoded: on Linux that is $XDG_DATA_HOME/mailcoded, which is the mail
# store itself — installing there would put binaries in the store and wipe it on reinstall.
# That is not tidiness: the AOT binaries load libe_sqlite3.so from their own directory and
# will not start without it, so the executable and the library have to stay together.
# A symlink is fine — the runtime resolves /proc/self/exe to the real directory — and it
# costs nothing at startup, which matters for a CLI an agent invokes in a loop.
set -euo pipefail
cd "$(dirname "$0")/.."

PREFIX="${PREFIX:-$HOME/.local}"
LIBEXEC="$PREFIX/libexec/mailcoded"
BINDIR="$PREFIX/bin"
RID=""
AOT=1
BINARIES=(mailcoded mailcoded-daemon mailcoded-mcp mailcoded-tui)

usage() {
  cat <<USAGE
usage: scripts/install.sh [--prefix DIR] [--rid RID] [--no-aot] [--uninstall]

  --prefix DIR   install root (default: \$HOME/.local, or \$PREFIX)
  --rid RID      target runtime identifier (default: this machine's)
  --no-aot       install the framework-dependent build instead of publishing Native AOT.
                 Much faster to install; needs the .NET runtime present to run.
  --uninstall    remove the symlinks and the payload directory
USAGE
}

uninstall() {
  local removed=0
  for b in "${BINARIES[@]}"; do
    if [ -L "$BINDIR/$b" ] || [ -e "$BINDIR/$b" ]; then
      rm -f "$BINDIR/$b"; echo "removed $BINDIR/$b"; removed=1
    fi
  done
  if [ -d "$LIBEXEC" ]; then
    for marker in store.db blobs secrets.vault; do
      [ -e "$LIBEXEC/$marker" ] && { echo "refusing to remove $LIBEXEC: it contains $marker" >&2; exit 1; }
    done
    rm -rf "$LIBEXEC"; echo "removed $LIBEXEC"; removed=1
  fi
  [ "$removed" -eq 1 ] || echo "nothing to uninstall under $PREFIX"
  exit 0
}

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) PREFIX="$2"; LIBEXEC="$PREFIX/libexec/mailcoded"; BINDIR="$PREFIX/bin"; shift 2 ;;
    --rid) RID="$2"; shift 2 ;;
    --no-aot) AOT=0; shift ;;
    --uninstall) uninstall ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -z "$RID" ]; then
  RID="$(dotnet --info 2>/dev/null | sed -n 's/^ *RID: *//p' | head -1)"
  [ -n "$RID" ] || { echo "could not detect a runtime identifier; pass --rid" >&2; exit 1; }
fi

restore() {
  dotnet restore Mailcoded.slnx >/dev/null 2>&1 && return 0
  echo "== nuget.org unreachable; restoring from the local package cache" >&2
  dotnet restore Mailcoded.slnx --configfile build/NuGet.offline.config >/dev/null
}

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

echo "== installing mailcoded ($RID) into $PREFIX"
restore

if [ "$AOT" -eq 1 ]; then
  for proj in Mailcoded.Cli Mailcoded.Daemon Mailcoded.Tui; do
    echo "== publishing $proj (Native AOT)"
    dotnet publish "src/$proj/$proj.csproj" -c Release -r "$RID" -p:PublishAot=true \
      --no-restore -o "$staging" >/dev/null
  done
  # The MCP adapter is not AOT: the SDK is not on the AOT-clean list (docs/DEPENDENCIES.md).
  # No -r here — a framework-dependent publish needs no RID, and asking for one requires a
  # RID-specific restore the solution does not do.
  echo "== publishing Mailcoded.Mcp (framework-dependent)"
  dotnet publish src/Mailcoded.Mcp/Mailcoded.Mcp.csproj -c Release \
    --self-contained false --no-restore -o "$staging" >/dev/null
else
  # PublishAot implies PublishTrimmed, and trimming a framework-dependent app is an SDK error
  # (NETSDK1102). Turning AOT off here is what --no-aot means.
  for proj in Mailcoded.Cli Mailcoded.Daemon Mailcoded.Mcp Mailcoded.Tui; do
    echo "== publishing $proj (framework-dependent)"
    dotnet publish "src/$proj/$proj.csproj" -c Release --self-contained false -p:PublishAot=false \
      --no-restore -o "$staging" >/dev/null
  done
fi

# Refuse to touch anything that looks like a mail store, whatever --prefix was given.
for marker in store.db store.db-wal blobs secrets.vault; do
  if [ -e "$LIBEXEC/$marker" ]; then
    echo "refusing to install over what looks like a mail store: $LIBEXEC contains $marker" >&2
    echo "pick a different --prefix" >&2
    exit 1
  fi
done

mkdir -p "$LIBEXEC" "$BINDIR"
rm -rf "${LIBEXEC:?}/"*
cp -a "$staging/." "$LIBEXEC/"

installed=()
for b in "${BINARIES[@]}"; do
  if [ -x "$LIBEXEC/$b" ]; then
    ln -sfn "$LIBEXEC/$b" "$BINDIR/$b"
    installed+=("$b")
  else
    echo "warning: $b was not produced; skipping" >&2
  fi
done

echo
echo "installed: ${installed[*]}"
echo "  payload  $LIBEXEC"
echo "  symlinks $BINDIR"

case ":$PATH:" in
  *":$BINDIR:"*) ;;
  *) echo
     echo "WARNING: $BINDIR is not on your PATH. Add it, e.g.:"
     echo "  export PATH=\"$BINDIR:\$PATH\"" ;;
esac

if command -v mailcoded >/dev/null 2>&1; then
  echo
  mailcoded version --json 2>/dev/null || true
fi
