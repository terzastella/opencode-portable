#!/usr/bin/env bash
# Download the matching upstream opencode binary into ./bin/
# Usage: ./scripts/setup.sh [--version 1.19.0]
# SPDX-License-Identifier: MIT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN_DIR="$ROOT/bin"
mkdir -p "$BIN_DIR"

VERSION="${1:-}"
if [ "${1:-}" = "--version" ]; then
  if [ -z "${2:-}" ]; then
    echo "[setup] ERROR: --version requires a value (e.g. --version 1.18.35)." >&2
    exit 1
  fi
  VERSION="$2"
fi
VERSION="${VERSION#v}"
# Explicit versions are validated like the pin: an untrusted VERSION must
# never reach URL interpolation raw (path probing / log injection).
if [ -n "$VERSION" ] && [ "$VERSION" != "latest" ]; then
  if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "[setup] ERROR: invalid version '$VERSION' (expected x.y.z or 'latest')." >&2
    exit 1
  fi
fi

raw_os=$(uname -s)
os=$(echo "$raw_os" | tr '[:upper:]' '[:lower:]')
case "$raw_os" in
  Darwin*) os="darwin" ;;
  Linux*) os="linux" ;;
  MINGW*|MSYS*|CYGWIN*) os="windows" ;;
esac

arch=$(uname -m)
if [[ "$arch" == "aarch64" ]]; then arch="arm64"; fi
if [[ "$arch" == "x86_64" ]]; then arch="x64"; fi
case "$os-$arch" in
  linux-x64|linux-arm64|darwin-x64|darwin-arm64|windows-x64|windows-arm64) ;;
  *)
    echo "[setup] ERROR: unsupported OS/arch: $os/$arch (upstream: linux/darwin/windows x64/arm64)." >&2
    exit 1
    ;;
esac

REPO="anomalyco/opencode"

# v1 (1.x): GitHub zip/tar.gz. v2 (2.x): npm tgz (no GitHub binary assets).
# Resolve VERSION first, then pick distribution by major.
resolve_latest_npm() {
  # Prints latest cli version from metadata endpoint, or empty on failure.
  curl -fsSL --connect-timeout 15 --max-time 60 "https://opencode.ai/update/api/latest/cli/npm" 2>/dev/null \
    | sed -n 's/.*"version":"\([^"]*\)".*/\1/p' | sed 's/^v//;s/[[:space:]]//g' || true
}

if [ -z "$VERSION" ] || [ "$VERSION" = "latest" ]; then
  # Pinned by default (reproducible): explicit --version always wins.
  pinned=""
  if [ -f "$ROOT/UPSTREAM_VERSION" ]; then
    pinned="$(tr -d '[:space:]' < "$ROOT/UPSTREAM_VERSION")"
  fi
  if [[ "$pinned" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    VERSION="$pinned"
    echo "[setup] using pinned v$VERSION (UPSTREAM_VERSION)"
  else
    VERSION="$(resolve_latest_npm)"
    if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      echo "[setup] ERROR: no valid UPSTREAM_VERSION pin and latest unreachable." >&2
      exit 1
    fi
    echo "[setup] using latest v$VERSION (no valid pin)"
  fi
fi

is_v2=0
if [[ "$VERSION" =~ ^2\. ]]; then is_v2=1; fi

target="$os-$arch"
if [ "$is_v2" = "1" ]; then
  filename="cli-$target-$VERSION.tgz"
  npm_pkg="@opencode/cli-$target"
  url="https://registry.npmjs.org/$npm_pkg/-/cli-$target-$VERSION.tgz"
  echo "[setup] downloading v$VERSION (npm $npm_pkg): $filename"
  ext=".tgz"
else
  ext=".zip"
  if [ "$os" = "linux" ]; then ext=".tar.gz"; fi
  filename="opencode-$target$ext"
  url="https://github.com/$REPO/releases/download/v$VERSION/$filename"
  echo "[setup] downloading v$VERSION: $filename"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl_args=(-fSL --retry 3 --retry-all-errors --connect-timeout 30 --max-time 600)
# Authenticated rate limits for CI (60 req/h anonymous): honor GITHUB_TOKEN.
# Via --config on stdin (never argv): invisible to `ps` on shared hosts.
# npm needs no auth; header is harmless there but only send for github.com.
if [[ "$url" == *"github.com"* ]] && [ -n "${GITHUB_TOKEN:-}" ]; then
  printf 'header = "Authorization: Bearer %s"\n' "$GITHUB_TOKEN" \
    | curl --config - "${curl_args[@]}" -o "$tmp/$filename" "$url"
else
  if ! curl "${curl_args[@]}" -o "$tmp/$filename" "$url"; then
    # Fallback for renamed scope: @opencode (current) -> @opencode-ai (legacy).
    if [ "$is_v2" = "1" ]; then
      alt_pkg="@opencode-ai/cli-$target"
      alt_url="https://registry.npmjs.org/$alt_pkg/-/cli-$target-$VERSION.tgz"
      echo "[setup] retry with legacy scope $alt_pkg..." >&2
      curl "${curl_args[@]}" -o "$tmp/$filename" "$alt_url"
    else
      exit 1
    fi
  fi
fi

# Hash pin: fail closed when recorded (see UPSTREAM_VERSION.sha256, maintained
# by publish-fat); TLS-only trust otherwise. Size floor like the ps1 mirror.
size="$(wc -c < "$tmp/$filename" | tr -d '[:space:]')"
if [ "${size:-0}" -lt 10485760 ]; then
  echo "[setup] ERROR: downloaded archive suspiciously small (${size}B), refusing install." >&2
  exit 1
fi
if command -v sha256sum >/dev/null 2>&1 && [ -f "$ROOT/UPSTREAM_VERSION.sha256" ]; then
  pin="$(grep -E "^$target[[:space:]]+$VERSION[[:space:]]+[0-9a-f]{64}[[:space:]]*$" "$ROOT/UPSTREAM_VERSION.sha256" | head -n 1 || true)"
  if [ -n "$pin" ]; then
    expected="$(echo "$pin" | grep -Eo '[0-9a-f]{64}' | head -n 1)"
    actual="$(sha256sum "$tmp/$filename" | cut -d' ' -f1)"
    if [ "$actual" != "$expected" ]; then
      echo "[setup] ERROR: downloaded archive hash mismatch (possible tampering), refusing install." >&2
      exit 1
    fi
    echo "[setup] hash pin verified."
  else
    echo "[setup] WARNING: no recorded hash for $target v$VERSION, TLS-only trust." >&2
  fi
fi

# Provenance (Sigstore release attestation, v1 GitHub assets only): enforced
# when the `gh` CLI exists (hash pin above is always enforced). Upstream uses
# the in-toto release predicate, not gh's SLSA default.
if [ "$is_v2" != "1" ] && command -v gh >/dev/null 2>&1; then
  set +e
  gh_out="$(gh attestation verify "$tmp/$filename" --repo anomalyco/opencode --predicate-type https://in-toto.io/attestation/release/v0.2 2>&1)"
  gh_rc=$?
  set -e
  if [ "$gh_rc" -eq 0 ]; then
    echo "[setup] attestation verificata (provenance: anomalyco/opencode)."
  elif printf '%s' "$gh_out" | grep -qi "no attestation"; then
    echo "[setup] WARNING: no attestation for this asset, hash pin only." >&2
  else
    echo "[setup] ERROR: attestation verification FAILED, refusing install." >&2
    echo "$gh_out" >&2
    exit 1
  fi
fi

if [ "$ext" = ".tgz" ] || [ "$os" = "linux" ]; then
  tar -xzf "$tmp/$filename" -C "$tmp"
else
  command -v unzip >/dev/null 2>&1 || { echo "[setup] ERROR: 'unzip' is required but not installed." >&2; exit 1; }
  unzip -q -o "$tmp/$filename" -d "$tmp"
fi

# Archive contains a single `opencode` (or `opencode.exe`) binary, possibly
# nested in a subdirectory: exact name first, then recursive fallback.
found=""
if [ -f "$tmp/opencode" ]; then
  found="$tmp/opencode"
elif [ -f "$tmp/opencode.exe" ]; then
  found="$tmp/opencode.exe"
else
  found="$(find "$tmp" -name 'opencode' -type f 2>/dev/null | head -n 1)"
  if [ -z "$found" ]; then
    found="$(find "$tmp" -name 'opencode.exe' -type f 2>/dev/null | head -n 1)"
  fi
fi
if [ -z "$found" ]; then
  echo "[setup] ERROR: no opencode binary found in archive" >&2
  exit 1
fi
cp -f "$found" "$BIN_DIR/opencode.new"
mv -f "$BIN_DIR/opencode.new" "$BIN_DIR/opencode"
chmod +x "$BIN_DIR/opencode"
echo "[setup] OK -> $BIN_DIR/opencode"
# A corrupt binary must fail the install, not exit 0 behind it.
"$BIN_DIR/opencode" --version
