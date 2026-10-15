# How it works

Two ways to run opencode portably. The recommended one is the single-file
Windows app; the scripts cover scripted use and Linux/macOS.

## A. OpencodePortable.exe (Windows, recommended)

One file to distribute (`dist/OpencodePortable.exe`, built with
`scripts/publish-fat.ps1`). On a normal run it is fully stateless: **nothing
is ever written next to the exe** (opt-in `--persist-auth` keeps an `auth/`
dir, `--update` stages a `.new.exe` for the post-exit replace). Double-click flow:

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

- `scripts/setup.*` → v1: upstream GitHub zip/tar.gz into `bin/`; v2: npm registry `cli-<os>-<arch>-<version>.tgz` (`@opencode`, fallback `@opencode-ai`), `package/bin/opencode(.exe)` extracted (script flow; honors `GITHUB_TOKEN` only for github.com, fails fast on unsupported OS/arch)
- `scripts/publish-fat.ps1` → uses the pinned version from `UPSTREAM_VERSION` (explicit `-Version/-Arch` win, RID matched), downloads v1 zip or v2 tgz (v2 re-zipped as `opencode.zip` so the runtime stays zip-only), embeds the zip, publishes the all-in-one exe into `dist/` (never committed), verifies via `--has-payload`
- `scripts/pack-release.ps1` → `opencode-portable-win-x64.zip`; `scripts/pack-release.sh` → `opencode-portable-linux-macos.tar.gz`. Both secret-scan and never include `data/`, `bin/*`, `dist/` or local configs.

## Self-updater (portable, explicit, zero-trace)

Normal runs never touch the network (offline-first). Only `--check-update`
and `--update [--yes]` do:

- `GET api.github.com/repos/terzastella/opencode-portable/releases/latest`
  (retry/timeout like the scripts, `GITHUB_TOKEN` header in-process only),
  compare tag against the assembly version (`csproj <Version>`).
- `--update` downloads `OpencodePortable.exe` + `OpencodePortable.exe.sha256`
  from the SAME release into a unique host-TEMP dir, verifies sha256
  fail-closed (+ size floor), stages `OpencodePortable.new.exe` next to the
  current exe, then spawns a detached system powershell helper that moves it
  over after exit (a running Windows exe is locked).
- Temp is deleted via `RobustDelete`; leftovers are reported and swept next
  run / via `--clean-host` (which now covers `opencode-portable-update-*`).
- The helper spawn only proves the mover started, not that the swap landed:
  a leftover `OpencodePortable.new.exe` next to the exe is reported at the
  next launch and the replace is retried on exit (idempotent; manual fallback
  — close all instances, rename by hand — is printed when staging).

## Auth note

Free models currently work with **no login** (verified Oct 2026 against the
1.19.0 payload: fresh root, no `auth.json` anywhere, no key in env). But
upstream gated them server-side before (`FreeTierError`, Sep 2026, hit even
official builds) and may do it again — so a one-time-login backup is built
in, not bolted on:

- `opencode auth login` (browser/OAuth) writes `$XDG_DATA_HOME/opencode/auth.json`
  (verified in the 1.19.0 binary; fallback `~/.opencode/auth.json`, covered by
  the host guard). Scripts persist it automatically (`data/` survives runs).
  The exe wipes it with the root **unless** `--persist-auth` (or
  `OPENCODE_PORTABLE_PERSIST_AUTH=1`): then `<exe-dir>/auth/auth.json` is
  seeded into each fresh root and copied back atomically before deletion
  (normal exit + console-close handler; watchdog/sweeper kills keep the
  last-good copy, re-login only in rare rotation races). `--forget-auth`
  deletes it (explicit logout); `--clean-host` never touches it.
- `OPENCODE_API_KEY` per session (inherited env, never stored, never logged):
  no files at all, works on every flow including stock exe. Best for
  multi-PC without carrying credentials.

Bearer tokens at rest: whoever holds `data/` or `<exe>/auth/` is you on any
PC (roams with the USB stick — the point). BitLocker-encrypt portable
drives; revoke from `opencode.ai/console` if lost. Never share or publish
`data/`, `.opencode-portable-*/` or `<exe>/auth/` — they may hold credentials.
Other providers' keys are typed per session or exported in your shell profile
(e.g. `ANTHROPIC_API_KEY`) — never stored by this project.
