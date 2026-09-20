# Third-party notices

Components bundled or used by Opencode Portable, with origin, version and
license. This project's own code is MIT (see `LICENSE`).

| Component | Version | Origin | License | Included in |
|---|---|---|---|---|
| opencode (upstream binary, embedded payload) | `1.18.31` (see `UPSTREAM_VERSION`) | https://github.com/anomalyco/opencode | MIT (upstream repo) | `dist/OpencodePortable.exe` |
| .NET SDK / runtime (self-contained publish) | `10.0.400` (see `global.json`) | https://github.com/dotnet/runtime | MIT | `dist/OpencodePortable.exe` |
| actions/checkout (CI) | `v7` (pinned to commit SHA, see workflow) | https://github.com/actions/checkout | MIT | CI only, not shipped |
| actions/setup-dotnet (CI) | `v6` (pinned to commit SHA, see workflow) | https://github.com/actions/setup-dotnet | MIT | CI only, not shipped |

Notes:

- The embedded opencode payload is downloaded at build time from upstream
  GitHub releases, hash-pinned (SHA-256 stored alongside) and re-verified
  before every extraction. Upstream publishes no independent checksums or
  signatures; see `SECURITY.md` and `docs/HOW-IT-WORKS.md` for the trust model.
- The app logo in `assets/` is original work for this project (MIT, same as
  the repo).
- If a component above changes version or license, update this file in the
  same commit.
