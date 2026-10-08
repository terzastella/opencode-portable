#!/usr/bin/env pwsh
# Download the matching upstream opencode binary into .\bin\
# Usage: .\scripts\setup.ps1 [-Version 1.18.35]
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
$FileName = "opencode-windows-$Arch.zip"
if ($Version -ne "") {
  # Explicit versions are validated like the pin: an untrusted VERSION must
  # never reach URL interpolation or log lines raw (path probing / CRLF log
  # injection). Only strict x.y.z or 'latest' are accepted.
  $Version = $Version.Trim().TrimStart('v')
  if ($Version -eq "" -or $Version -eq "latest") {
    $Url = "https://github.com/$Repo/releases/latest/download/$FileName"
    Write-Host "[setup] downloading latest: $FileName"
  } elseif ($Version -match '^\d+\.\d+\.\d+$') {
    $Url = "https://github.com/$Repo/releases/download/v$Version/$FileName"
    Write-Host "[setup] downloading v$Version : $FileName"
  } else {
    throw "invalid version '$Version' (expected x.y.z or 'latest')."
  }
} else {
  # Pinned by default (reproducible): explicit -Version always wins.
  $PinnedFile = Join-Path $Root 'UPSTREAM_VERSION'
  $pinned = ""
  if (Test-Path -LiteralPath $PinnedFile) { $pinned = (Get-Content -LiteralPath $PinnedFile -Raw).Trim() }
  if ($pinned -match '^\d+\.\d+\.\d+$') {
    $Version = $pinned
    $Url = "https://github.com/$Repo/releases/download/v$Version/$FileName"
    Write-Host "[setup] downloading pinned v$Version (UPSTREAM_VERSION) : $FileName"
  } else {
    $Url = "https://github.com/$Repo/releases/latest/download/$FileName"
    Write-Host "[setup] downloading latest: $FileName"
  }
}

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("opencode-setup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Tmp | Out-Null
try {
  $Zip = Join-Path $Tmp $FileName
  $headers = @{}
  # Authenticated rate limits for CI (60 req/h anonymous): honor GITHUB_TOKEN.
  # Header-only (never argv): the token stays in-process, invisible to `ps`.
  if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
  # Retry + timeout like publish-fat (5.1-compatible: -UseBasicParsing avoids
  # the IE parser hang on Windows PowerShell 5.1).
  $downloaded = $false
  for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
    try {
      Invoke-WebRequest -Uri $Url -OutFile $Zip -Headers $headers -TimeoutSec 600 -UseBasicParsing
      $downloaded = Test-Path -LiteralPath $Zip
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
  $zipHash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($pin) {
    $expected = ([regex]::Match($pin, '([0-9a-f]{64})').Groups[1].Value).ToLowerInvariant()
    if ($zipHash -ne $expected) { throw "downloaded zip hash mismatch (possible tampering), refusing install." }
    Write-Host "[setup] hash pin verified."
  } else {
    Write-Host "[setup] ATTENZIONE: no recorded hash for windows-$Arch v$Version, TLS-only trust." -ForegroundColor Yellow
  }
  Expand-Archive -LiteralPath $Zip -DestinationPath $Tmp -Force
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
