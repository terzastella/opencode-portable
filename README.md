# Opencode Portable

<img src="assets/logo.png" width="128" alt="Opencode Portable logo">

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-blue)
[![Release](https://img.shields.io/github/v/release/terzastella/opencode-portable)](https://github.com/terzastella/opencode-portable/releases)

> Designed for opencode's built-in free models — no login, no providers to
> connect. External providers (NVIDIA, Anthropic, OpenAI, …) were **not
> tested** with this project: API keys are accepted per session but provider
> setups are unverified. Read this in [Italiano](README.it.md).

Run [opencode](https://github.com/anomalyco/opencode) as a **portable app on
Windows 10/11**: config, data, cache, state and temp stay inside this folder. Nothing is written to `%APPDATA%`, `%LOCALAPPDATA%`, `~/.config` or `~/.local/share` — and anything opencode creates outside is cleaned up if it was born during that run.

Tested with opencode `1.18.31` (snapshot version; setup scripts default to latest upstream).

## Download

No install, no setup: grab **`OpencodePortable.exe`** from the
[latest release](https://github.com/terzastella/opencode-portable/releases),
put it anywhere (Desktop, USB stick), double-click it. That's the whole
installation. (Companion `.cmd`/`.ps1`/`.sh` launchers plus Linux/macOS
support live in the source below.)

## Quickstart

```bash
git clone https://github.com/terzastella/opencode-portable.git
cd opencode-portable
```

(End users: you don't need this — see [Download](#download) above.)

### Option A — single exe (Windows, recommended)

Build once (requires .NET 10 SDK), then distribute just the exe — fully
offline at runtime, it never downloads anything:

```powershell
.\scripts\publish-fat.ps1
# -> dist/OpencodePortable.exe (~137 MB: runtime + embedded opencode)
```

Double-click it: pick the work folder in the explorer dialog, opencode starts
inside it. On first run it extracts the embedded opencode binary by itself
(no network). Everything the app writes — config, cache, state, tmp, binary —
lives in a per-run `<workspace>/.opencode-portable-<timestamp>-<pid>-<rand>/`
folder, **deleted on exit, always**: delete... nothing, it's already gone. Closing the
window (X / Alt+F4) is handled too: the folder is atomically renamed to trash
first (instant), then the child tree is killed and the trash deleted with the
remaining grace time — leftovers, if any, are swept on next run. Each concurrent run gets its own folder; crash leftovers are swept on the
next run (live processes are never touched). Nothing is ever written next to
the exe, and the dialog always starts fresh (no memory). No login is stored:
built-in free models just work, provider API keys are typed per session.
Flags: `--workspace <dir>` skips the dialog, `--console` goes straight to
drag-and-drop input (for machines where the native dialog misbehaves),
`--from-path` reuses opencode
from PATH (same fail-closed rules as the scripts), `--self-test` checks the
native folder dialog without showing it. If the shell dialog cannot return
the selection, the exe falls back to drag-and-drop input in the console.
(The picker runs in a child process, so even a native crash degrades to the
fallback instead of killing the app.)
Deletion is retried for seconds against locked files (running image,
antivirus) and leftovers are reported, never silent; `--clean <workspace>`
sweeps stale roots on demand (e.g. after a Task Manager kill).
A watchdog process covers even hard kills: whatever terminates the parent —
exit, crash, window close, Task Manager — wakes it and it deletes the
per-run folder. It runs as a renamed copy (`opwatch-<pid>-<rand>.exe`) detached
from the console, so killing the app group in Task Manager spares it; after
cleaning, the copy deletes itself (stale copies are swept at startup).
It also kills processes still executing from inside the folder, and writes
`%TEMP%\opencode-portable-watchdog-<pid>.log` (diagnostic; removed on success,
left as evidence on failure).

### Option B — scripts

Download the binary for your OS into `bin/` (binaries are **not** committed to git):

```powershell
# Windows
.\scripts\setup.ps1
# optionally: .\scripts\setup.ps1 -Version 1.18.31
```

```bash
# Linux / macOS
./scripts/setup.sh
# optionally: ./scripts/setup.sh --version 1.18.31
```

Run it:

```cmd
:: Windows (double-click works too)
opencode-portable.cmd --help
```

```powershell
# Windows PowerShell (pipeline-capable)
.\opencode-portable.ps1 --help
```

```bash
# Linux / macOS
./opencode-portable.sh --help
```

On first run `config/opencode.json` is auto-created from `config/opencode.example.json`.

## Structure

```
.
├── dist/OpencodePortable.exe  # single-file Windows app (build: scripts/publish-fat.ps1)
├── opencode-portable.cmd   # Windows launcher (double-click friendly)
├── opencode-portable.ps1   # Windows PowerShell launcher (mirror)
├── opencode-portable.sh    # Linux / macOS launcher (mirror)
├── bin/                    # binaries go here (git-ignored, see scripts/setup.*)
├── assets/                 # logo.svg, logo.png, opencode-portable.ico
├── config/
│   ├── opencode.example.json  # tracked template
│   └── opencode.json          # your local config (git-ignored, auto-created)
├── data/                   # all runtime files (git-ignored)
│   ├── config/ data/ cache/ state/ tmp/
├── scripts/
│   ├── setup.ps1           # download windows-x64/arm64 binary
│   ├── setup.sh            # download linux/darwin x64/arm64 binary
│   ├── publish-fat.ps1     # build the all-in-one exe (embeds upstream zip)
│   ├── pack-release.ps1/.sh  # clean release archives with secret scan
│   └── install-shortcuts.ps1  # Desktop + Start Menu shortcuts with icon
├── src/OpencodePortable/   # C# source of the single-file exe
├── docs/HOW-IT-WORKS.md    # architecture details
├── LICENSE                 # MIT
├── README.md               # this file (EN)
└── README.it.md            # Italian version
```
(`bin/` holds `opencode.exe` + `.gitkeep`; `config/` holds the tracked
`opencode.example.json`; `dist/`, `data/`, `src/**/payload|bin|obj` are
git-ignored build/runtime outputs.)

## Desktop shortcuts (Windows)

```powershell
.\scripts\install-shortcuts.ps1            # create Desktop + Start Menu shortcuts
.\scripts\install-shortcuts.ps1 -Uninstall # remove them
```

Shortcuts launch `opencode-portable.cmd` with the custom icon from `assets/opencode-portable.ico`. (Deliberately the script, not the exe: shortcuts are for quick terminal use; the exe is the double-click app.) Working directory is the portable root.

## How it works

| Concern | What the launchers do |
|---|---|
| Isolation | Set `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME`, `XDG_STATE_HOME` to `data/...`, and `OPENCODE_CONFIG` to `config/opencode.json` |
| Fast startup | Set `OPENCODE_DISABLE_MODELS_FETCH=1` (skips the blocking 10–30s `models.dev` fetch) |
| Temp | Isolated per-run tmp (`TMP/TEMP/TMPDIR` point inside it, deleted on exit). Naming differs per launcher: `run-HHMMSS-RAND` (cmd), `run-yyyyMMdd-HHmmss-PID-rand` (ps1), `mktemp run-...-PID-XXX` (sh); the exe needs no sub-level (its root is already per-run) |
| Crash leftovers | Swept next run (exe + ps1/sh: dead PID only, live never touched; cmd: age >7 days, no PID check — documented divergence) |
| Host pollution guard | Snapshot `%TEMP%\opencode`, `%LOCALAPPDATA%\opencode`, `%APPDATA%\opencode` (Win) or `~/.config/opencode`, `~/.local/share/opencode`, `~/.cache/opencode` (Unix) and remove them on exit **only if this run created them** |
| Env hygiene | `.ps1`/`.sh` restore or scope env vars (`try/finally`, `trap`); concurrent instances are safe |

See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) for details.

## Binaries policy

`bin/`, `data/`, `dist/`, `src/**/payload|bin|obj` and local `config/opencode.json` are git-ignored. The repo ships **source only**:

- `scripts/setup.ps1` → `opencode-windows-x64.zip` (or `-arm64`, auto-detected, WOW64-aware) from upstream releases
- `scripts/setup.sh` → `opencode-linux-x64.tar.gz` / `opencode-darwin-arm64.zip`, etc. (fails fast on unsupported OS/arch)
- If `bin/` is empty the launchers **refuse to run** (fail-closed): reusing `opencode` from `PATH` requires explicit opt-in (`--from-path` — stripped anywhere by exe/cmd, first-arg-only by ps1/sh — or `OPENCODE_ALLOW_PATH_FALLBACK=1`), and the resolved path is always printed. The exe prefers a bundled binary and only then looks at PATH.

For distribution: `pack-release.ps1` builds `opencode-portable-win-x64.zip`, `pack-release.sh` builds `opencode-portable-linux-macos.tar.gz`. Both secret-scan the content and never include `data/`, `bin/*`, `dist/` or local configs. The all-in-one exe itself (`dist/`) is never committed (~137 MB).

## Security notes

- **Binaries are trusted upstream artifacts**: `setup.*` downloads and `publish-fat.ps1` embeds over HTTPS from GitHub releases, with no checksum pinning. If upstream ever publishes hashes/signatures, verify them before running.
- **Credentials live in `data/` (scripts) or `<workspace>/.opencode-portable-*/` (exe)** (auth tokens, API keys): never share or publish those folders, and never ship an archive containing them — `pack-release` excludes them by design. A lost USB stick means lost credentials.
- **Downloaded zips carry Mark-of-the-Web**: on other PCs Windows may block the `.ps1` launchers (execution policy). Use `opencode-portable.cmd`, or `Unblock-File`, after inspecting the content.
- This wrapper isolates files, it does **not sandbox** opencode itself: an AI coding agent runs shell commands in your workspace — review what it does, as with any upstream install.
- **Trust model**: the workspace is NOT a security boundary — use only folders you trust (no network shares, synced folders writable by others, or untrusted repos); anyone who can write the workspace can influence config, cache and extracted binaries. Same for `--from-path` / `OPENCODE_ALLOW_PATH_FALLBACK=1`: it bypasses the bundled-payload trust and runs whatever `opencode` is first in PATH — resolved path is always printed, hash it yourself if unsure.
- **Updates**: no auto-update (`autoupdate: false`); each release pins its opencode version at build time (`publish-fat` resolves latest then, embeds + hashes it). To update, download the new release.
- **No Windows Error Reporting**: the exe silences WER for its own crashes (including supervised picker-child AVs) so nothing lands in `ReportArchive`; diagnostics stay in our own `crash-*.log`. `--clean-host` removes our host-side traces (old WER archives, watchdog telemetry leftovers, TEMP crash dir) — pre-existing archives may need admin.

## Exe flags & exit codes

| Flag | Effect |
|---|---|
| `--workspace <dir>` / `--workspace=<dir>` | Skip the picker, start directly there |
| `--console` | Skip the dialog, go straight to drag-and-drop input |
| `--from-path` | Opt in to reusing `opencode` from PATH if no binary found (anywhere pre-`--`; also `OPENCODE_ALLOW_PATH_FALLBACK=1`) |
| `--picker-exe <path>` / `--picker-exe=<path>` | Use another executable as the picker child (diagnostics) |
| `--pick-folder [title] [initial]` | Child mode: native dialog, stdout protocol `PICK-OK` (0) / `PICK-CANCEL` (1) / `PICK-ERROR` (2) |
| `--watch <pid> <ticks> <dir>` | Watchdog mode (spawned automatically, orphan hop) |
| `--clean <dir>` | Sweep stale roots without launching; errors on missing dir |
| `--clean-host` | Remove own host traces (WER archives, watchdog telemetry, TEMP crash dir); always exit 0 |
| `--self-test` | Non-UI COM smoke test (instantiate + options + WER-silenced check, no dialog shown) |
| `--test-fallback`, `--test-close`, `--test-robust-delete`, `--test-watchdog`, `--test-watchdog-proc`, `--test-watchdog-copy`, `--test-detached` | Built-in self-tests (see source) |
| `--report-console <file>` | Write this process' console HWND (detachment probe) |
| `--has-payload` | Report embedded payload info; exit 0 iff present (used by publish-fat) |
| `--` | Everything after is forwarded to opencode verbatim, even launcher-like flags |

Exit codes: `0` = ok / picker cancelled / `--clean-host` done; `1` = invalid workspace, missing binary/config, failed fallback, failed self-test, `--clean` missing dir, `--watch` misuse, watchdog abort, fatal error (MessageBox + `crash-*.log` with line numbers); `2` = picker-child internal error (parent maps it to fallback); otherwise opencode's own exit code is propagated.

## Config

Scripts flow: edit `config/opencode.json` (defaults: `autoupdate: false`, `share: "disabled"` — sensible for a portable install). The template lives in `config/opencode.example.json`; delete your local `opencode.json` to reset to defaults.

Exe flow: the config is **ephemeral per run** (`<root>/config/opencode.json`, from the template next to the exe or the embedded default) and deleted on exit with everything else.

Note: this project targets opencode's built-in free models only — no login, no
external providers to connect. Login/auth state never persists for the exe
(fresh root every run); provider API keys, if you ever need other models, are
typed per session, never stored (see [HOW-IT-WORKS](docs/HOW-IT-WORKS.md) § Auth note).

## License

[MIT](LICENSE) — © 2026 Terzastella.
