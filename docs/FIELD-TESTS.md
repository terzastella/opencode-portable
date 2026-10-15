# Field tests

Manual runs no CI can cover (fresh Windows VMs, real AV, real kills).
Build under test: _______________ (e.g. `v1.4.0` release exe).

How to fill: one row per run — date, host (Win10/11 + VM or hardware),
result (`PASS` / `FAIL` + what was left behind), notes. A `FAIL` means
leftovers outside the documented scope or a crash without diagnostics.

## Cleanup checklist (run after every scenario)

```cmd
:: 1. workspace clean (shows hidden/system too):
dir "C:\path\to\workspace" /a
:: 2. known leftovers on C: (the exe itself is expected):
dir C:\*opencode* /s /b /a
dir C:\opwatch-*.exe /s /b /a
:: 3. host TEMP after failures:
dir %TEMP%\opencode-portable-crash-* /a
dir %TEMP%\opencode-portable-watchdog-*.log /a
dir %TEMP%\opencode-portable-update-* /a
:: 4. no lingering processes (tasklist takes no wildcards):
tasklist | findstr /I "opencode opwatch"
```

Expected: only your files, only the exe, no processes.

## Matrix

| # | Scenario | Steps | Expected | Result | Notes |
|---|----------|-------|----------|--------|-------|
| 1 | First run, clean Win11 VM | Double-click, pick folder, `say hi` with a free model, quitvia `exit` | Works; cleanup checklist clean |  |  |
| 2 | First run, clean Win10 VM | Same as 1 on Windows 10 | Works; checklist clean |  |  |
| 3 | Workspace with accents/spaces | Folder `C:\test\Caffè lavoro\dati importanti` | Works; root created/deleted, no mojibake |  |  |
| 4 | Very long workspace path | ~200-char nested path | Works or clear error, no leftovers |  |  |
| 5 | USB stick (exFAT) | Exe + workspace on removable drive, run, eject after quit | Works; checklist clean on host and stick (minus your files) |  |  |
| 6 | Defender real-time on | Fresh exe, first run (SmartScreen prompt expected) | Prompt shown; after allow, works; checklist clean |  |  |
| 7 | Close via X / Alt+F4 mid-session | Start a task, close the window | Root renamed-to-trash then deleted (or swept next run); checklist clean after next run |  |  |
| 8 | Task Manager kill (process) | Kill `OpencodePortable.exe` | Watchdog deletes root; checklist clean |  |  |
| 9 | Task Manager kill (tree) | Kill process tree | Orphan watchdog still deletes root; checklist clean |  |  |
| 10 | Ctrl+C | Interrupt mid-session | Clean exit, checklist clean |  |  |
| 11 | Locked files | Keep the extracted `bin\opencode.exe` output open (e.g. `tail -f` on log) and quit | Best-effort message, leftovers reported (never silent), swept next run |  |  |
| 12 | Two concurrent runs | Same workspace, two exes, quit both | Separate roots, no interference, both cleaned |  |  |
| 13 | `--persist-auth` roundtrip | Login once with `--persist-auth`, quit, reopen with flag, free works, `--forget-auth` | No re-login; forget wipes `<exe>\auth\` |  |  |
| 14 | `--check-update` / `--update` | Check (offline stays offline on normal runs); `--update` on an old build | Check reports; update stages `.new.exe` + replace on restart, hash-verified |  |  |
| 15 | `--clean` after kill | Kill -9 style, then `--clean <workspace>` + `--clean-host` | Stale roots/host traces gone, exit 0 |  |  |
| 16 | Fast startup | Time from double-click to prompt (first run vs second run) | No 10–30 s `models.dev` stall (`OPENCODE_DISABLE_MODELS_FETCH=1`) |  | Record seconds |
| 17 | Free model end-to-end | Real message to a `*-free` model | Answer arrives, no `FreeTierError` |  | If 403: note date — upstream gates server-side |

## Out of scope (do not test live)

Power loss mid-run (next-run sweeper covers it by design), shared/synced
writable-by-others workspaces (documented as untrusted — use local folders).
