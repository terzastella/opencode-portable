#!/usr/bin/env pwsh
# Download the matching upstream opencode binary into .\bin\
# Usage: .\scripts\setup.ps1 [-Version 1.18.31]
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
  $Version = $Version.TrimStart('v')
  $Url = "https://github.com/$Repo/releases/download/v$Version/$FileName"
  Write-Host "[setup] downloading v$Version : $FileName"
} else {
  $Url = "https://github.com/$Repo/releases/latest/download/$FileName"
  Write-Host "[setup] downloading latest: $FileName"
}

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("opencode-setup-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Tmp | Out-Null
try {
  $Zip = Join-Path $Tmp $FileName
  $headers = @{}
  # Authenticated rate limits for CI (60 req/h anonymous): honor GITHUB_TOKEN.
  if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
  Invoke-WebRequest -Uri $Url -OutFile $Zip -Headers $headers
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
  & $Final --version
} finally {
  Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue
}
