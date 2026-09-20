#!/usr/bin/env bash
# Build a clean public tarball: scans for secrets, then packs only publishable files.
# Secrets (data/, bin/*, config/opencode.json) are NEVER included.
# Usage: ./scripts/pack-release.sh [out-dir]
# SPDX-License-Identifier: MIT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-$ROOT/dist}"
mkdir -p "$OUT_DIR"

INCLUDE=(
  opencode-portable.cmd opencode-portable.ps1 opencode-portable.sh
  LICENSE README.md README.it.md SECURITY.md CHANGELOG.md
  THIRD-PARTY-NOTICES.md UPSTREAM_VERSION global.json
  assets bin/.gitkeep config/opencode.example.json
  docs scripts .github
)

echo "[pack] validating include list..."
for r in "${INCLUDE[@]}"; do
  if [ ! -e "$ROOT/$r" ]; then
    echo "[pack] ERROR: include missing: $r (scan would be falsely clean)." >&2
    exit 1
  fi
done

echo "[pack] secret scan..."
# Case-insensitive like the ps1 mirror (Select-String is insensitive by default).
PATTERN='sk-[A-Za-z0-9]{20,}|sk-ant-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[bpas]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|api[_-]?key[[:space:]]*[:=][[:space:]]*['\''\"][^'\''\"]{8,}'
hits="$(cd "$ROOT" && grep -rEni -- "$PATTERN" "${INCLUDE[@]}" 2>/dev/null || true)"
if [ -n "$hits" ]; then
  echo "$hits" >&2
  echo "[pack] ERROR: possible secrets found (above)." >&2
  exit 1
fi
echo "[pack] scan clean."

TARBALL="$OUT_DIR/opencode-portable-linux-macos.tar.gz"
# Pack straight from the scanned INCLUDE list (no staging dir: nothing unpacked
# can slip in unscanned between scan and archive).
(cd "$ROOT" && tar -czf "$TARBALL" "${INCLUDE[@]}")
echo "[pack] OK -> $TARBALL"
