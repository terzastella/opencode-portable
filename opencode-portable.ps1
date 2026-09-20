#!/usr/bin/env pwsh
# ============================================================
#  Opencode Portable launcher (Windows PowerShell)
#  Mirror di opencode-portable.cmd: config, dati, cache e log
#  restano dentro .\data. Le variabili d'ambiente vengono
#  ripristinate all'uscita (try/finally).
# ============================================================

$Root = Split-Path $MyInvocation.MyCommand.Definition -Parent
$Data = Join-Path $Root 'data'
$TmpRoot = Join-Path $Data 'tmp'
foreach ($d in @('config', 'data', 'cache', 'state', 'tmp')) {
  $p = Join-Path $Data $d
  if (-not (Test-Path -LiteralPath $p)) {
    New-Item -ItemType Directory -Path $p | Out-Null
  }
}

# First run: create config from template on fresh clone
# (Join-Path nested per-component: literal backslashes break pwsh on Linux/macOS)
$Cfg = Join-Path (Join-Path $Root 'config') 'opencode.json'
$CfgExample = Join-Path (Join-Path $Root 'config') 'opencode.example.json'
if (-not (Test-Path -LiteralPath $Cfg) -and (Test-Path -LiteralPath $CfgExample)) {
  Copy-Item -LiteralPath $CfgExample -Destination $Cfg
}

$Names = @('XDG_CONFIG_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME', 'XDG_STATE_HOME', 'OPENCODE_CONFIG', 'OPENCODE_DISABLE_MODELS_FETCH', 'TMP', 'TEMP', 'TMPDIR')
$Saved = @{}
foreach ($n in $Names) {
  $Saved[$n] = [Environment]::GetEnvironmentVariable($n, 'Process')
}

$code = 1
try {
  $env:XDG_CONFIG_HOME = Join-Path $Data 'config'
  $env:XDG_DATA_HOME   = Join-Path $Data 'data'
  $env:XDG_CACHE_HOME  = Join-Path $Data 'cache'
  $env:XDG_STATE_HOME  = Join-Path $Data 'state'
  $env:OPENCODE_CONFIG = Join-Path (Join-Path $Root 'config') 'opencode.json'
  # Niente fetch bloccante di models.dev all'avvio (10-30s): usa cache/integrato
  $env:OPENCODE_DISABLE_MODELS_FETCH = '1'
  # Foto della temp di sistema PRIMA del redirect (guard: cancello solo cio che nasce in questa run)
  $SysTargets = @()
  if ($env:TEMP) { $p = Join-Path $env:TEMP 'opencode'; $SysTargets += @{ Path = $p; Existed = (Test-Path -LiteralPath $p) } }
  if ($env:LOCALAPPDATA) { $p = Join-Path $env:LOCALAPPDATA 'opencode'; $SysTargets += @{ Path = $p; Existed = (Test-Path -LiteralPath $p) } }
  if ($env:APPDATA) { $p = Join-Path $env:APPDATA 'opencode'; $SysTargets += @{ Path = $p; Existed = (Test-Path -LiteralPath $p) } }

  # Temp isolata per questa run (sicura anche con istanze concorrenti)
  $RunId = 'run-{0:yyyyMMdd-HHmmss}-{1}-{2}' -f (Get-Date), $PID, (Get-Random -Maximum 100000)
  $RunTmp = Join-Path $TmpRoot $RunId
  if (Test-Path -LiteralPath $RunTmp) { $RunTmp = '{0}-{1}' -f $RunTmp, (Get-Random -Maximum 100000) }
  New-Item -ItemType Directory -Path $RunTmp -ErrorAction SilentlyContinue | Out-Null
  $CleanRunTmp = Test-Path -LiteralPath $RunTmp
  if (-not $CleanRunTmp) { $RunTmp = $TmpRoot }

  # Sweeper: dead run tmps (crash/kill leftovers). STRICT: PID in the name must
  # be dead; live PIDs are never touched, whatever their age.
  Get-ChildItem -LiteralPath $TmpRoot -Filter 'run-*' -Directory -ErrorAction SilentlyContinue |
    ForEach-Object {
      if ($_.Name -notmatch '^run-\d{8}-\d{6}-(\d+)-\d+$') { return } # not ours: never touch
      $alive = $false
      try { $p = Get-Process -Id ([int]$Matches[1]) -ErrorAction Stop; $alive = -not $p.HasExited } catch { }
      if (-not $alive) { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }

  # Foto della tmp del portable (guard: opencode potrebbe scriverci una cartella "opencode")
  $TmpRootOpenc = Join-Path $TmpRoot 'opencode'
  $TmpRootOpencExisted = Test-Path -LiteralPath $TmpRootOpenc

  # Temp di opencode e processi figli nella tmp di questa run
  $env:TMP = $RunTmp
  $env:TEMP = $RunTmp
  $env:TMPDIR = $RunTmp

  # Bundled EXE. PATH fallback ONLY with explicit opt-in (--from-path as first
  # argument, or OPENCODE_ALLOW_PATH_FALLBACK=1): without opt-in a same-name
  # executable in PATH would run silently.
  $AllowPath = ($env:OPENCODE_ALLOW_PATH_FALLBACK -eq '1')
  $fwdArgs = @($args)
  if ($fwdArgs.Count -gt 0 -and $fwdArgs[0] -eq '--from-path') {
    $AllowPath = $true
    $fwdArgs = @($fwdArgs | Select-Object -Skip 1)
  }

  $Bin = Join-Path (Join-Path $Root 'bin') 'opencode.exe'
  if (-not (Test-Path -LiteralPath $Bin)) {
    if (-not $AllowPath) { throw "bin missing: $Bin. Run scripts/setup.ps1, or pass --from-path first / set OPENCODE_ALLOW_PATH_FALLBACK=1 to reuse opencode from PATH." }
    $found = Get-Command opencode -ErrorAction SilentlyContinue
    if (-not $found) { throw "no opencode found in PATH." }
    $Bin = $found.Source
    Write-Warning "[opencode-portable] using opencode from PATH: $Bin"
  }

  # Support pipeline input
  if ($MyInvocation.ExpectingInput) { $input | & $Bin @fwdArgs }
  else { & $Bin @fwdArgs }
  # $LASTEXITCODE can stay $null for non-native shims: never propagate null.
  $code = if ($null -eq $LASTEXITCODE) { 1 } else { $LASTEXITCODE }
} catch {
  Write-Error "[opencode-portable] ERRORE: $($_.Exception.Message)"
  $code = 1
} finally {
  # Cleanup best-effort della sola tmp di questa run (mai il codice d'uscita)
  $sep = [IO.Path]::DirectorySeparatorChar
  if ($CleanRunTmp -and $RunTmp -and $RunTmp.StartsWith($TmpRoot + $sep, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $RunTmp -Recurse -Force -ErrorAction SilentlyContinue
  }
  if ($TmpRootOpenc -and -not $TmpRootOpencExisted -and (Test-Path -LiteralPath $TmpRootOpenc)) {
    Remove-Item -LiteralPath $TmpRootOpenc -Recurse -Force -ErrorAction SilentlyContinue
  }
  # Cartelle opencode fuori dal portable: solo se generate da questa run
  foreach ($t in $SysTargets) {
    if (-not $t.Existed -and (Test-Path -LiteralPath $t.Path)) {
      Remove-Item -LiteralPath $t.Path -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
  foreach ($n in $Names) {
    if ($null -eq $Saved[$n]) { Remove-Item "Env:\$n" -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable($n, $Saved[$n], 'Process') }
  }
}
exit $code
