#!/usr/bin/env pwsh
# Create (or remove) Windows shortcuts for Opencode Portable with the custom icon.
# Usage: .\scripts\install-shortcuts.ps1
#        .\scripts\install-shortcuts.ps1 -Uninstall
# SPDX-License-Identifier: MIT
param([switch]$Uninstall)

# $IsWindows exists only on PowerShell 6+: use the framework check for 5.1 compat.
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw "install-shortcuts.ps1 is Windows-only (WScript.Shell + Start Menu)." }

$ErrorActionPreference = 'Stop'
$Root = Split-Path (Split-Path $MyInvocation.MyCommand.Definition -Parent) -Parent
$Name = 'Opencode Portable'
$Target = Join-Path $Root 'opencode-portable.cmd'
$Icon = Join-Path $Root 'assets\opencode-portable.ico'

if (-not (Test-Path -LiteralPath $Target)) { throw "launcher not found: $Target" }
if (-not (Test-Path -LiteralPath $Icon)) { throw "icon not found: $Icon. (Repo checkout complete?)" }

$Desktop = [Environment]::GetFolderPath('Desktop')
$StartMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
if ([string]::IsNullOrWhiteSpace($Desktop)) { throw "Desktop folder not found (redirected profile?)." }
if ([string]::IsNullOrWhiteSpace($StartMenu)) { throw "Start Menu folder not found." }
if (-not (Test-Path -LiteralPath $StartMenu)) { New-Item -ItemType Directory -Path $StartMenu -Force | Out-Null }
$Links = @(
  (Join-Path $Desktop "$Name.lnk"),
  (Join-Path $StartMenu "$Name.lnk")
)

$Shell = New-Object -ComObject WScript.Shell
if ($Uninstall) {
  foreach ($lnk in $Links) {
    if (Test-Path -LiteralPath $lnk) {
      Remove-Item -LiteralPath $lnk -Force
      Write-Host "[shortcuts] removed $lnk"
    } else {
      Write-Host "[shortcuts] not present $lnk"
    }
  }
  return
}

foreach ($lnk in $Links) {
  $sc = $Shell.CreateShortcut($lnk)
  $sc.TargetPath = $Target
  $sc.WorkingDirectory = $Root
  $sc.IconLocation = $Icon
  $sc.Description = 'Opencode Portable - config, dati e cache restano nella cartella'
  $sc.Save()
  Write-Host "[shortcuts] created $lnk"
}
