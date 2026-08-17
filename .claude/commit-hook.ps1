# Fires on Stop. Bumps version + commits if plugin source files changed this session.
$pluginChanges = & git -C 'E:\mydev\project-perseus' status --porcelain -- 'revit_plugin/src/ProjectPerseus' 2>&1
if ([string]::IsNullOrWhiteSpace($pluginChanges)) { exit 0 }

& powershell -NonInteractive -ExecutionPolicy Bypass -File 'E:\mydev\project-perseus\bump-version.ps1'
if ($LASTEXITCODE -ne 0) { Write-Host '[commit-hook] bump-version failed'; exit 1 }

& powershell -NonInteractive -ExecutionPolicy Bypass -File 'E:\mydev\project-perseus\build-commit.ps1' `
    -RepoRoot 'E:\mydev\project-perseus' `
    -Configuration 'Debug 2026'
