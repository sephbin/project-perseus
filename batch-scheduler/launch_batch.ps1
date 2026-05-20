# launch_batch.ps1
# Writes the Perseus batch instruction file with a fresh timestamp, then launches Revit 2026.
# Called by the Windows Scheduled Task. Do not run while Revit is already open.
#
# EDIT the $Models array below to match the models you want to process.

# -------------------------------------------------------------------------
# CONFIGURATION
# -------------------------------------------------------------------------
$RevitExe = "C:\Program Files\Autodesk\Revit 2026\Revit.exe"

# -------------------------------------------------------------------------
# MODELS TO PROCESS
# Edit this list. Each entry must have model_guid, project_guid, and region.
# workset_whitelist_regex : only open worksets whose name matches this pattern
# workset_blacklist_regex : never open worksets whose name matches this (overrides whitelist)
# Use "." to match everything (i.e. no effective filter).
# -------------------------------------------------------------------------
$Models = @(
    [ordered]@{
        model_guid              = "99f865fc-b79f-4d04-8105-40f15f3cbba6"
        project_guid            = "49e82fc1-fe5f-4c57-83aa-35e0b7df5406"
        region                  = "AUS"
        workset_blacklist_regex = "."
        workset_whitelist_regex = "^A-"
    },
    [ordered]@{
        model_guid              = "ff8b5406-b4ff-4efb-86c0-3d93a36971a9"
        project_guid            = "49e82fc1-fe5f-4c57-83aa-35e0b7df5406"
        region                  = "AUS"
        workset_blacklist_regex = "."
        workset_whitelist_regex = "^A-"
    },
    [ordered]@{
        model_guid              = "40061fc2-0d97-4c6a-a9c1-110319730f8c"
        project_guid            = "49e82fc1-fe5f-4c57-83aa-35e0b7df5406"
        region                  = "AUS"
        workset_blacklist_regex = "."
        workset_whitelist_regex = "^A-"
    },
    [ordered]@{
        model_guid              = "08cff244-9181-46dc-91f6-27877f9e2e8b"
        project_guid            = "49e82fc1-fe5f-4c57-83aa-35e0b7df5406"
        region                  = "AUS"
        workset_blacklist_regex = "."
        workset_whitelist_regex = "^A-"
    },
    [ordered]@{
        model_guid              = "c84fd25e-3db1-40ed-8834-3cdd29d7a3c8"
        project_guid            = "49e82fc1-fe5f-4c57-83aa-35e0b7df5406"
        region                  = "AUS"
        workset_blacklist_regex = "."
        workset_whitelist_regex = "^A-"
    }
)

# -------------------------------------------------------------------------
# MAIN
# -------------------------------------------------------------------------

# Abort if Revit is already running — a leftover process would block the batch
if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    Write-Host "Revit is already running. Aborting to avoid conflicts."
    exit 1
}

if (-not (Test-Path $RevitExe)) {
    Write-Host "Revit 2026 not found at: $RevitExe"
    exit 1
}

# Write the instruction file. Timestamp must be within 4 hours of Revit startup
# (enforced by BatchInstruction.IsValid() in the plugin).
$BatchDir  = "$env:APPDATA\ProjectPerseus"
$BatchFile = "$BatchDir\batch_task.json"

if (-not (Test-Path $BatchDir)) {
    New-Item -ItemType Directory -Path $BatchDir | Out-Null
}

$Instruction = [ordered]@{
    models_to_process = $Models
    timestamp         = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
}

$Instruction | ConvertTo-Json -Depth 4 | Set-Content -Path $BatchFile -Encoding UTF8

Write-Host "batch_task.json written to: $BatchFile"
Write-Host "Launching Revit 2026..."

Start-Process -FilePath $RevitExe
