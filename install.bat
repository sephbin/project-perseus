@echo off
set "sourceDir=K:\Computational Design Group\93_Development\project-perseus\revit_plugin\src\ProjectPerseus\bin\Debug"
set "installDir=%appdata%\ProjectPerseus"

:: Create target directory if it doesn't exist
if not exist "%installDir%" (
    echo Creating target directory: %installDir%
    mkdir "%installDir%"
)

xcopy "%sourceDir%" "%installDir%" /E /I /Y

set "sourceFile=K:\Computational Design Group\93_Development\project-perseus\ProjectPerseus.addin"

:: List of target directories
set "targetDirs=%appdata%\Autodesk\Revit\Addins\2021 %appdata%\Autodesk\Revit\Addins\2022 %appdata%\Autodesk\Revit\Addins\2023 %appdata%\Autodesk\Revit\Addins\2024 %appdata%\Autodesk\Revit\Addins\2025 %appdata%\Autodesk\Revit\Addins\2026"

for %%D in (%targetDirs%) do (
    if exist "%%D" (
        echo Copying to %%D...
        copy "%sourceFile%" "%%D"
    ) else (
        echo Directory %%D does not exist.
    )
)
