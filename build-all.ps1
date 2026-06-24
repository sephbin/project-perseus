# build-all.ps1
# Builds Debug 2026 and Debug 2023 in sequence with a single version bump and single git commit.
#
# Flow:
#   1. Build Debug 2026 | x64  — bumps version, builds installer (2023 DLL skipped if missing), no commit
#   2. Build Debug 2023 | x64  — no version bump, builds installer (both DLLs present), triggers GitCommit
#
# Both builds install the .addin dev manifest (DevInstall) and kill Revit beforehand (KillRevit).
# Pass -DryRun to compile without committing (useful for testing the script).

param(
    [switch]$DryRun
)

Set-Location $PSScriptRoot

# --- Locate MSBuild ---
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Error "vswhere.exe not found. Is Visual Studio installed?"
    exit 1
}
$msBuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
if (-not $msBuild -or -not (Test-Path $msBuild)) {
    Write-Error "Could not locate MSBuild via vswhere."
    exit 1
}

$sln = Join-Path $PSScriptRoot "revit_plugin\src\ProjectPerseus.sln"

# --- Build Debug 2026 (primary: bumps version, no commit) ---
Write-Host ""
Write-Host "=== Building Debug 2026 | x64 ===" -ForegroundColor Cyan
$skipCommit = if ($DryRun) { 'true' } else { 'true' }   # always skip here; 2023 build commits
& $msBuild $sln /p:Configuration="Debug 2026" /p:Platform=x64 /p:SkipGitCommit=true /m /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Debug 2026 build failed (exit $LASTEXITCODE). Aborting."
    exit $LASTEXITCODE
}

# --- Build Debug 2023 (no version bump, GitCommit runs at end) ---
Write-Host ""
Write-Host "=== Building Debug 2023 | x64 ===" -ForegroundColor Cyan
$skipCommitFlag = if ($DryRun) { '/p:SkipGitCommit=true' } else { '' }
if ($DryRun) {
    & $msBuild $sln /p:Configuration="Debug 2023" /p:Platform=x64 /p:SkipBumpVersion=true /p:SkipGitCommit=true /m /nologo
} else {
    & $msBuild $sln /p:Configuration="Debug 2023" /p:Platform=x64 /p:SkipBumpVersion=true /m /nologo
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "Debug 2023 build failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "=== build-all complete ===" -ForegroundColor Green
