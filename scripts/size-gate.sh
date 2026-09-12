#!/usr/bin/env bash
# Thresholds are docs/DEPENDENCIES.md's. Every file in the directory is measured, so nothing ships
# uncounted; the total is reported rather than gated because no threshold for it is written down.
set -euo pipefail

dir="${1:?usage: size-gate.sh <publish-dir>}"

warn_raw=$((12 * 1024 * 1024))
fail_raw=$((20 * 1024 * 1024))
warn_gz=$((6 * 1024 * 1024))
fail_gz=$((10 * 1024 * 1024))

status=0
total_raw=0
total_gz=0

mib() { awk "BEGIN{printf \"%.2f\", $1/1048576}"; }

# StripSymbols is on, so the .dbg beside a binary is a build artefact rather than payload.
shipped() {
  case "$1" in
    *.dbg | *.pdb | *.mstat | *.map | *.xml) return 1 ;;
    *) return 0 ;;
  esac
}

[ -d "$dir" ] || { echo "size-gate: '$dir' is not a directory"; exit 1; }

while IFS= read -r -d '' file; do
  name="${file#"$dir"/}"
  shipped "$name" || continue

  raw=$(wc -c < "$file")
  gz=$(gzip -9 -c "$file" | wc -c)
  total_raw=$((total_raw + raw))
  total_gz=$((total_gz + gz))

  verdict="OK  "
  note=""
  if [ "$raw" -gt "$fail_raw" ]; then
    verdict="FAIL"; note=" — over the 20 MB uncompressed gate"; status=1
  elif [ "$gz" -gt "$fail_gz" ]; then
    verdict="FAIL"; note=" — over the 10 MB compressed gate"; status=1
  elif [ "$raw" -gt "$warn_raw" ]; then
    verdict="WARN"; note=" — over the 12 MB uncompressed warning"
  elif [ "$gz" -gt "$warn_gz" ]; then
    verdict="WARN"; note=" — over the 6 MB compressed warning"
  fi

  printf '%s %-28s %8s MiB  (%s MiB gzipped)%s\n' \
    "$verdict" "$name" "$(mib "$raw")" "$(mib "$gz")" "$note"
done < <(find "$dir" -type f -print0 | sort -z)

printf '\n     %-28s %8s MiB  (%s MiB gzipped)\n' \
  "TOTAL shipped payload" "$(mib "$total_raw")" "$(mib "$total_gz")"

exit $status
