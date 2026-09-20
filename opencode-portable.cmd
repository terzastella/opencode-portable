@ECHO OFF
SETLOCAL
REM ============================================================
REM  Opencode Portable launcher (Windows)
REM  Config, dati, cache e log restano dentro la cartella
REM  portable (.\data). Niente in %USERPROFILE%\.config o
REM  %USERPROFILE%\.local\share.
REM ============================================================

REM Cartella che contiene questo script (senza backslash finale, ma mai
REM ridotta a lettera drive nuda: "C:\" deve restare "C:\", non "C:")
SET "ROOT=%~dp0"
IF "%ROOT%"=="%~d0\" SET "ROOT=%~d0\"
IF NOT "%ROOT%"=="%~d0\" IF "%ROOT:~-1%"=="\" SET "ROOT=%ROOT:~0,-1%"

SET "DATA=%ROOT%\data"
IF NOT EXIST "%DATA%\config" MKDIR "%DATA%\config"
IF NOT EXIST "%DATA%\data"   MKDIR "%DATA%\data"
IF NOT EXIST "%DATA%\cache"  MKDIR "%DATA%\cache"
IF NOT EXIST "%DATA%\state"  MKDIR "%DATA%\state"
SET "TMPROOT=%DATA%\tmp"
IF NOT EXIST "%TMPROOT%" MKDIR "%TMPROOT%"

REM Prima esecuzione: crea config da template se manca (fresh clone)
IF NOT EXIST "%ROOT%\config\opencode.json" IF EXIST "%ROOT%\config\opencode.example.json" COPY /Y "%ROOT%\config\opencode.example.json" "%ROOT%\config\opencode.json" >NUL

REM Redirect di config / dati / cache dentro .\data
SET "XDG_CONFIG_HOME=%DATA%\config"
SET "XDG_DATA_HOME=%DATA%\data"
SET "XDG_CACHE_HOME=%DATA%\cache"
SET "XDG_STATE_HOME=%DATA%\state"
SET "OPENCODE_CONFIG=%ROOT%\config\opencode.json"
REM Niente fetch bloccante di models.dev all'avvio (10-30s): usa cache/integrato
SET "OPENCODE_DISABLE_MODELS_FETCH=1"

REM Foto della temp di sistema PRIMA del redirect (guard: cancello solo cio che nasce in questa run)
SET "SYS_TMP=%TEMP%"
SET "SYS_OPENC=%SYS_TMP%\opencode"
IF EXIST "%SYS_OPENC%" (SET "SYS_OPENC_EXISTS=1") ELSE (SET "SYS_OPENC_EXISTS=0")
SET "SYS_LOCAL_OPENC=%LOCALAPPDATA%\opencode"
IF EXIST "%SYS_LOCAL_OPENC%" (SET "SYS_LOCAL_EXISTS=1") ELSE (SET "SYS_LOCAL_EXISTS=0")
SET "SYS_ROAM_OPENC=%APPDATA%\opencode"
IF EXIST "%SYS_ROAM_OPENC%" (SET "SYS_ROAM_EXISTS=1") ELSE (SET "SYS_ROAM_EXISTS=0")

REM Temp isolata per questa run (sicura anche con istanze concorrenti).
REM NOTA: %TIME% dipende dal locale (separatori diversi, spazi): sanitizza.
SET "RAWTIME=%TIME: =0%"
SET "RAWTIME=%RAWTIME:/=-%"
SET "RAWTIME=%RAWTIME:.=-%"
SET "RAWTIME=%RAWTIME:,=-%"
SET "RAWTIME=%RAWTIME:;=-%"
SET "RUNID=run-%RAWTIME:~0,2%%RAWTIME:~3,2%%RAWTIME:~6,2%%RAWTIME:~9,2%-%RANDOM%"
SET "RUN_TMP=%TMPROOT%\%RUNID%"
IF EXIST "%RUN_TMP%" SET "RUN_TMP=%TMPROOT%\%RUNID%-%RANDOM%"
IF EXIST "%RUN_TMP%" SET "RUN_TMP=%TMPROOT%\%RUNID%-%RANDOM%-%RANDOM%"
MKDIR "%RUN_TMP%" 2>NUL
SET "CLEAN_RUN_TMP=1"
IF NOT EXIST "%RUN_TMP%" SET "CLEAN_RUN_TMP=0"
IF "%CLEAN_RUN_TMP%"=="0" SET "RUN_TMP=%TMPROOT%"

REM Foto della tmp del portable (guard: opencode potrebbe scriverci una cartella "opencode")
SET "TMPROOT_OPENC=%TMPROOT%\opencode"
IF EXIST "%TMPROOT_OPENC%" (SET "TMPROOT_OPENC_EXISTS=1") ELSE (SET "TMPROOT_OPENC_EXISTS=0")

REM Sweeper: tmp di run morte da oltre 7 giorni (crash/kill). NOTA: cmd non
REM conosce il PID (niente liveness check come ps1/sh/exe): tiene la soglia
REM di 7 giorni per non toccare mai run recenti/concorrenti.
FORFILES /P "%TMPROOT%" /M "run-*" /D -7 /C "CMD /C IF @isdir==TRUE RMDIR /S /Q @path" 2>NUL

REM Temp di opencode e processi figli nella tmp di questa run
SET "TMP=%RUN_TMP%"
SET "TEMP=%RUN_TMP%"
SET "TMPDIR=%RUN_TMP%"

REM EXE bundled. Fallback a opencode da PATH SOLO con opt-in esplicito
REM (--from-path ovunque tra gli argomenti, o OPENCODE_ALLOW_PATH_FALLBACK=1):
REM senza opt-in un eseguibile omonimo nel PATH verrebbe lanciato in silenzio.
SET "BIN=%ROOT%\bin\opencode.exe"
SET "ALLOW_PATH=0"
IF /I "%OPENCODE_ALLOW_PATH_FALLBACK%"=="1" SET "ALLOW_PATH=1"
REM Ricostruisci gli argomenti togliendo --from-path (ovunque sia): SHIFT non
REM tocca %*, quindi %* inoltrerebbe il flag al figlio. Re-quoting uniforme.
SET "FWD="
:argloop
IF "%~1"=="" GOTO :argdone
IF "%~1"=="--from-path" (SET "ALLOW_PATH=1" & SHIFT /1 & GOTO :argloop)
SET "FWD=%FWD% "%~1""
SHIFT /1
GOTO :argloop
:argdone
IF EXIST "%BIN%" GOTO :runbin
IF "%ALLOW_PATH%"=="0" (
  ECHO [opencode-portable] ERRORE: "%BIN%" non trovato.
    ECHO [opencode-portable] Esegui scripts\setup.ps1 per scaricarlo, oppure riusa opencode da PATH con --from-path o OPENCODE_ALLOW_PATH_FALLBACK=1.
  ENDLOCAL & EXIT /B 1
)
WHERE opencode >NUL 2>&1
IF ERRORLEVEL 1 (
  ECHO [opencode-portable] ERRORE: nessun opencode in PATH.
  ENDLOCAL & EXIT /B 1
)
SET "BIN_FROM_PATH="
FOR /F "delims=" %%B IN ('WHERE opencode 2^>NUL') DO IF NOT DEFINED BIN_FROM_PATH SET "BIN_FROM_PATH=%%B"
IF NOT DEFINED BIN_FROM_PATH (
  ECHO [opencode-portable] ERRORE: impossibile risolvere opencode in PATH.
  ENDLOCAL & EXIT /B 1
)
SET "BIN=%BIN_FROM_PATH%"
ECHO [opencode-portable] ATTENZIONE: uso opencode da PATH: "%BIN%"
:runbin

"%BIN%" %FWD%
SET "EXITCODE=%ERRORLEVEL%"
REM Cleanup best-effort della sola tmp di questa run (mai il codice d'uscita)
SET "MARKER_CHECK=%RUN_TMP:\data\tmp\run-=%"
IF "%CLEAN_RUN_TMP%"=="1" IF NOT "%MARKER_CHECK%"=="%RUN_TMP%" IF EXIST "%RUN_TMP%" RMDIR /S /Q "%RUN_TMP%" 2>NUL
IF "%TMPROOT_OPENC_EXISTS%"=="0" IF EXIST "%TMPROOT_OPENC%" RMDIR /S /Q "%TMPROOT_OPENC%" 2>NUL
REM Cartelle opencode fuori dal portable: solo se generate da questa run
REM (RMDIR per le dir, DEL per gli eventuali file: mirror di DeletePath)
REM Cartelle opencode fuori dal portable: solo se generate da questa run.
REM RMDIR per le dir (test via suffisso \NUL), DEL per i file: mirror di DeletePath.
IF DEFINED SYS_TMP IF "%SYS_OPENC_EXISTS%"=="0" IF EXIST "%SYS_OPENC%\NUL" (RMDIR /S /Q "%SYS_OPENC%" 2>NUL) ELSE IF EXIST "%SYS_OPENC%" DEL /F /Q "%SYS_OPENC%" 2>NUL
IF DEFINED LOCALAPPDATA IF "%SYS_LOCAL_EXISTS%"=="0" IF EXIST "%SYS_LOCAL_OPENC%\NUL" (RMDIR /S /Q "%SYS_LOCAL_OPENC%" 2>NUL) ELSE IF EXIST "%SYS_LOCAL_OPENC%" DEL /F /Q "%SYS_LOCAL_OPENC%" 2>NUL
IF DEFINED APPDATA IF "%SYS_ROAM_EXISTS%"=="0" IF EXIST "%SYS_ROAM_OPENC%\NUL" (RMDIR /S /Q "%SYS_ROAM_OPENC%" 2>NUL) ELSE IF EXIST "%SYS_ROAM_OPENC%" DEL /F /Q "%SYS_ROAM_OPENC%" 2>NUL
ENDLOCAL & EXIT /B %EXITCODE%
