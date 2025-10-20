@echo off
set "fileName=ProjectPerseus.addin"

:: List of target directories
set "targetDirs=%appdata%\Autodesk\Revit\Addins\2021 %appdata%\Autodesk\Revit\Addins\2022 %appdata%\Autodesk\Revit\Addins\2023 %appdata%\Autodesk\Revit\Addins\2024 %appdata%\Autodesk\Revit\Addins\2025 %appdata%\Autodesk\Revit\Addins\2026"

for %%D in (%targetDirs%) do (
    if exist "%%D\%fileName%" (
        echo Deleting %%D\%fileName%...
        del "%%D\%fileName%"
    ) else (
        echo File not found in %%D
    )
)

echo Done.