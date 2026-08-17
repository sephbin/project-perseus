$j = [Console]::In.ReadToEnd() | ConvertFrom-Json
$f = if ($j.tool_input.file_path) { $j.tool_input.file_path } else { $j.tool_response.filePath }
if (-not $f -or $f -notmatch 'project-perseus' -or $f -notmatch '\.(cs|csproj)$') { exit 0 }

$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$sln = 'E:\mydev\project-perseus\revit_plugin\src\ProjectPerseus.sln'
$out = & $msbuild $sln /p:Configuration=Debug /t:Build /v:minimal 2>&1 | Select-Object -Last 15 | Out-String

@{ hookSpecificOutput = @{ hookEventName = 'PostToolUse'; additionalContext = "MSBuild result:`n$out" } } | ConvertTo-Json -Compress
exit 0
