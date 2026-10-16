#!/usr/bin/env pwsh
# Build the all-in-one exe: downloads the upstream opencode zip at BUILD time,
# embeds it as a resource, publishes the single file. No network at runtime, ever.
# Usage: .\scripts\publish-fat.ps1 [-Version <x.y.z|latest>] [-Arch x64] [-Out dist] [-AllowPinRotation]
# Empty -Version uses the pinned UPSTREAM_VERSION (reproducible); `latest`
# truly resolves the newest upstream; explicit x.y.z uses exactly that.
# SPDX-License-Identifier: MIT
param(
  [string]$Version = "",
  [ValidateSet('', 'x64', 'arm64')][string]$Arch = "",
  [string]$Out = "dist",
  [switch]$AllowPinRotation
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
if ($Version -eq "") {
  # Pinned by default: reproducible releases.
  $pinned = ""
  if (Test-Path -LiteralPath $PinnedFile) { $pinned = (Get-Content -LiteralPath $PinnedFile -Raw).Trim() }
  if ($pinned -match '^\d+\.\d+\.\d+$') {
    $Version = $pinned
    Step "using pinned upstream v$Version (from UPSTREAM_VERSION)."
  } else {
    throw "no valid UPSTREAM_VERSION pin: pass -Version <x.y.z|latest> explicitly."
  }
} elseif ($Version -eq "latest") {
  Step "resolving latest cli (npm metadata)..."
  $meta = $null
  for ($i = 1; $i -le 3 -and $null -eq $meta; $i++) {
    try { $meta = Invoke-RestMethod -Uri "https://opencode.ai/update/api/latest/cli/npm" -TimeoutSec 30 }
    catch { if ($i -eq 3) { throw }; Step "API retry $i/3..."; Start-Sleep 5 }
  }
  $Version = ([string]$meta.version).TrimStart('v')
  if ($Version -eq "" -or $Version -notmatch '^\d+\.\d+\.\d+$') { throw "could not resolve latest version." }
} else {
  $Version = $Version.TrimStart('v')
  if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "invalid version '$Version' (expected x.y.z or 'latest')." }
}
$IsV2 = $Version -match '^2\.'
if ($IsV2) {
  $NpmPkg = "@opencode/cli-windows-$Arch"
  $FileName = "cli-windows-$Arch-$Version.tgz"
  $Url = "https://registry.npmjs.org/$NpmPkg/-/cli-windows-$Arch-$Version.tgz"
} else {
  $FileName = "opencode-windows-$Arch.zip"
  $Url = "https://github.com/$Repo/releases/download/v$Version/$FileName"
}
Step "opencode v$Version ($Arch)"

if (-not (Test-Path -LiteralPath $PayloadDir)) { New-Item -ItemType Directory -Path $PayloadDir | Out-Null }
$DownloadTmp = Join-Path ([IO.Path]::GetTempPath()) ("opencode-payload-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $DownloadTmp | Out-Null
$TgzHashRecord = ""
try {
  # Remove any stale partial from an aborted run: never embed a corrupt zip.
  if (Test-Path -LiteralPath $PayloadZip) { Remove-Item -LiteralPath $PayloadZip -Force }
  $downloadTarget = $PayloadZip
  $sendToken = ($Url -like "*github.com*") -and ($env:GITHUB_TOKEN)
  if ($IsV2) {
    # v2: download npm tgz aside, then re-zip the exe as payload (runtime stays zip).
    $downloadTarget = Join-Path $DownloadTmp $FileName
    Step "downloading $FileName (npm)..."
  } else {
    Step "downloading $FileName..."
  }
  $downloaded = $false
  if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
    # curl: fast, retry built-in, no PowerShell progress pathology.
    # The token travels via --config on stdin (never argv): invisible to `ps`.
    # Never send GITHUB_TOKEN to npmjs (third party): only github.com.
    $curlBase = @('-fSL', '--retry', '3', '--retry-all-errors', '--connect-timeout', '30', '-o', $downloadTarget, $Url)
    for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
      if ($sendToken) {
        "header = `"Authorization: Bearer $($env:GITHUB_TOKEN)`"" | & curl.exe --config - @curlBase
      } else {
        & curl.exe @curlBase
      }
      if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $downloadTarget)) {
        # v2 fallback: @opencode (current) -> @opencode-ai (legacy beta scope).
        $downloaded = $true
      }
      else {
        if ($IsV2 -and $Url -like "*@opencode/cli-windows*") {
          $altUrl = $Url -replace "@opencode/cli-windows", "@opencode-ai/cli-windows"
          Step "retry with legacy scope @opencode-ai..."
          $curlAlt = @('-fSL', '--retry', '3', '--retry-all-errors', '--connect-timeout', '30', '-o', $downloadTarget, $altUrl)
          & curl.exe @curlAlt
          if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $downloadTarget)) { $downloaded = $true; $Url = $altUrl }
          else { Step "download retry $i/3..."; Start-Sleep 5 }
        } else { Step "download retry $i/3..."; Start-Sleep 5 }
      }
    }
  }
  if (-not $downloaded) {
    for ($i = 1; $i -le 3 -and -not $downloaded; $i++) {
      try {
        $reqHeaders = @{}
        if ($sendToken) { $reqHeaders = $headers }
        Invoke-WebRequest -Uri $Url -OutFile $downloadTarget -TimeoutSec 600 -Headers $reqHeaders
        $downloaded = Test-Path -LiteralPath $downloadTarget
      } catch { if ($i -eq 3) { throw }; Step "download retry $i/3..."; Start-Sleep 5 }
    }
  }
  if (-not $downloaded) { throw "download failed after retries." }
  if ($IsV2) {
    # v2 npm tgz -> re-zip opencode.exe as payload (runtime extractor stays zip-only).
    $TgzHashRecord = (Get-FileHash -LiteralPath $downloadTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    Step "tgz sha256: $TgzHashRecord"
    $tgzTmp = Join-Path $DownloadTmp "tgz"
    New-Item -ItemType Directory -Path $tgzTmp -Force | Out-Null
    $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
    if (-not $tar) { throw "tar.exe required for v2 tgz but not found." }
    & tar.exe -xzf $downloadTarget -C $tgzTmp
    if ($LASTEXITCODE -ne 0) { throw "tgz extraction failed." }
    $exeHit = Get-ChildItem -LiteralPath $tgzTmp -Filter 'opencode.exe' -Recurse | Select-Object -First 1
    if (-not $exeHit) { throw "no opencode.exe in npm tgz." }
    if ($exeHit.Length -lt 10MB) { throw "npm opencode.exe suspiciously small, refusing embed." }
    Step ("npm exe: {0:N1} MB, re-zipping as payload..." -f ($exeHit.Length / 1MB))
    Compress-Archive -LiteralPath $exeHit.FullName -DestinationPath $PayloadZip -Force
  }
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
  # Provenance FIRST: a failed check below must never leave the pin file
  # modified. Sigstore release attestation, v1 GitHub assets only — enforced
  # when `gh` is present AND authenticated (hash pin recorded after this is
  # always enforced). Upstream uses the in-toto release predicate, NOT gh's
  # SLSA default — without it every verification fails. npm tgz (v2) carries
  # no GitHub attestation: skipped.
  if (-not $IsV2) {
    $Gh = Get-Command gh -ErrorAction SilentlyContinue
    $ghAuthed = $false
    if ($Gh) {
      $null = & gh auth status 2>&1
      $ghAuthed = ($LASTEXITCODE -eq 0)
    }
    if (-not $Gh) {
      Step "nota: `gh` assente, provenance non verificata (hash pin attivo)."
    } elseif (-not $ghAuthed) {
      Step "nota: `gh` senza autenticazione, provenance non verificata (hash pin attivo)."
    } else {
      $verified = $false
      $ghText = ""
      for ($r = 1; $r -le 2 -and -not $verified; $r++) {
        $ghOut = & gh attestation verify $PayloadZip --repo 'anomalyco/opencode' --predicate-type 'https://in-toto.io/attestation/release/v0.2' 2>&1
        if ($LASTEXITCODE -eq 0) { $verified = $true }
        else { $ghText = ($ghOut | Out-String); if ($r -lt 2) { Start-Sleep 5 } }
      }
      if ($verified) {
        Step "attestation verificata (provenance: anomalyco/opencode)."
      } elseif ($ghText -match '(?i)no attestation') {
        Step "ATTENZIONE: nessuna attestation per questo asset, vale solo hash pin."
      } else {
        throw "attestation verification FAILED, refusing to embed.`n$ghText"
      }
    }
  }
  # Pin the hash: the launcher verifies it before every extraction (supply-chain
  # pinning without freezing the version - publish-fat resolves latest at build).
  $PayloadHash = Join-Path $PayloadDir 'opencode.zip.sha256'
  $zipHashRecord = (Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash.ToLowerInvariant()
  $zipHashRecord | Set-Content -LiteralPath $PayloadHash -NoNewline
  # Committed pin (tracked): setup.* scripts fail closed against this for the
  # same version+arch instead of trusting TLS alone. Upsert our line, keep
  # other arches (recorded by their own publish runs).
  # v1: pin = zip hash. v2: pin = npm tgz hash (what setup.* downloads).
  $PinFile = Join-Path $Root 'UPSTREAM_VERSION.sha256'
  $pinHash = $zipHashRecord
  if ($IsV2 -and $TgzHashRecord -match '^[0-9a-f]{64}$') { $pinHash = $TgzHashRecord }
  # Rotation gate: a recorded pin for the same arch+version must never change
  # silently (upstream re-cut the asset, or tampering). Accept explicitly.
  $existing = @()
  if (Test-Path -LiteralPath $PinFile) { $existing = @(Get-Content -LiteralPath $PinFile | Where-Object { $_ -match "^windows-$Arch\s+$Version\s+([0-9a-f]{64})\s*$" }) }
  if ($existing.Count -gt 0) {
    $oldHash = ([regex]::Match($existing[0], '([0-9a-f]{64})').Groups[1].Value).ToLowerInvariant()
    if ($oldHash -ne $pinHash -and -not $AllowPinRotation) {
      throw "recorded pin for windows-$Arch $Version differs from the downloaded archive (upstream re-cut the asset, or possible tampering). Re-run with -AllowPinRotation to accept explicitly."
    }
    if ($oldHash -ne $pinHash) { Step "pin rotation accepted explicitly (-AllowPinRotation)." }
  }
  $pinLine = "windows-$Arch $Version $pinHash"
  $pins = @()
  if (Test-Path -LiteralPath $PinFile) { $pins = @(Get-Content -LiteralPath $PinFile | Where-Object { $_ -notmatch "^windows-$Arch\s+$Version\s+" -and $_.Trim() -ne "" }) }
  $pins += $pinLine
  $pins | Set-Content -LiteralPath $PinFile
  Step "recorded hash pin: $pinLine"

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
  # v1: upstream hash = zip hash. v2: upstream hash = npm tgz hash (pin file).
  $zipHash = (Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash.ToLowerInvariant()
  $upstreamHash = $zipHash
  if ($IsV2 -and $TgzHashRecord -match '^[0-9a-f]{64}$') { $upstreamHash = $TgzHashRecord }
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
        hashes = @([ordered]@{ alg = "SHA-256"; content = $upstreamHash })
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
  # Remove only what we downloaded/created (.zip + .sha256 + tgz tmp); never wipe a pre-existing payload dir.
  foreach ($f in @($PayloadZip, (Join-Path $PayloadDir 'opencode.zip.sha256'))) {
    if (Test-Path -LiteralPath $f) {
      Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }
  }
  if (Test-Path -LiteralPath $DownloadTmp) {
    Remove-Item -LiteralPath $DownloadTmp -Recurse -Force -ErrorAction SilentlyContinue
  }
  Step "staging pulita."
}
