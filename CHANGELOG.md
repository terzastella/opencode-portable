# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) (essentials only).
Upstream opencode stays at `1.18.31` (see `UPSTREAM_VERSION`) unless noted.

## [Unreleased]

## [v1.3] — current

- Security test hooks: `--test-junction` (link removed, target intact),
  `--test-midrun-delete` (graceful exit when the root vanishes mid-run).
- Watchdog hardening: strict per-run name validation, verified parent identity,
  kill-by-image before delete, orphan double-hop, self-deleting renamed copy.
- Strict sweeper everywhere: exact-stamp names + dead PIDs only — live sessions
  and user directories are never touched.
- Payload SHA-256 embedded at build and re-verified before every extraction;
  atomic setup installs; absolute system shell for self-delete.
- CI: full-payload `fat` job on tags; `UPSTREAM_VERSION` pin file.

## [v1.2]

- Hero READMEs (EN+IT) with collapsible sections, language toggle buttons,
  zero-trace verification checklist.
- CI Windows build workflow (build + self-tests + secret scan + hygiene checks).
- Forensic-scope disclaimer: application traces removed, OS/security telemetry
  out of scope.
- `publish-fat.ps1`: arch-matched RID, payload integrity gate, `--has-payload`
  assert, staging cleanup.

## [v1.1] — first public release

- Single-file Windows app (`OpencodePortable.exe`, embedded opencode, offline).
- Per-run workspace root, always deleted (quit, X/Alt+F4, Ctrl+C, Task Manager).
- Supervised child picker with drag-and-drop fallback; fail-closed PATH;
  strict sweeper; orphan watchdog; WER silencing.
- Companion `.cmd`/`.ps1`/`.sh` launchers (Linux/macOS via scripts).
- Bilingual docs, MIT license.
