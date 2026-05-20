@echo off
setlocal EnableDelayedExpansion

:: ==============================================================
:: install_task.bat  -  Perseus Task Scheduler Installer
:: Uses schtasks /xml to avoid quoting and line-continuation bugs.
:: Run this ONCE as the user who will run Revit (NOT as Administrator).
::
:: Usage:
::   install_task.bat            (install/update)
::   install_task.bat remove     (remove the task)
:: ==============================================================

:: --------------------------------------------------------------
:: CONFIGURATION  -  edit these lines
:: --------------------------------------------------------------

:: Choose launcher: "bat" or "python"
set LAUNCHER_TYPE=bat

:: Time to run each day (24-hour HH:MM)
set TASK_TIME=22:00

:: --------------------------------------------------------------

set TASK_NAME=Perseus Batch Sync
set SCRIPT_DIR=%~dp0

if /i "%~1"=="remove" goto :remove

:: --- Pick the launcher script and command ---
if /i "%LAUNCHER_TYPE%"=="python" (
    set LAUNCHER_SCRIPT=%SCRIPT_DIR%launch_batch.py
    set EXEC_CMD=python.exe
    set EXEC_ARGS="%SCRIPT_DIR%launch_batch.py"
) else (
    set LAUNCHER_SCRIPT=%SCRIPT_DIR%launch_batch.bat
    set EXEC_CMD=cmd.exe
    set EXEC_ARGS=/c "%SCRIPT_DIR%launch_batch.bat"
)

if not exist "%LAUNCHER_SCRIPT%" (
    echo ERROR: Launcher not found:
    echo   %LAUNCHER_SCRIPT%
    echo Check LAUNCHER_TYPE at the top of this file.
    goto :end
)

:: --- Write a Task Scheduler XML to a temp file ---
:: This avoids all schtasks command-line quoting issues.
:: LogonType=InteractiveToken means the task only runs when this
:: user is logged on, which is required for Revit to show its UI.
set XML=%TEMP%\perseus_task.xml

(
echo ^<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"^>
echo   ^<RegistrationInfo^>
echo     ^<Description^>Perseus automated batch sync^</Description^>
echo   ^</RegistrationInfo^>
echo   ^<Triggers^>
echo     ^<CalendarTrigger^>
echo       ^<StartBoundary^>2000-01-01T%TASK_TIME%:00^</StartBoundary^>
echo       ^<Enabled^>true^</Enabled^>
echo       ^<ScheduleByDay^>
echo         ^<DaysInterval^>1^</DaysInterval^>
echo       ^</ScheduleByDay^>
echo     ^</CalendarTrigger^>
echo   ^</Triggers^>
echo   ^<Principals^>
echo     ^<Principal id="Author"^>
echo       ^<LogonType^>InteractiveToken^</LogonType^>
echo       ^<RunLevel^>LeastPrivilege^</RunLevel^>
echo     ^</Principal^>
echo   ^</Principals^>
echo   ^<Settings^>
echo     ^<MultipleInstancesPolicy^>IgnoreNew^</MultipleInstancesPolicy^>
echo     ^<ExecutionTimeLimit^>PT8H^</ExecutionTimeLimit^>
echo     ^<StartWhenAvailable^>true^</StartWhenAvailable^>
echo     ^<AllowStartIfOnBatteries^>true^</AllowStartIfOnBatteries^>
echo     ^<DontStopIfGoingOnBatteries^>true^</DontStopIfGoingOnBatteries^>
echo   ^</Settings^>
echo   ^<Actions Context="Author"^>
echo     ^<Exec^>
echo       ^<Command^>%EXEC_CMD%^</Command^>
echo       ^<Arguments^>%EXEC_ARGS%^</Arguments^>
echo       ^<WorkingDirectory^>%SCRIPT_DIR%^</WorkingDirectory^>
echo     ^</Exec^>
echo   ^</Actions^>
echo ^</Task^>
) > "%XML%"

:: --- Remove old task then import ---
schtasks /delete /tn "%TASK_NAME%" /f >nul 2>&1
schtasks /create /tn "%TASK_NAME%" /xml "%XML%" /f
set RESULT=%ERRORLEVEL%
del "%XML%" >nul 2>&1

if %RESULT% EQU 0 (
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
    echo ERROR: schtasks failed with code %RESULT%.
    echo Make sure you are NOT running as Administrator.
)
goto :end

:remove
schtasks /delete /tn "%TASK_NAME%" /f
if %ERRORLEVEL% EQU 0 (
    echo Task "%TASK_NAME%" removed.
) else (
    echo Task "%TASK_NAME%" was not found.
)

:end
endlocal
pause
