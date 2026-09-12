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
WANT_MODEL=0
BINARIES=(mailcoded mailcoded-daemon mailcoded-mcp mailcoded-tui)

# The one download this project's tooling can make, and only with --with-model. Leave these three
# empty until a human has read the licence and pinned a real digest: an unverified model is a
# binary blob from the internet that every mail body then flows through.
MODEL_NAME=""
MODEL_URL=""
MODEL_SHA256=""
MODEL_LICENCE=""

usage() {
  cat <<USAGE
usage: scripts/install.sh [--prefix DIR] [--rid RID] [--no-aot] [--with-model] [--uninstall]

  --prefix DIR   install root (default: \$HOME/.local, or \$PREFIX)
  --rid RID      target runtime identifier (default: this machine's)
  --no-aot       build every command framework-dependent instead of publishing Native AOT.
                 Much faster to install, and then all of them need the .NET runtime. Note that
                 mailcoded-mcp is framework-dependent either way.
  --with-model   download the embedding model search-by-meaning needs. No model is pinned yet,
                 so today this prints why and installs nothing. When one is pinned it will ask
                 first, print the licence and verify a checksum. Search works without it.
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
    --with-model) WANT_MODEL=1; shift ;;
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

install_model() {
  local target="$1"

  if [ -z "$MODEL_URL" ] || [ -z "$MODEL_SHA256" ]; then
    echo "no model is pinned in this checkout, so there is nothing to download." >&2
    echo "search will rank by words alone, which needs no model at all." >&2
    return 1
  fi

  echo
  echo "About to download an embedding model:"
  echo "  name     $MODEL_NAME"
  echo "  from     $MODEL_URL"
  echo "  licence  $MODEL_LICENCE"
  echo "  sha256   $MODEL_SHA256"
  echo "  into     $target"
  echo
  printf 'Download it? [y/N] '
  read -r reply
  case "$reply" in
    y | Y | yes | YES) ;;
    *) echo "skipped; search will rank by words alone."; return 1 ;;
  esac

  local scratch
  scratch="$(mktemp -d)"
  trap 'rm -rf "$scratch"' RETURN

  if ! curl -fsSL "$MODEL_URL" -o "$scratch/model.tar.gz"; then
    echo "the download failed; search will rank by words alone." >&2
    return 1
  fi

  # Verified before it is unpacked, never after: an archive is code to the thing that opens it.
  local actual
  actual="$(sha256sum "$scratch/model.tar.gz" | cut -d' ' -f1)"
  if [ "$actual" != "$MODEL_SHA256" ]; then
    echo "checksum mismatch: expected $MODEL_SHA256, got $actual" >&2
    echo "refusing to install this file." >&2
    return 1
  fi

  mkdir -p "$target"
  tar -xzf "$scratch/model.tar.gz" -C "$target"

  for required in config.json model.safetensors vocab.txt; do
    if [ ! -f "$target/$required" ]; then
      echo "the archive did not contain $required; removing it." >&2
      rm -rf "$target"
      return 1
    fi
  done

  echo "model installed; the daemon will embed your mail in the background."
}

if [ "$WANT_MODEL" = 1 ]; then
  data_dir="${XDG_DATA_HOME:-$HOME/.local/share}/mailcoded"
  case "$(uname -s)" in
    Darwin) data_dir="$HOME/Library/Application Support/mailcoded" ;;
  esac
  install_model "$data_dir/model" || true
fi

if command -v mailcoded >/dev/null 2>&1; then
  echo
  mailcoded version --json 2>/dev/null || true
fi
