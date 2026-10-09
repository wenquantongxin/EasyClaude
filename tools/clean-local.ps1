# Cleans only this Easy Claude checkout. Preview by default; use -Execute to delete.
param([switch]$Execute, [switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$taskRootPrefix = $taskRoot + '\'
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'easyclaude.root') -PathType Leaf)) {
    throw 'Missing Easy Claude root marker.'
}
foreach ($taskProcess in @(Get-Process -Name EasyClaude,easyclaude-mihomo,easyclaude-opencode,EasyClaude.Smoke -ErrorAction SilentlyContinue)) {
    if ($taskProcess.Path -and $taskProcess.Path.StartsWith($taskRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Exit this Easy Claude installation from the tray before cleanup.'
    }
}
$taskRelativeTargets = @(
    '.tools', '.vs', 'TestResults', 'dist',
    'src\EasyClaude\bin', 'src\EasyClaude\obj',
    'src\EasyClaude.Core\bin', 'src\EasyClaude.Core\obj',
    'tests\EasyClaude.Core.Tests\bin', 'tests\EasyClaude.Core.Tests\obj',
    'tools\EasyClaude.Smoke\bin', 'tools\EasyClaude.Smoke\obj',
    'data\opencode', 'data\webview2', 'data\logs', 'data\tmp', 'data\mihomo', 'data\egress.json', 'data\api-key-test.json'
)
if ($BuildOnly) { $taskRelativeTargets = @($taskRelativeTargets | Where-Object { -not $_.StartsWith('data\', [StringComparison]::OrdinalIgnoreCase) }) }
$taskTotal = 0L
$taskTargets = foreach ($taskRelative in $taskRelativeTargets) {
    $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (-not $taskTarget.StartsWith($taskRootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Target outside project root.' }
    if (-not (Test-Path -LiteralPath $taskTarget)) { continue }
    # Refuse junction/symlink targets and descendants so deletion cannot follow another workspace.
    $taskItem = Get-Item -LiteralPath $taskTarget -Force
    if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse target: $taskTarget" }
    $taskChildren = if ($taskItem.PSIsContainer) { @(Get-ChildItem -LiteralPath $taskTarget -Recurse -Force) } else { @($taskItem) }
    if ($taskChildren | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
        throw "Reparse descendant: $taskTarget"
    }
    $taskBytes = [long](($taskChildren | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum)
    $taskTotal += $taskBytes
    [PSCustomObject]@{ Path = $taskTarget; Bytes = $taskBytes; GiB = [Math]::Round($taskBytes / 1GB, 3) }
}
$taskTargets | Format-Table Path,GiB -AutoSize
Write-Output ('Selected: {0:N3} GiB. Runtime app/bin, settings and encrypted node profile are preserved.' -f ($taskTotal / 1GB))
if (-not $Execute) { Write-Output 'Preview only. Run again with -Execute to delete. -BuildOnly preserves all production data.'; return }
# Stop build servers hosted by this checkout's disposable SDK (including dotnet-hosted MSBuild/Roslyn).
$taskSdkPrefix = (Join-Path $taskRoot '.tools\dotnet') + '\'
foreach ($taskBuildProcess in @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($taskSdkPrefix, [StringComparison]::OrdinalIgnoreCase)
})) {
    if ($taskBuildProcess.HasExited) { continue }
    Stop-Process -Id $taskBuildProcess.Id -Force -ErrorAction Stop
    if (-not $taskBuildProcess.WaitForExit(5000)) { throw 'Temporary SDK build process did not stop.' }
}
foreach ($taskTarget in $taskTargets) {
    # Absolute targets were validated above. Use the same PowerShell shell end-to-end.
    Remove-Item -LiteralPath $taskTarget.Path -Recurse -Force -ErrorAction Stop
}
if ($BuildOnly) { Write-Output ('Removed {0:N3} GiB of build/test files. All production data preserved.' -f ($taskTotal / 1GB)) }
else { Write-Output ('Removed {0:N3} GiB. Stored Claude key, sessions and browser cache have been erased.' -f ($taskTotal / 1GB)) }
