@echo off
setlocal EnableDelayedExpansion

:: ==============================================================
:: launch_batch.bat  -  Perseus Batch Launcher (CMD/BAT version)
:: Writes batch_task.json (or resumes an existing one) and loops
:: launching Revit until the plugin reports the batch is done.
::
:: Survival loop: the plugin's BatchProcessor exits Revit with a
:: non-zero code and leaves batch_task.json behind when there's
:: more work pending (e.g. a cloud-model GetUserWorksetInfo
:: poisoned the session). When the batch is complete, the plugin
:: deletes batch_task.json. We use the file's presence as the
:: authoritative signal because a hard native crash might not
:: set a clean exit code.
::
:: Note: CMD has no JSON parser, so "resume" here just means
:: "the file exists" - we trust that the plugin removes it on
:: completion. The .ps1 and .py launchers do a real parse.
::
:: Schedule with install_task.bat, or double-click to test.
:: ==============================================================

set REVIT_EXE=C:\Program Files\Autodesk\Revit 2026\Revit.exe
set BATCH_DIR=%APPDATA%\ProjectPerseus
set BATCH_FILE=%BATCH_DIR%\batch_task.json
set MAX_RELAUNCHES=20
set RELAUNCH_DELAY=5

:: --------------------------------------------------------------
:: Abort if Revit is already open
:: --------------------------------------------------------------
tasklist /FI "IMAGENAME eq Revit.exe" 2>NUL | find /I "Revit.exe" >NUL
if %ERRORLEVEL% EQU 0 (
    echo Revit is already running. Aborting to avoid conflicts.
    exit /b 1
)

:: --------------------------------------------------------------
:: Abort if Revit executable is missing
:: --------------------------------------------------------------
if not exist "%REVIT_EXE%" (
    echo Revit 2026 not found at: %REVIT_EXE%
    exit /b 1
)

:: --------------------------------------------------------------
:: Ensure output directory exists
:: --------------------------------------------------------------
if not exist "%BATCH_DIR%\" mkdir "%BATCH_DIR%"

:: --------------------------------------------------------------
:: Resume vs. fresh: if batch_task.json already exists, the
:: plugin left work unfinished - resume instead of clobbering.
:: --------------------------------------------------------------
if exist "%BATCH_FILE%" (
    echo Resuming existing batch_task.json - leaving file untouched.
    goto :launch_loop
)

:: --------------------------------------------------------------
:: Build timestamp using WMIC (locale-independent)
:: Output format: LocalDateTime=20260520193000.000000+600
:: --------------------------------------------------------------
for /f "tokens=2 delims==" %%I in ('wmic os get LocalDateTime /value 2^>nul') do set DT=%%I
set TIMESTAMP=%DT:~0,4%-%DT:~4,2%-%DT:~6,2%T%DT:~8,2%:%DT:~10,2%:%DT:~12,2%

if "%TIMESTAMP%"=="-T::" (
    echo ERROR: Could not read system clock via WMIC.
    exit /b 1
)

:: --------------------------------------------------------------
:: Write batch_task.json
:: Note: ^ in regex values must be written as ^^ in CMD echo.
::       "." blacklist and "^^A-" whitelist produce "." and "^A-" in the file.
:: --------------------------------------------------------------
(
echo {
echo   "models_to_process": [
echo     {
echo       "model_guid":              "99f865fc-b79f-4d04-8105-40f15f3cbba6",
echo       "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
echo       "region":                  "AUS",
echo       "workset_blacklist_regex": ".",
echo       "workset_whitelist_regex": "^^A-"
echo     },
echo     {
echo       "model_guid":              "ff8b5406-b4ff-4efb-86c0-3d93a36971a9",
echo       "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
echo       "region":                  "AUS",
echo       "workset_blacklist_regex": ".",
echo       "workset_whitelist_regex": "^^A-"
echo     },
echo     {
echo       "model_guid":              "40061fc2-0d97-4c6a-a9c1-110319730f8c",
echo       "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
echo       "region":                  "AUS",
echo       "workset_blacklist_regex": ".",
echo       "workset_whitelist_regex": "^^A-"
echo     },
echo     {
echo       "model_guid":              "08cff244-9181-46dc-91f6-27877f9e2e8b",
echo       "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
echo       "region":                  "AUS",
echo       "workset_blacklist_regex": ".",
echo       "workset_whitelist_regex": "^^A-"
echo     },
echo     {
echo       "model_guid":              "c84fd25e-3db1-40ed-8834-3cdd29d7a3c8",
echo       "project_guid":            "49e82fc1-fe5f-4c57-83aa-35e0b7df5406",
echo       "region":                  "AUS",
echo       "workset_blacklist_regex": ".",
echo       "workset_whitelist_regex": "^^A-"
echo     }
echo   ],
echo   "timestamp": "%TIMESTAMP%"
echo }
) > "%BATCH_FILE%"

echo Written: %BATCH_FILE%

:: --------------------------------------------------------------
:: Survival loop: launch Revit, wait, decide based on file presence.
:: --------------------------------------------------------------
:launch_loop
set RELAUNCH=0

:relaunch
set /a RELAUNCH+=1
if %RELAUNCH% GTR %MAX_RELAUNCHES% (
    echo Hit max relaunches (%MAX_RELAUNCHES%^). Stopping; investigate batch_task.json manually.
    exit /b 1
)

echo.
echo === Revit launch %RELAUNCH% of %MAX_RELAUNCHES% ===
echo Starting Revit and waiting for exit...
start "" /wait "%REVIT_EXE%"
set EXITCODE=%ERRORLEVEL%
echo Revit exited with code %EXITCODE%.

if not exist "%BATCH_FILE%" (
    echo batch_task.json is gone. Batch complete.
    exit /b 0
)

if %EXITCODE% EQU 0 (
    echo Exit 0 but batch_task.json still exists. Treating as complete and stopping to avoid loop.
    exit /b 0
)

echo More work pending. Relaunching in %RELAUNCH_DELAY% second(s)...
timeout /t %RELAUNCH_DELAY% /nobreak >nul
goto :relaunch

endlocal
