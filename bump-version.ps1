# bump-version.ps1
# Increments the patch number in AssemblyInfo.cs and perseus_installer.iss.
# Called automatically by the post-build MSBuild target (patch bump).
# Can also be run manually for minor/major bumps:
#   .\bump-version.ps1           # 1.1.7 -> 1.1.8  (default)
#   .\bump-version.ps1 -BumpMinor  # 1.1.7 -> 1.2.0
#   .\bump-version.ps1 -BumpMajor  # 1.1.7 -> 2.0.0

param(
    [switch]$BumpMinor,
    [switch]$BumpMajor
)

$AssemblyInfoPath = "$PSScriptRoot\revit_plugin\src\ProjectPerseus\Properties\AssemblyInfo.cs"
$IssPath          = "$PSScriptRoot\revit_plugin\installer\perseus_installer.iss"

# --- Read current version from AssemblyInfo.cs ---
$content = [System.IO.File]::ReadAllText($AssemblyInfoPath)

if ($content -notmatch '\[assembly: AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]') {
    Write-Error "Could not parse AssemblyVersion from $AssemblyInfoPath"
    exit 1
}

$verMaj = [int]$Matches[1]
$verMin = [int]$Matches[2]
$verPat = [int]$Matches[3]
$verRev = [int]$Matches[4]
$oldVer = "$verMaj.$verMin.$verPat"

# --- Calculate new version ---
if ($BumpMajor) {
    $verMaj++; $verMin = 0; $verPat = 0
} elseif ($BumpMinor) {
    $verMin++; $verPat = 0
} else {
    $verPat++
}

$newAssemblyVer = "$verMaj.$verMin.$verPat.$verRev"
$newAppVer      = "$verMaj.$verMin.$verPat"

Write-Host "Bumping $oldVer -> $newAppVer"

# --- Update AssemblyInfo.cs ---
$content = $content -replace `
    '\[assembly: AssemblyVersion\("[^"]+"\)\]', `
    "[assembly: AssemblyVersion(""$newAssemblyVer"")]"
$content = $content -replace `
    '\[assembly: AssemblyFileVersion\("[^"]+"\)\]', `
    "[assembly: AssemblyFileVersion(""$newAssemblyVer"")]"
# Keep the comment in sync too
$content = $content -replace `
    '// \[assembly: AssemblyVersion\("[^"]+"\)\]', `
    "// [assembly: AssemblyVersion(""$newAssemblyVer"")]"

[System.IO.File]::WriteAllText($AssemblyInfoPath, $content, [System.Text.Encoding]::UTF8)
Write-Host "  Updated: AssemblyInfo.cs -> $newAssemblyVer"

# --- Update perseus_installer.iss ---
$iss = [System.IO.File]::ReadAllText($IssPath)
$iss = $iss -replace 'AppVersion=[\d.]+', "AppVersion=$newAppVer"
[System.IO.File]::WriteAllText($IssPath, $iss, [System.Text.Encoding]::UTF8)
Write-Host "  Updated: perseus_installer.iss -> $newAppVer"
