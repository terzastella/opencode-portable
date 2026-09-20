#!/usr/bin/env pwsh
# Build a clean public zip: scans for secrets, then packs only publishable files.
# Secrets (data/, bin/*, config/opencode.json) are NEVER included.
# Usage: .\scripts\pack-release.ps1 [-Out dist]
# SPDX-License-Identifier: MIT
param([string]$Out = "dist")

$ErrorActionPreference = 'Stop'
$Root = Split-Path (Split-Path $MyInvocation.MyCommand.Definition -Parent) -Parent

$SecretPatterns = @(
  'sk-[A-Za-z0-9]{20,}',
  'sk-ant-[A-Za-z0-9\-_]{20,}',
  'ghp_[A-Za-z0-9]{20,}',
  'gho_[A-Za-z0-9]{20,}',
  'AKIA[0-9A-Z]{16}',
  'xox[bpas]\-[A-Za-z0-9\-]{10,}',
  '-----BEGIN [A-Z ]*PRIVATE KEY-----',
  '(?i)api[_-]?key\s*[:=]\s*[''"][^''"]{8,}'
)
$Include = @(
  'opencode-portable.cmd', 'opencode-portable.ps1', 'opencode-portable.sh',
  'LICENSE', 'README.md', 'README.it.md', 'SECURITY.md', 'CHANGELOG.md',
  'THIRD-PARTY-NOTICES.md', 'UPSTREAM_VERSION', 'global.json',
  'assets', 'bin/.gitkeep', 'config/opencode.example.json',
  'docs', 'scripts', '.github'
)

Write-Host "[pack] validating include list..."
foreach ($rel in $Include) {
  if (-not (Test-Path -LiteralPath (Join-Path $Root $rel))) {
    throw "[pack] ERROR: include missing: $rel (scan would be falsely clean)."
  }
}

Write-Host "[pack] secret scan..."
$files = foreach ($rel in $Include) {
  $p = Join-Path $Root $rel
  if (Test-Path -LiteralPath $p -PathType Container) { Get-ChildItem -LiteralPath $p -Recurse -File }
  elseif (Test-Path -LiteralPath $p) { Get-Item -LiteralPath $p }
}
$hits = $files | Select-String -Pattern $SecretPatterns -ErrorAction SilentlyContinue
if ($hits) {
  Write-Host "[pack] ERROR: possible secrets found:" -ForegroundColor Red
  $hits | ForEach-Object { Write-Host ("  " + $_.Path + ":" + $_.LineNumber) -ForegroundColor Red }
  exit 1
}
Write-Host "[pack] scan clean."

$OutDir = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $Root $Out }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$Zip = Join-Path $OutDir 'opencode-portable-win-x64.zip'
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }

$stage = Join-Path ([IO.Path]::GetTempPath()) ("opencode-pack-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
  foreach ($rel in $Include) {
    $src = Join-Path $Root $rel
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = Join-Path $stage $rel
    if (Test-Path -LiteralPath $src -PathType Container) {
      New-Item -ItemType Directory -Path $dst -Force | Out-Null
      Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
    } else {
      $d = Split-Path $dst -Parent
      if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
      Copy-Item -LiteralPath $src -Destination $dst -Force
    }
  }
  # Never ship a local config even if the exclude list drifts
  Remove-Item -LiteralPath (Join-Path $stage 'config/opencode.json') -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $Zip
  Write-Host "[pack] OK -> $Zip"
} finally {
  Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
