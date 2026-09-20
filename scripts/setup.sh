#!/usr/bin/env bash
# Download the matching upstream opencode binary into ./bin/
# Usage: ./scripts/setup.sh [--version 1.18.31]
# SPDX-License-Identifier: MIT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN_DIR="$ROOT/bin"
mkdir -p "$BIN_DIR"

VERSION="${1:-}"
if [ "${1:-}" = "--version" ]; then
  if [ -z "${2:-}" ]; then
    echo "[setup] ERROR: --version requires a value (e.g. --version 1.18.31)." >&2
    exit 1
  fi
  VERSION="$2"
fi
VERSION="${VERSION#v}"

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
ext=".zip"
if [ "$os" = "linux" ]; then ext=".tar.gz"; fi

target="$os-$arch"
filename="opencode-$target$ext"

if [ -z "$VERSION" ]; then
  # Pinned by default (reproducible): explicit --version always wins.
  pinned=""
  if [ -f "$ROOT/UPSTREAM_VERSION" ]; then
    pinned="$(tr -d '[:space:]' < "$ROOT/UPSTREAM_VERSION")"
  fi
  if [[ "$pinned" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    VERSION="$pinned"
    url="https://github.com/$REPO/releases/download/v$VERSION/$filename"
    echo "[setup] downloading pinned v$VERSION (UPSTREAM_VERSION): $filename"
  else
    url="https://github.com/$REPO/releases/latest/download/$filename"
    echo "[setup] downloading latest: $filename"
  fi
else
  url="https://github.com/$REPO/releases/download/v$VERSION/$filename"
  echo "[setup] downloading v$VERSION: $filename"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl_args=(-fL)
# Authenticated rate limits for CI (60 req/h anonymous): honor GITHUB_TOKEN.
if [ -n "${GITHUB_TOKEN:-}" ]; then
  curl_args+=(-H "Authorization: Bearer $GITHUB_TOKEN")
fi
curl "${curl_args[@]}" -o "$tmp/$filename" "$url"

if [ "$os" = "linux" ]; then
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
cp -f "$found" "$BIN_DIR/opencode"
chmod +x "$BIN_DIR/opencode"
echo "[setup] OK -> $BIN_DIR/opencode"
"$BIN_DIR/opencode" --version || true
