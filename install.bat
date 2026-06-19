@echo off
setlocal enabledelayedexpansion

:: ============================================================
:: Project Perseus - Dev Test Installer
:: Supports: Revit 2021, 2022, 2023, 2024
:: Skipped:  Revit 2025+ (addin install via .bat not supported)
:: ============================================================

set "UNC=\\sydsrv01\projects\Computational Design Group\93_Development\testing\project-perseus"

set "srcDir23=%UNC%\revit_plugin\src\ProjectPerseus\bin\x64\Debug 2023"
set "srcDir26=%UNC%\revit_plugin\src\ProjectPerseus\bin\x64\Debug 2026"

set "installDir23=%appdata%\ProjectPerseus\2023"
set "installDir26=%appdata%\ProjectPerseus\2026"

set "addInId=257f1a77-bc53-4b92-84db-4aa79a7f3050"
set "addInClass=ProjectPerseus.Plugin"
set "addInName=Project Perseus"
set "addInVendor=Andrew Butler"

:: --- Remove any existing addin manifests (all versions) ---
echo Removing existing addin manifests...
for %%V in (2021 2022 2023 2024 2025 2026) do (
    set "f=%appdata%\Autodesk\Revit\Addins\%%V\ProjectPerseus.addin"
    if exist "!f!" (
        echo   Deleting !f!
        del "!f!"
    )
)

:: --- Copy builds locally ---
echo.
echo Copying Debug 2023 build (Revit 2021-2023^)...
if not exist "%installDir23%" mkdir "%installDir23%"
xcopy "%srcDir23%" "%installDir23%" /E /I /Y

echo.
echo Copying Debug 2026 build (Revit 2024^)...
if not exist "%installDir26%" mkdir "%installDir26%"
xcopy "%srcDir26%" "%installDir26%" /E /I /Y

:: --- Write addin manifests ---
echo.
echo Writing addin manifests...

:: Revit 2021, 2022, 2023 -> Debug 2023 build
for %%V in (2021 2022 2023) do (
    set "addinsDir=%appdata%\Autodesk\Revit\Addins\%%V"
    if exist "!addinsDir!" (
        echo   Writing !addinsDir!\ProjectPerseus.addin
        (
            echo ^<?xml version="1.0" encoding="utf-8"?^>
            echo ^<RevitAddIns^>
            echo   ^<AddIn Type="Application"^>
            echo     ^<Name^>%addInName%^</Name^>
            echo     ^<Assembly^>%installDir23%\ProjectPerseus.dll^</Assembly^>
            echo     ^<AddInId^>%addInId%^</AddInId^>
            echo     ^<FullClassName^>%addInClass%^</FullClassName^>
            echo     ^<VendorId^>%addInVendor%^</VendorId^>
            echo     ^<VendorDescription^>%addInVendor%^</VendorDescription^>
            echo   ^</AddIn^>
            echo ^</RevitAddIns^>
        ) > "!addinsDir!\ProjectPerseus.addin"
    ) else (
        echo   Skipping Revit %%V ^(Addins folder not found^)
    )
)

:: Revit 2024 -> Debug 2026 build
set "addinsDir=%appdata%\Autodesk\Revit\Addins\2024"
if exist "%addinsDir%" (
    echo   Writing %addinsDir%\ProjectPerseus.addin
    (
        echo ^<?xml version="1.0" encoding="utf-8"?^>
        echo ^<RevitAddIns^>
        echo   ^<AddIn Type="Application"^>
        echo     ^<Name^>%addInName%^</Name^>
        echo     ^<Assembly^>%installDir26%\ProjectPerseus.dll^</Assembly^>
        echo     ^<AddInId^>%addInId%^</AddInId^>
        echo     ^<FullClassName^>%addInClass%^</FullClassName^>
        echo     ^<VendorId^>%addInVendor%^</VendorId^>
        echo     ^<VendorDescription^>%addInVendor%^</VendorDescription^>
        echo   ^</AddIn^>
        echo ^</RevitAddIns^>
    ) > "%addinsDir%\ProjectPerseus.addin"
) else (
    echo   Skipping Revit 2024 ^(Addins folder not found^)
)

echo.
echo Done. Note: Revit 2025 and 2026 are not supported by this install method.
pause
endlocal
