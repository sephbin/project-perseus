# build-commit.ps1
# Called by the ProjectPerseus post-build target after every successful Release build.
# Commits all staged changes with the Claude session notes as the body, then pushes.
# The .claude_changes.md file is cleared after a successful commit so the next
# Claude session starts with a clean slate.

param(
    [string]$RepoRoot,
    [string]$Configuration
)

if ($Configuration -ne "Release") {
    Write-Host "[build-commit] Skipping — Debug build."
    exit 0
}

Set-Location $RepoRoot

# Nothing to do if the working tree is clean
$status = & git status --porcelain 2>&1
if ([string]::IsNullOrWhiteSpace($status)) {
    Write-Host "[build-commit] Nothing to commit — working tree clean."
    exit 0
}

# Read Claude session notes (may be empty for manual edits)
$changesFile = Join-Path $RepoRoot ".claude_changes.md"
$changesBody = ""
if (Test-Path $changesFile) {
    $changesBody = (Get-Content $changesFile -Raw -Encoding UTF8).Trim()
}

# Extract version from AssemblyInfo
$version = "unknown"
$assemblyInfo = Join-Path $RepoRoot "revit_plugin\src\ProjectPerseus\Properties\AssemblyInfo.cs"
if (Test-Path $assemblyInfo) {
    $match = Select-String -Path $assemblyInfo -Pattern 'AssemblyVersion\("([^"]+)"\)'
    if ($match) { $version = $match.Matches[0].Groups[1].Value }
}

# Build the commit message
$bodyText = if ($changesBody) { $changesBody } else { "Manual build — no Claude session notes recorded." }

$commitMessage = "Build $version

$bodyText

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"

# Write message to a temp file so multiline content is passed safely
$msgFile = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($msgFile, $commitMessage, [System.Text.Encoding]::UTF8)

    & git add -A
    & git commit -F $msgFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[build-commit] git commit failed."
        exit 1
    }

    & git push
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[build-commit] git push failed."
        exit 1
    }

    # Clear the changes file — Claude will repopulate it next session
    [System.IO.File]::WriteAllText($changesFile, "", [System.Text.Encoding]::UTF8)
    Write-Host "[build-commit] Committed and pushed v$version. Changes log cleared."
}
finally {
    Remove-Item $msgFile -Force -ErrorAction SilentlyContinue
}
