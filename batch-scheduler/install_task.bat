@echo off
setlocal EnableDelayedExpansion

:: ==============================================================
:: install_task.bat  -  Perseus Task Scheduler Installer
:: Registers a daily Windows Scheduled Task using schtasks.exe.
:: Run this ONCE on the target machine as the user who runs Revit.
:: Do NOT run as Administrator — Revit requires an interactive session.
::
:: Usage:
::   install_task.bat            (installs/updates the task)
::   install_task.bat remove     (removes the task)
:: ==============================================================

:: --------------------------------------------------------------
:: CONFIGURATION  -  edit these three lines
:: --------------------------------------------------------------

:: Choose your launcher: "bat" or "python"
set LAUNCHER_TYPE=bat

:: Time the task fires each day (24-hour HH:MM)
set TASK_TIME=22:00

:: Full path to your launcher script (defaults to same folder as this file)
set SCRIPT_DIR=%~dp0

:: ==============================================================

set TASK_NAME=Perseus Batch Sync

:: --- Remove mode ---
if /i "%~1"=="remove" goto :remove

:: --- Derive launcher command ---
if /i "%LAUNCHER_TYPE%"=="python" (
    set LAUNCHER_SCRIPT=%SCRIPT_DIR%launch_batch.py
    set TASK_CMD=python.exe "%LAUNCHER_SCRIPT%"
) else (
    set LAUNCHER_SCRIPT=%SCRIPT_DIR%launch_batch.bat
    set TASK_CMD=cmd.exe /c "%LAUNCHER_SCRIPT%"
)

:: --- Validate script exists ---
if not exist "%LAUNCHER_SCRIPT%" (
    echo ERROR: Launcher not found at:
    echo   %LAUNCHER_SCRIPT%
    echo Check LAUNCHER_TYPE and SCRIPT_DIR at the top of this file.
    exit /b 1
)

:: --- Remove any existing task silently ---
schtasks /delete /tn "%TASK_NAME%" /f >nul 2>&1

:: --- Register the task ---
:: /it  = interactive only (runs only when this user is logged on — required for Revit)
:: /rl  = run level LIMITED (non-elevated, required for Revit UI)
:: /f   = force overwrite without confirmation
schtasks /create ^
    /tn "%TASK_NAME%" ^
    /tr "%TASK_CMD%" ^
    /sc DAILY ^
    /st %TASK_TIME% ^
    /it ^
    /rl LIMITED ^
    /f

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Task "%TASK_NAME%" registered successfully.
    echo   Launcher : %LAUNCHER_TYPE%
    echo   Script   : %LAUNCHER_SCRIPT%
    echo   Schedule : daily at %TASK_TIME%
    echo.
    echo To test immediately:
    echo   schtasks /run /tn "%TASK_NAME%"
    echo.
    echo To remove:
    echo   install_task.bat remove
) else (
    echo.
    echo ERROR: schtasks failed with code %ERRORLEVEL%.
    echo Make sure you are NOT running this as Administrator.
)
goto :end

:remove
schtasks /delete /tn "%TASK_NAME%" /f
if %ERRORLEVEL% EQU 0 (
    echo Task "%TASK_NAME%" removed.
) else (
    echo Task "%TASK_NAME%" was not found or could not be removed.
)

:end
endlocal
pause
