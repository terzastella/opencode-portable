#!/usr/bin/env pwsh
# Download the matching upstream opencode binary into .\bin\
# Usage: .\scripts\setup.ps1 [-Version 1.19.0]
# SPDX-License-Identifier: MIT
param([string]$Version = "")

$ErrorActionPreference = 'Stop'
$Root = Split-Path (Split-Path $MyInvocation.MyCommand.Definition -Parent) -Parent
$BinDir = Join-Path $Root 'bin'
if (-not (Test-Path -LiteralPath $BinDir)) { New-Item -ItemType Directory -Path $BinDir | Out-Null }

# Architecture mapping (WOW64-aware): 32-bit shells on 64-bit Windows report x86
# in PROCESSOR_ARCHITECTURE, the real arch is in PROCESSOR_ARCHITEW6432.
$ProcArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$Arch = switch -Wildcard ($ProcArch) {
  '*ARM64*' { 'arm64' }
  'AMD64'   { 'x64' }
  default   { throw "unsupported architecture: $ProcArch (upstream ships windows x64/arm64 only)." }
}

$Repo = 'anomalyco/opencode'
# v1 (1.x): GitHub zip. v2 (2.x): npm tgz (no GitHub zip assets since v2).
# Major is known after version resolution below; default to v1 layout until then.
$FileName = "opencode-windows-$Arch.zip"
$IsV2 = $false
if ($Version -ne "") {
  # Explicit versions are validated like the pin: an untrusted VERSION must
  # never reach URL interpolation or log lines raw (path probing / CRLF log
  # injection). Only strict x.y.z or 'latest' are accepted.
  $Version = $Version.Trim().TrimStart('v')
  if ($Version -eq "" -or $Version -eq "latest") {
    # 'latest' now means npm latest (v2). Resolve via metadata endpoint.
    try {
      $meta = Invoke-RestMethod -Uri "https://opencode.ai/update/api/latest/cli/npm" -TimeoutSec 30
      $resolved = ([string]$meta.version).TrimStart('v')
      if ($resolved -match '^\d+\.\d+\.\d+$') { $Version = $resolved }
      else { throw "bad metadata version" }
    } catch {
      throw "could not resolve latest version (npm metadata unreachable)."
    }
    Write-Host "[setup] resolved latest: v$Version"
  } elseif ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "invalid version '$Version' (expected x.y.z or 'latest')."
  }
} else {
  # Pinned by default (reproducible): explicit -Version always wins.
  $PinnedFile = Join-Path $Root 'UPSTREAM_VERSION'
  $pinned = ""
  if (Test-Path -LiteralPath $PinnedFile) { $pinned = (Get-Content -LiteralPath $PinnedFile -Raw).Trim() }
  if ($pinned -match '^\d+\.\d+\.\d+$') {
    $Version = $pinned
    Write-Host "[setup] using pinned v$Version (UPSTREAM_VERSION)"
  } else {
    try {
      $meta = Invoke-RestMethod -Uri "https://opencode.ai/update/api/latest/cli/npm" -TimeoutSec 30
      $resolved = ([string]$meta.version).TrimStart('v')
      if ($resolved -match '^\d+\.\d+\.\d+$') { $Version = $resolved }
      else { throw "bad metadata version" }
    } catch {
      throw "no valid UPSTREAM_VERSION pin and latest unreachable."
    }
    Write-Host "[setup] using latest v$Version (no valid pin)"
  }
}

$IsV2 = $Version -match '^2\.'
if ($IsV2) {
  $FileName = "cli-windows-$Arch-$Version.tgz"
  $NpmPkg = "@opencode/cli-windows-$Arch"
  $Url = "https://registry.npmjs.org/$NpmPkg/-/cli-windows-$Arch-$Version.tgz"
  Write-Host "[setup] downloading v$Version (npm $NpmPkg) : $FileName"
} else {
  $FileName = "opencode-windows-$Arch.zip"
  $Url = "https://github.com/$Repo/releases/download/v$Version/$FileName"
  Write-Host "[setup] downloading v$Version : $FileName"
}

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("opencode-setup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Tmp | Out-Null
try {
  $Archive = Join-Path $Tmp $FileName
  $headers = @{}
  # Authenticated rate limits for CI (60 req/h anonymous): honor GITHUB_TOKEN,
  # but ONLY for github.com — never send it to third parties (npm registry).
  $sendToken = ($Url -like "*github.com*") -and ($env:GITHUB_TOKEN)
  if ($sendToken) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
  # Retry + timeout like publish-fat (5.1-compatible: -UseBasicParsing avoids
  # the IE parser hang on Windows PowerShell 5.1).
  $downloaded = $false
  for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
    try {
      # npm registry needs no auth; GitHub benefits from GITHUB_TOKEN rate limits.
      # Fallback for renamed scope: @opencode (current) -> @opencode-ai (legacy beta).
      try {
        Invoke-WebRequest -Uri $Url -OutFile $Archive -Headers $headers -TimeoutSec 600 -UseBasicParsing
        $downloaded = Test-Path -LiteralPath $Archive
      } catch {
        if ($IsV2 -and $Url -like "*@opencode/cli-windows*") {
          $altUrl = $Url -replace "@opencode/cli-windows", "@opencode-ai/cli-windows"
          Write-Host "[setup] retry with legacy scope @opencode-ai..."
          Invoke-WebRequest -Uri $altUrl -OutFile $Archive -Headers $headers -TimeoutSec 600 -UseBasicParsing
          $downloaded = Test-Path -LiteralPath $Archive
          if ($downloaded) { $Url = $altUrl }
        } else { throw }
      }
    } catch { if ($i -eq 3) { throw }; Write-Host "[setup] download retry $i/3..."; Start-Sleep 5 }
  }
  if (-not $downloaded) { throw "download failed after retries." }
  # Hash pin: fail closed when a hash was recorded for this version+arch
  # (see UPSTREAM_VERSION.sha256, maintained by publish-fat); warn on TLS-only
  # trust when no pin exists (e.g. upstream re-cut the asset).
  $PinFile = Join-Path $Root 'UPSTREAM_VERSION.sha256'
  $pin = ""
  if (Test-Path -LiteralPath $PinFile) {
    $pin = @(Get-Content -LiteralPath $PinFile | Where-Object { $_ -match "^windows-$Arch\s+$Version\s+([0-9a-f]{64})\s*$" } | Select-Object -First 1)
  }
  $arcHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($pin) {
    $expected = ([regex]::Match($pin, '([0-9a-f]{64})').Groups[1].Value).ToLowerInvariant()
    if ($arcHash -ne $expected) { throw "downloaded archive hash mismatch (possible tampering), refusing install." }
    Write-Host "[setup] hash pin verified."
  } else {
    Write-Host "[setup] ATTENZIONE: no recorded hash for windows-$Arch v$Version, TLS-only trust." -ForegroundColor Yellow
  }
  if ($IsV2) {
    # npm tgz: package/bin/opencode.exe. Use system tar (Win10+).
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) { throw "tar.exe required for v2 tgz but not found." }
    & tar.exe -xzf $Archive -C $Tmp
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed." }
  } else {
    Expand-Archive -LiteralPath $Archive -DestinationPath $Tmp -Force
  }
  # Prefer the exact binary; fall back to first opencode*.exe match.
  $Exe = Get-ChildItem -LiteralPath $Tmp -Filter 'opencode.exe' -Recurse | Select-Object -First 1
  if (-not $Exe) { $Exe = Get-ChildItem -LiteralPath $Tmp -Filter 'opencode*.exe' -Recurse | Select-Object -First 1 }
  if (-not $Exe) { throw "no opencode.exe found in archive" }
  if ($Exe.Length -lt 10MB) { throw "extracted binary suspiciously small, refusing install." }
  $Final = Join-Path $BinDir 'opencode.exe'
  # Atomic install: write aside, then move over the final name (same volume),
  # so an interrupted run never leaves a partial binary behind.
  $Staging = Join-Path $BinDir 'opencode.exe.new'
  Copy-Item -LiteralPath $Exe.FullName -Destination $Staging -Force
  Move-Item -LiteralPath $Staging -Destination $Final -Force
  Write-Host "[setup] OK -> $Final"
  # A corrupt binary must fail the install, not exit 0 behind it.
  & $Final --version
  if ($LASTEXITCODE -ne 0) { throw "installed binary failed --version smoke test." }
} finally {
  Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}
