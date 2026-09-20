#!/usr/bin/env pwsh
# Build the all-in-one exe: downloads the upstream opencode zip at BUILD time,
# embeds it as a resource, publishes the single file. No network at runtime, ever.
# Usage: .\scripts\publish-fat.ps1 [-Version latest] [-Arch x64] [-Out dist]
# SPDX-License-Identifier: MIT
param(
  [string]$Version = "latest",
  [ValidateSet('', 'x64', 'arm64')][string]$Arch = "",
  [string]$Out = "dist"
)

$ErrorActionPreference = 'Stop'
# The progress bar famously slows/hangs big Invoke-WebRequest downloads.
$ProgressPreference = 'SilentlyContinue'
function Step([string]$m) { Write-Host ("[{0:HH:mm:ss}] [publish-fat] {1}" -f (Get-Date), $m) }
$Root = Split-Path (Split-Path $MyInvocation.MyCommand.Definition -Parent) -Parent
$PayloadDir = Join-Path $Root 'src\OpencodePortable\payload'
$PayloadZip = Join-Path $PayloadDir 'opencode.zip'
# -Out accepts absolute paths too (bare Join-Path would corrupt "C:\...").
$OutDir = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $Root $Out }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

if ($Arch -eq "") {
  $pa = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
  $Arch = switch -Wildcard ($pa) {
    '*ARM64*' { 'arm64' }
    'AMD64'   { 'x64' }
    default   { throw "unsupported architecture: $pa (upstream ships windows x64/arm64 only)." }
  }
}
# The exe is built for THIS machine's arch only: matching RID avoids shipping
# an x64 exe with an arm64 payload (or vice versa).
$Rid = "win-$Arch"

$Repo = 'anomalyco/opencode'
if ($Version -eq "" -or $Version -eq "latest") {
  Step "resolving latest release..."
  $headers = @{}
  if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
  $rel = $null
  for ($i = 1; $i -le 3 -and $null -eq $rel; $i++) {
    try { $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers -TimeoutSec 60 }
    catch { if ($i -eq 3) { throw }; Step "API retry $i/3..."; Start-Sleep 5 }
  }
  $Version = ([string]$rel.tag_name).TrimStart('v')
  if ($Version -eq "") { throw "could not resolve latest version." }
} else {
  $Version = $Version.TrimStart('v')
}
$FileName = "opencode-windows-$Arch.zip"
$Url = "https://github.com/$Repo/releases/download/v$Version/$FileName"
Step "opencode v$Version ($Arch)"

if (-not (Test-Path -LiteralPath $PayloadDir)) { New-Item -ItemType Directory -Path $PayloadDir | Out-Null }
try {
  # Remove any stale partial from an aborted run: never embed a corrupt zip.
  if (Test-Path -LiteralPath $PayloadZip) { Remove-Item -LiteralPath $PayloadZip -Force }
  Step "downloading $FileName..."
  $downloaded = $false
  if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    # curl: fast, retry built-in, no PowerShell progress pathology.
    $curlArgs = @('-fSL', '--retry', '3', '--retry-all-errors', '--connect-timeout', '30', '-o', $PayloadZip, $Url)
    if ($env:GITHUB_TOKEN) { $curlArgs = @('-H', "Authorization: Bearer $($env:GITHUB_TOKEN)") + $curlArgs }
    for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
      & curl.exe @curlArgs
      if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $PayloadZip)) { $downloaded = $true }
      else { Step "download retry $i/3..."; Start-Sleep 5 }
    }
  }
  if (-not $downloaded) {
    for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
      try {
        Invoke-WebRequest -Uri $Url -OutFile $PayloadZip -TimeoutSec 600 -Headers $headers
        $downloaded = Test-Path -LiteralPath $PayloadZip
      } catch { if ($i -eq 3) { throw }; Step "download retry $i/3..."; Start-Sleep 5 }
    }
  }
  if (-not $downloaded) { throw "download failed after retries." }
  $zipSize = (Get-Item -LiteralPath $PayloadZip).Length
  Step ("payload: {0:N1} MB, verifying integrity..." -f ($zipSize / 1MB))
  # Integrity gate: a truncated zip must NEVER be embedded. Open it and require
  # the real binary entry.
  Add-Type -AssemblyName System.IO.Compression
  $zipOk = $false
  try {
    $zr = [System.IO.Compression.ZipFile]::OpenRead($PayloadZip)
    try {
      $hit = $zr.Entries | Where-Object { $_.Name -like 'opencode*.exe' } | Select-Object -First 1
      if ($hit -and $hit.Length -gt 10MB) { $zipOk = $true }
      else { Step "integrity FAIL: no opencode*.exe >10MB inside." }
    } finally { $zr.Dispose() }
  } catch {
    Step ("integrity FAIL: cannot open zip ({0})." -f $_.Exception.Message)
  }
  if (-not $zipOk) { throw "payload zip invalid — refusing to embed. Check network and retry." }

  Step "publishing ($Rid)..."
  & dotnet publish (Join-Path $Root 'src\OpencodePortable\OpencodePortable.csproj') -c Release -r $Rid -o $OutDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

  $exe = Join-Path $OutDir 'OpencodePortable.exe'
  $exeSize = (Get-Item -LiteralPath $exe).Length
  # Airtight check (not a size heuristic): the exe itself reports the payload.
  & $exe --has-payload
  if ($LASTEXITCODE -ne 0) { throw "payload NOT embedded (see --has-payload output above)." }
  Step ("OK -> {0} ({1:N1} MB, payload v{2} embedded)" -f $exe, ($exeSize / 1MB), $Version)
} finally {
  # Remove only what we downloaded; never wipe a pre-existing payload dir.
  if (Test-Path -LiteralPath $PayloadZip) {
    Remove-Item -LiteralPath $PayloadZip -Force -ErrorAction SilentlyContinue
    Step "staging pulita."
  }
}
