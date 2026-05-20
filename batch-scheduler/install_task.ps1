# install_task.ps1
# Run this ONCE on the target machine to register the Perseus batch task
# with Windows Task Scheduler. Must be run as the user who will run Revit
# (NOT as Administrator — Revit needs an interactive user session).
#
# Usage:
#   .\install_task.ps1                      # schedule at default time (22:00)
#   .\install_task.ps1 -At "06:00"          # schedule at 6 AM
#   .\install_task.ps1 -At "22:00" -Remove  # remove the task

param(
    [string]$At     = "22:00",
    [switch]$Remove
)

$TaskName      = "Perseus Batch Sync"
$LaunchScript  = "$PSScriptRoot\launch_batch.ps1"

# --- Remove existing task ---
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

if ($Remove) {
    Write-Host "Task '$TaskName' removed."
    exit 0
}

# --- Validate ---
if (-not (Test-Path $LaunchScript)) {
    Write-Error "launch_batch.ps1 not found at: $LaunchScript"
    exit 1
}

# --- Build the task ---

# Trigger: every day at $At
$Trigger = New-ScheduledTaskTrigger -Daily -At $At

# Action: run the launch script as a non-interactive PowerShell process
$Action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$LaunchScript`""

# Settings
$Settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Hours 8) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -MultipleInstances   IgnoreNew

# Register as the current interactive user.
# RunLevel=Limited means it runs in the user's normal (non-elevated) session,
# which is required for Revit to display its UI.
Register-ScheduledTask `
    -TaskName  $TaskName `
    -Trigger   $Trigger `
    -Action    $Action `
    -Settings  $Settings `
    -RunLevel  Limited `
    -Force

Write-Host ""
Write-Host "Task '$TaskName' registered successfully."
Write-Host "  Schedule : daily at $At"
Write-Host "  Script   : $LaunchScript"
Write-Host ""
Write-Host "To test immediately:"
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'"
Write-Host ""
Write-Host "To view in Task Scheduler UI:"
Write-Host "  taskschd.msc"
