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
$headers = @{}
if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
$PinnedFile = Join-Path $Root 'UPSTREAM_VERSION'
if ($Version -eq "" -or $Version -eq "latest") {
  # Pinned by default: reproducible releases. An explicit -Version always wins.
  $pinned = ""
  if (Test-Path -LiteralPath $PinnedFile) { $pinned = (Get-Content -LiteralPath $PinnedFile -Raw).Trim() }
  if ($pinned -match '^\d+\.\d+\.\d+$') {
    $Version = $pinned
    Step "using pinned upstream v$Version (from UPSTREAM_VERSION; -Version overrides)."
  } else {
    Step "resolving latest release (no valid UPSTREAM_VERSION pin)..."
    $rel = $null
    for ($i = 1; $i -le 3 -and $null -eq $rel; $i++) {
      try { $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers -TimeoutSec 60 }
      catch { if ($i -eq 3) { throw }; Step "API retry $i/3..."; Start-Sleep 5 }
    }
    $Version = ([string]$rel.tag_name).TrimStart('v')
    if ($Version -eq "") { throw "could not resolve latest version." }
  }
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
  # Integrity gate: a truncated zip must NEVER be embedded. Expand to temp and
  # require the real binary entry, with sane bounds (zip-bomb defense).
  # (Expand-Archive instead of raw .NET ZipFile: immune to assembly differences
  # between PowerShell 5.1/7.)
  $zipOk = $false
  $gateTmp = Join-Path ([IO.Path]::GetTempPath()) ("opencode-gate-" + [Guid]::NewGuid().ToString('N'))
  try {
    Expand-Archive -LiteralPath $PayloadZip -DestinationPath $gateTmp -Force
    $files = @(Get-ChildItem -LiteralPath $gateTmp -Recurse -File -ErrorAction Stop)
    $hit = $files | Where-Object { $_.Name -eq 'opencode.exe' } | Select-Object -First 1
    if (-not $hit) { $hit = $files | Where-Object { $_.Name -like 'opencode*.exe' } | Select-Object -First 1 }
    if ($files.Count -gt 50) { Step ("integrity FAIL: too many entries ({0})." -f $files.Count) }
    elseif (-not $hit -or $hit.Length -lt 10MB) { Step "integrity FAIL: no opencode*.exe >10MB inside." }
    else { $zipOk = $true }
  } catch {
    Step ("integrity FAIL: cannot open zip ({0})." -f $_.Exception.Message)
  } finally {
    Remove-Item -LiteralPath $gateTmp -Recurse -Force -ErrorAction SilentlyContinue
  }
  if (-not $zipOk) { throw "payload zip invalid - refusing to embed. Check network and retry." }
  # Pin the hash: the launcher verifies it before every extraction (supply-chain
  # pinning without freezing the version - publish-fat resolves latest at build).
  $PayloadHash = Join-Path $PayloadDir 'opencode.zip.sha256'
  (Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath $PayloadHash -NoNewline

  Step "publishing ($Rid)..."
  & dotnet publish (Join-Path $Root 'src\OpencodePortable\OpencodePortable.csproj') -c Release -r $Rid -o $OutDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

  $exe = Join-Path $OutDir 'OpencodePortable.exe'
  $exeSize = (Get-Item -LiteralPath $exe).Length
  # Airtight check (not a size heuristic): the exe itself reports the payload.
  & $exe --has-payload
  if ($LASTEXITCODE -ne 0) { throw "payload NOT embedded (see --has-payload output above)." }
  # Release hash of the final exe (what users verify after download).
  $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
  $exeHash | Set-Content -LiteralPath ($exe + '.sha256') -NoNewline
  # Minimal CycloneDX SBOM: ingredients list with versions, licenses, hashes.
  $zipHash = (Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash.ToLowerInvariant()
  $appVersion = ([regex]::Match((Get-Content -LiteralPath (Join-Path $Root 'src\OpencodePortable\OpencodePortable.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value).Trim()
  if ($appVersion -eq "") { $appVersion = "0.0.0-unknown" }
  $sdkLine = (& dotnet --list-runtimes 2>$null | Where-Object { $_ -like 'Microsoft.NETCore.App 10.*' } | Select-Object -First 1)
  $runtimeVer = if ($sdkLine -match '(\d+\.\d+\.\d+)') { $Matches[1] } else { "10.x" }
  $sbom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    version = 1
    metadata = [ordered]@{
      timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
      supplier = [ordered]@{ name = "Terzastella"; url = @("https://github.com/terzastella/opencode-portable") }
      component = [ordered]@{ type = "application"; name = "OpencodePortable"; version = $appVersion }
    }
    components = @(
      [ordered]@{
        type = "application"; name = "opencode (upstream embedded payload)"; version = $Version
        hashes = @([ordered]@{ alg = "SHA-256"; content = $zipHash })
        licenses = @([ordered]@{ license = [ordered]@{ id = "MIT" } })
        externalReferences = @([ordered]@{ type = "distribution"; url = $Url })
      },
      [ordered]@{
        type = "framework"; name = "Microsoft.NETCore.App (self-contained runtime)"; version = $runtimeVer
        licenses = @([ordered]@{ license = [ordered]@{ id = "MIT" } })
      }
    )
  }
  $sbomPath = Join-Path $OutDir 'OpencodePortable.exe.cdx.json'
  ($sbom | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $sbomPath
  Step ("OK -> {0} ({1:N1} MB, payload v{2} embedded)" -f $exe, ($exeSize / 1MB), $Version)
  Step ("artifacts: exe + .sha256 + .cdx.json in {0}" -f $OutDir)
} finally {
  # Remove only what we downloaded (.zip + .sha256); never wipe a pre-existing payload dir.
  foreach ($f in @($PayloadZip, (Join-Path $PayloadDir 'opencode.zip.sha256'))) {
    if (Test-Path -LiteralPath $f) {
      Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }
  }
  Step "staging pulita."
}
