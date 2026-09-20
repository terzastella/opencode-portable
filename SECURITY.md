# Security Policy

## Supported versions

Only the latest release is supported. Older releases receive no security
updates — download the newest `OpencodePortable.exe` from
[Releases](https://github.com/terzastella/opencode-portable/releases).

| Version | Supported |
|---|---|
| latest (`v1.3`+) | ✅ |
| older | ❌ |

## Scope — what this project is (and is not)

- This launcher isolates **files** (config, cache, temp, binary) and cleans them
  up on exit. It is **not a sandbox**: opencode runs with your user permissions
  and can execute shell commands in the workspace.
- Use **trusted workspaces only**: local folders, not shared/synced/untrusted
  locations. Anyone who can write the workspace can influence what runs.
- "Zero traces" means application files and known runtime artifacts are
  removed (see README checklist). It is **not** forensic anti-tracking: OS and
  security telemetry (Prefetch, Defender/SmartScreen, USN Journal, shell
  history, antivirus logs) cannot be removed by any portable app.
- No login or credentials are stored by the launcher itself. Provider API keys
  are typed per session; never share `data/` or `.opencode-portable-*/` folders.
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
| Upstream GitHub releases over HTTPS (no independent anchor — documented) | Binaries found in PATH (fail-closed, explicit opt-in) |
| The user running it (their machine, their choice) | Other local users/processes (single-user oriented) |

## Verifying a download

```powershell
if ((Get-FileHash OpencodePortable.exe -Algorithm SHA256).Hash.ToLower() -ne (Get-Content OpencodePortable.exe.sha256)) { throw 'hash mismatch' }
```

The `.sha256` is published next to the `.exe` in every release. This proves the
download is intact — not authorship (no Authenticode signature yet).
