# How it works

Two ways to run opencode portably. The recommended one is the single-file
Windows app; the scripts cover scripted use and Linux/macOS.

## A. OpencodePortable.exe (Windows, recommended)

One file to distribute (`dist/OpencodePortable.exe`, built with
`scripts/publish-fat.ps1`). It is fully stateless: **nothing is ever written
next to the exe**. Double-click flow:

1. **Workspace selection** — a supervised child process shows the native
   folder picker (`--pick-folder` protocol: `PICK-OK:<path>` (0),
   `PICK-CANCEL` (1), `PICK-ERROR:<trace>` (2)). A native crash in the dialog
   kills only the child (exit `0xC0000005`) and the parent falls back to
   drag-and-drop console input (3 attempts, quotes stripped, UTF-8, `\\?\`
   normalized). `--workspace <dir>` skips the picker, `--console` goes straight
   to drag-and-drop, `--picker-exe <path>` overrides the child (diagnostics).
   Cancel exits silently with code 0. Self-test (`--self-test`) never shows
   the dialog. Exit codes: `0` ok/cancel/`--clean-host`/`--has-payload`;
   `1` invalid workspace (incl. drive root), missing binary/config, failed
   fallback/self-test, `--clean` missing dir, `--watch`/`--watch-hop`/`--picker-exe`
   misuse, watchdog abort, fatal (MessageBox + `crash-*.log`); `2` only for a
   direct `--pick-folder` child error (parent auto-falls-back); otherwise
   opencode's own code is propagated.
2. **Portable root** — everything lives inside the selected workspace under
   a per-run `.opencode-portable-<timestamp>-<pid>-<rand>/` folder (config,
   data, cache, state, tmp, `bin/opencode.exe`). Deletion is best-effort with
   budgets (normal exit ~10 s, close handler ~3 s, watchdog ~15 s, sweeper
   retry): leftovers are reported, never silent, and swept next run or via
   `--clean <workspace>`. Concurrent runs get separate folders (no sharing,
   no locks); the sweeper only removes exact-stamp names with dead PIDs —
   live processes and user directories are never touched.
3. **Watchdog** — at startup the app copies itself to `%TEMP%\opwatch-<pid>-<rand>.exe`
   and spawns the copy detached; the copy spawns a `--watch` grandchild and
   exits, leaving an orphan no Task Manager tree-kill can reach from our
   processes. Whatever kills the parent (normal exit, crash, window close,
   Task Manager) wakes it: it kills processes still executing from inside the
   folder, deletes the per-run root, then self-deletes the copy. PID reuse is
   defeated via process start-time matching; host TEMP telemetry
   (`opencode-portable-watchdog-<pid>.log`) is removed on success. Killing the
   copy too (or power loss) falls back to the next-run sweeper and `--clean`.
4. **First run** — the opencode binary embedded at BUILD time is extracted to
   `<stamp-root>/bin/opencode.exe` (fresh every run, ~172 MB — re-extraction
   cost per launch; staged via the workspace tmp, never host TEMP). No network
   at runtime, ever. Without an embedded payload the exe errors out explicitly
   instead of downloading. A dev run (`dotnet run`) falls back to
   `BaseDirectory`-relative `bin/opencode.exe` when no payload is embedded.
5. **Isolated run** — XDG redirect into the per-run root
   (`XDG_*` → `<root>/{config,data,cache,state}`, `OPENCODE_CONFIG` →
   `<root>/config/opencode.json`, `TMP/TEMP/TMPDIR` → `<root>/tmp` directly —
   no extra `run-*` level, the root itself is per-run), `CWD=workspace`, child
   env via `ProcessStartInfo` (no shell pollution), `OPENCODE_DISABLE_MODELS_FETCH=1`
   (skips the blocking 10–30 s `models.dev` fetch), fail-closed PATH (opt-in
   via `--from-path` anywhere pre-`--` or `OPENCODE_ALLOW_PATH_FALLBACK=1`;
   bundled binary preferred, PATH only if nothing found), exit code propagation.

## B. Scripts (opencode-portable.cmd/.ps1/.sh)

Config, data, cache and logs stay inside `./data` next to the scripts.

| Concern | What the launchers do |
|---|---|
| Isolation | Set `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME`, `XDG_STATE_HOME` to `data/...`, and `OPENCODE_CONFIG` to the local `opencode.json` (auto-created from `config/opencode.example.json`) |
| Fast startup | Set `OPENCODE_DISABLE_MODELS_FETCH=1` (skips the blocking 10–30s `models.dev` fetch) |
| Temp | Per-launcher naming differs: `run-HHMMSScc-RAND` (cmd, locale-sanitized), `run-yyyyMMdd-HHmmss-PID-rand` (ps1), `mktemp run-YYYYMMDD-HHMMSS-PID-XXXXXX` (sh). Sweepers: exe/ps1/sh strict (dead PID + exact stamp only, live never touched), cmd age >7 days only (no PID check — documented divergence) |
| Crash leftovers | Swept next run (strict) or via `--clean <workspace>` |
| Host pollution guard | Snapshot `%TEMP%\opencode`, `%LOCALAPPDATA%\opencode`, `%APPDATA%\opencode`, `%USERPROFILE%\.config\opencode`, `%USERPROFILE%\.local\share\opencode` (Win; exe only — mirrors cover TEMP/LOCALAPPDATA/APPDATA) or `~/.config|~/.local/share|~/.cache/opencode` (Unix) and remove them on exit **only if this run created them** |
| Env hygiene | `.ps1`/`.sh` restore or scope env vars (`try/finally`, `trap`); exe passes env to the child only (no shell pollution); concurrent instances are safe |
| Binary | `bin/` or fail-closed error (PATH reuse needs `--from-path` / env opt-in, resolved path always printed) |

## Binaries policy

`bin/`, `data/`, `dist/`, `src/**/payload|bin|obj` and local `config/opencode.json` are git-ignored. The repo ships **source only**:

- `scripts/setup.*` → download the upstream binary into `bin/` (script flow; honors `GITHUB_TOKEN`, fails fast on unsupported OS/arch)
- `scripts/publish-fat.ps1` → uses the pinned version from `UPSTREAM_VERSION` (explicit `-Version/-Arch` win, RID matched), embeds the zip, publishes the all-in-one exe into `dist/` (never committed: ~137 MiB / ~144 MB), verifies via `--has-payload`
- `scripts/pack-release.ps1` → `opencode-portable-win-x64.zip`; `scripts/pack-release.sh` → `opencode-portable-linux-macos.tar.gz`. Both secret-scan and never include `data/`, `bin/*`, `dist/` or local configs.

## Auth note

This project is designed for opencode's **built-in free models: no login, no
external providers to connect**. `OPENCODE_CONFIG` (and therefore any
`opencode auth login` state) is **per run** for the exe (fresh root every
launch, deleted on exit) and persistent per folder for the scripts
(`<root>/config/opencode.json`). If you ever need other models, provider API
keys are typed per session or exported in your shell profile
(e.g. `ANTHROPIC_API_KEY`) — never stored by this project.
Never share or publish `data/` or `.opencode-portable-*/` — they may hold credentials.
