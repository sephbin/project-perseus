# launch_batch.ps1
# Writes the Perseus batch instruction file (or resumes an existing one) and
# loops launching Revit until the plugin reports the batch is fully done.
#
# Called by the Windows Scheduled Task. Do not run while Revit is already open.
#
# Survival loop: the plugin's BatchProcessor exits Revit with code 1 when a
# cloud-model GetUserWorksetInfo poisons the session (and there's more work
# pending), and code 0 when the batch is genuinely complete. This script keeps
# relaunching Revit until batch_task.json is deleted by the plugin (exit 0) or
# the relaunch cap is hit.
#
# EDIT the $Models array below to match the models you want to process.

# -------------------------------------------------------------------------
# CONFIGURATION
# -------------------------------------------------------------------------
$RevitExe       = "C:\Program Files\Autodesk\Revit 2026\Revit.exe"
$MaxRelaunches  = 20                # safety cap so a persistently broken model can't loop forever
$RelaunchDelaySec = 5               # breathing room between Revit launches
$SessionIterationCap = $null        # iterations per Revit session: $null=default (50), 0=unlimited, N=custom

# -------------------------------------------------------------------------
# MODELS TO PROCESS
# Only used when batch_task.json doesn't exist OR has zero pending models.
# If a previous run left the file with pending work, that takes priority and
# this list is ignored for that session (so we don't clobber crash-survival state).
#
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

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    Write-Host "Revit is already running. Aborting to avoid conflicts."
    exit 1
}

if (-not (Test-Path $RevitExe)) {
    Write-Host "Revit 2026 not found at: $RevitExe"
    exit 1
}

$BatchDir  = "$env:APPDATA\ProjectPerseus"
$BatchFile = "$BatchDir\batch_task.json"

if (-not (Test-Path $BatchDir)) {
    New-Item -ItemType Directory -Path $BatchDir | Out-Null
}

# Resume vs. fresh: if batch_task.json exists with pending models, the plugin
# left work unfinished from a prior crash/poison restart — pick up where it
# stopped instead of clobbering the queue.
$resumeExisting = $false
if (Test-Path $BatchFile) {
    try {
        $existing  = Get-Content $BatchFile -Raw | ConvertFrom-Json
        $remaining = @($existing.models_to_process).Count
        if ($remaining -gt 0) {
            Write-Host "Resuming existing batch_task.json - $remaining model(s) pending."
            $resumeExisting = $true
        } else {
            Write-Host "Existing batch_task.json has zero models. Overwriting with fresh batch."
        }
    } catch {
        Write-Host "Could not parse existing batch_task.json ($_). Overwriting with fresh batch."
    }
}

if (-not $resumeExisting) {
    $Instruction = [ordered]@{
        models_to_process = $Models
        timestamp         = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
    }
    if ($null -ne $SessionIterationCap) {
        $Instruction.session_iteration_cap = $SessionIterationCap
    }
    $Instruction | ConvertTo-Json -Depth 4 | Set-Content -Path $BatchFile -Encoding UTF8
    Write-Host "Fresh batch_task.json written: $BatchFile ($($Models.Count) model(s))."
}

# Survival loop: launch Revit, wait, decide based on exit code.
$relaunch = 0
while ($relaunch -lt $MaxRelaunches) {
    $relaunch++
    Write-Host ""
    Write-Host "=== Revit launch $relaunch of $MaxRelaunches ==="
    Write-Host "Starting Revit and waiting for exit..."

    $proc = Start-Process -FilePath $RevitExe -PassThru
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
    Write-Host "Revit exited with code $exitCode."

    # Plugin signals "all done" by exiting 0 AND deleting batch_task.json.
    # We trust the file's presence over the code since a native crash might
    # not set a clean exit code at all.
    if (-not (Test-Path $BatchFile)) {
        Write-Host "batch_task.json is gone. Batch complete."
        exit 0
    }

    # File still exists - more work pending (or plugin couldn't delete it for some reason).
    if ($exitCode -eq 0) {
        Write-Host "Exit 0 but batch_task.json still exists. Treating as complete and stopping to avoid loop."
        exit 0
    }

    Write-Host "More work pending. Relaunching in $RelaunchDelaySec second(s)..."
    Start-Sleep -Seconds $RelaunchDelaySec
}

Write-Host "Hit max relaunches ($MaxRelaunches). Stopping launcher; investigate batch_task.json manually."
exit 1
