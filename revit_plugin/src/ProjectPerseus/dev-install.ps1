# dev-install.ps1
# Writes an .addin manifest pointing directly at the Debug build output.
# Called by the DevInstall MSBuild target on every Debug build.
# No file-copy needed — Revit loads the DLL straight from bin\x64\Debug {year}\.

param(
    [Parameter(Mandatory)][string]$TargetPath,
    [Parameter(Mandatory)][string]$RevitVersion
)

$addinsDir = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
$addinFile = "$addinsDir\ProjectPerseus.addin"

if (!(Test-Path $addinsDir)) {
    New-Item -ItemType Directory -Force $addinsDir | Out-Null
}

$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Project Perseus</Name>
    <Assembly>$TargetPath</Assembly>
    <AddInId>257f1a77-bc53-4b92-84db-4aa79a7f3050</AddInId>
    <FullClassName>ProjectPerseus.Plugin</FullClassName>
    <VendorId>Andrew Butler</VendorId>
    <VendorDescription>Andrew Butler</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

Set-Content -Path $addinFile -Value $xml -Encoding UTF8
Write-Host "[Perseus DevInstall] $addinFile -> $TargetPath"
