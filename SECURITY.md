# Security Policy

## Supported versions

Only the latest release is supported. Older releases receive no security
updates — download the newest `OpencodePortable.exe` from
[Releases](https://github.com/terzastella/opencode-portable/releases).

| Version | Supported |
|---|---|
| latest (`v1.4.0`) | ✅ |
| older | ❌ |

## Scope — what this project is (and is not)

- This launcher isolates **files** (config, cache, temp, binary) and cleans them
  up on exit. It is **not a sandbox**: opencode runs with your user permissions
  and can execute shell commands in the workspace.
- Use **trusted workspaces only**: local folders, not shared/synced/untrusted
  locations. Anyone who can write the workspace can influence what runs.
- The per-run root **inherits the workspace ACLs**: on a shared drive or a
  synced folder, other people may read live credentials. Never use
  shared/synced locations for sessions with secrets; prefer a local,
  single-user folder.
- `--clean-host` removes our WER/watchdog/crash/update leftovers but may need admin
  for `%ProgramData%` archives; a standard user can only clean their own
  profile. Exit code stays 0 with a skipped count — read the output.
  It never touches the opt-in `<exe>/auth/` dir: credentials are never
  deleted silently, use `--forget-auth` for explicit logout.
- Self-updater: explicit `--check-update` / `--update` only; normal runs
  never use the network. Updates check our own portable releases over HTTPS,
  verify the release `.sha256` fail-closed (missing/mismatch aborts), never
  send tokens in argv, stage `.new.exe` for a detached replace (the running
  exe is locked on Windows). Review the output before restarting.
- Persistent auth (`--persist-auth`) is bearer tokens at rest next to the exe:
  BitLocker-encrypt portable drives; a lost stick means lost credentials until
  you revoke the key from `opencode.ai/console`. `--forget-auth` wipes it.
- Lost or stolen media means lost credentials: BitLocker-encrypt portable
  drives and dismount them; EFS is not portable across machines.
- "Zero traces" means application files and known runtime artifacts are
  removed (see README checklist). It is **not** forensic anti-tracking: OS and
  security telemetry (Prefetch, Defender/SmartScreen, USN Journal, shell
  history, antivirus logs) cannot be removed by any portable app.
- No login or credentials are stored by the launcher itself, except the
  explicit opt-in `<exe>/auth/` (`--persist-auth`) and the scripts' `data/`
  (which persists `opencode auth login` state by design). Provider API keys
  stay per-session via env (e.g. `OPENCODE_API_KEY`); never share `data/`,
  `.opencode-portable-*/` or `<exe>/auth/` folders. Lost media = revoke the
  key from `opencode.ai/console` and rotate.
- External providers (NVIDIA, Anthropic, OpenAI, …) are **untested** with this
  project. No warranty on provider setups.

## Reporting a vulnerability

**Do not open a public issue** for security problems. Instead:

1. Go to the repository → **Security** tab → **Report a vulnerability**
   (private security advisory, visible only to maintainers), and describe:
   - affected version / commit
   - steps to reproduce
   - what an attacker could achieve (what data, whose machine)
   - whether it needs local access, a malicious workspace, or network
2. Expect an acknowledgement (best effort — this is a personal project, no SLA,
   no bug bounty). Critical issues affecting the current release (arbitrary
   deletion outside the workspace, silent execution of untrusted binaries) are
   prioritised: fix + new release + CHANGELOG entry.
3. Out of scope (will be closed as such): upstream opencode vulnerabilities
   (report to upstream), SmartScreen warnings on the unsigned build, missing
   Authenticode signature, social-engineering scenarios, physical access.

## Trust model summary

| Trusts | Does NOT trust |
|---|---|
| Microsoft Windows 10/11 OS itself | Workspace contents (treat as untrusted input) |
| Upstream GitHub releases over HTTPS + committed hash pin (`UPSTREAM_VERSION.sha256`, setup.* fail closed; v1 upstream publishes no checksums of its own; v2 npm tgz adds registry `integrity` plus our pin) | Binaries found in PATH (fail-closed, explicit opt-in) |
| Portable releases over HTTPS + release `.sha256` for self-update (`--update` only; normal runs offline) | Silent auto-update (there is none: explicit flags only) |
| The user running it (their machine, their choice) | Other local users/processes (single-user oriented) |

## Verifying a download

```powershell
if ((Get-FileHash OpencodePortable.exe -Algorithm SHA256).Hash.ToLower() -ne (Get-Content OpencodePortable.exe.sha256)) { throw 'hash mismatch' }
```

The `.sha256` is published next to the `.exe` in every release. This proves the
download is intact — not authorship (no Authenticode signature yet).
