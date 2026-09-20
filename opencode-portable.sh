#!/usr/bin/env bash
# ============================================================
#  Opencode Portable launcher (Linux / macOS)
#  Mirror of opencode-portable.cmd/.ps1: config, data, cache
#  and logs stay inside ./data. Environment variables are
#  only exported for this process (no shell pollution).
#  SPDX-License-Identifier: MIT
# ============================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA="$ROOT/data"
for d in config data cache state tmp; do
  mkdir -p "$DATA/$d"
done

# First run: create config/opencode.json from template if missing
if [ ! -f "$ROOT/config/opencode.json" ] && [ -f "$ROOT/config/opencode.example.json" ]; then
  cp "$ROOT/config/opencode.example.json" "$ROOT/config/opencode.json"
fi

export XDG_CONFIG_HOME="$DATA/config"
export XDG_DATA_HOME="$DATA/data"
export XDG_CACHE_HOME="$DATA/cache"
export XDG_STATE_HOME="$DATA/state"
export OPENCODE_CONFIG="$ROOT/config/opencode.json"
# No blocking models.dev fetch at startup (10-30s): use cache/builtin
export OPENCODE_DISABLE_MODELS_FETCH=1

# Snapshot of system locations BEFORE redirect (guard: only remove what this run creates)
SYS_TARGETS=()
for p in "$HOME/.config/opencode" "$HOME/.local/share/opencode" "$HOME/.cache/opencode"; do
  if [ -e "$p" ]; then
    SYS_TARGETS+=("$p|1")
  else
    SYS_TARGETS+=("$p|0")
  fi
done

# Isolated tmp for this run (safe with concurrent instances)
TMPROOT="$DATA/tmp"
RUN_TMP="$(mktemp -d "$TMPROOT/run-$(date +%Y%m%d-%H%M%S)-$$-XXXXXX" 2>/dev/null || echo "")"
CLEAN_RUN_TMP=1
if [ -z "$RUN_TMP" ] || [ ! -d "$RUN_TMP" ]; then
  RUN_TMP="$TMPROOT"
  CLEAN_RUN_TMP=0
fi

# Sweeper: dead run tmps (crash/kill leftovers). STRICT: only names matching our
# exact stamp AND dead PID; anything else (user dirs, garbage) is never touched.
if [ -d "$TMPROOT" ]; then
  for d in "$TMPROOT"/run-*; do
    [ -d "$d" ] || continue
    base=${d##*/}
    case "$base" in
      run-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]-[0-9]*-[0-9]*)
        ;;
      *) continue ;;
    esac
    pid=$(echo "$base" | awk -F- '{print $4}')
    alive=0
    case "$pid" in ''|*[!0-9]*) ;; *) kill -0 "$pid" 2>/dev/null && alive=1 ;; esac
    if [ "$alive" = "0" ]; then
      rm -rf "$d" 2>/dev/null || true
    fi
  done
fi

TMPROOT_OPENC="$TMPROOT/opencode"
if [ -e "$TMPROOT_OPENC" ]; then TMPROOT_OPENC_EXISTS=1; else TMPROOT_OPENC_EXISTS=0; fi

export TMPDIR="$RUN_TMP"
export TMP="$RUN_TMP"
export TEMP="$RUN_TMP"

cleanup() {
  if [ "$CLEAN_RUN_TMP" = "1" ] && [ -n "${RUN_TMP:-}" ] && [ -d "${RUN_TMP:-}" ]; then
    case "$RUN_TMP" in
      "$TMPROOT"/run-*) rm -rf "$RUN_TMP" 2>/dev/null || true ;;
    esac
  fi
  if [ "${TMPROOT_OPENC_EXISTS:-1}" = "0" ] && [ -e "$TMPROOT_OPENC" ]; then
    rm -rf "$TMPROOT_OPENC" 2>/dev/null || true
  fi
  for entry in "${SYS_TARGETS[@]}"; do
    p="${entry%|*}"
    existed="${entry#*|}"
    if [ "$existed" = "0" ] && [ -e "$p" ]; then
      rm -rf "$p" 2>/dev/null || true
    fi
  done
}
trap cleanup EXIT INT TERM

# Bundled binary. PATH fallback ONLY with explicit opt-in (--from-path as first
# argument, or OPENCODE_ALLOW_PATH_FALLBACK=1): without opt-in a same-name
# executable in PATH would run silently.
ALLOW_PATH=0
[ "${OPENCODE_ALLOW_PATH_FALLBACK:-}" = "1" ] && ALLOW_PATH=1
if [ "${1:-}" = "--from-path" ]; then ALLOW_PATH=1; shift; fi

BIN="$ROOT/bin/opencode"
if [ ! -x "$BIN" ]; then
  if [ "$ALLOW_PATH" != "1" ]; then
    echo "[opencode-portable] ERROR: $BIN not found." >&2
    echo "[opencode-portable] Run scripts/setup.sh, or pass --from-path first / set OPENCODE_ALLOW_PATH_FALLBACK=1 to reuse opencode from PATH." >&2
    exit 1
  fi
  if command -v opencode >/dev/null 2>&1; then
    BIN="opencode"
    echo "[opencode-portable] WARNING: using opencode from PATH: $(command -v opencode)" >&2
  else
    echo "[opencode-portable] ERROR: no opencode found in PATH." >&2
    exit 1
  fi
fi

# NOTE: no `exec` here on purpose: exec would replace this shell and the EXIT
# trap cleanup would never run, leaking RUN_TMP on every successful run.
"$BIN" "$@"
code=$?
exit "$code"
