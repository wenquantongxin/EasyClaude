# Validate the minimal direct-EXE release in a disposable copy. No persistent reports are created.
#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Directory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Releases'),
    [switch]$Lifecycle,
    [string]$ProfilePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$taskSource = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
if ($Lifecycle -and (-not $ProfilePath -or -not (Test-Path -LiteralPath $ProfilePath -PathType Leaf))) {
    throw '-Lifecycle requires a YAML node profile via -ProfilePath. Production keys and sessions are never copied.'
}
$taskCheckRoot = Join-Path $taskRoot ('dist\.verify-' + [Guid]::NewGuid().ToString('N'))
$taskRuntime = Join-Path $taskCheckRoot 'app'
$taskReport = [ordered]@{ ok = $false; filesCopiedAndVerified = 0; componentsVerified = 0; uiChecks = 0; lifecycleRequested = [bool]$Lifecycle }
function Get-PlainFiles([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point in release input.' }
    if (-not $item.PSIsContainer) { return $item }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
        # Personal data is excluded before descending, including cached files and stored credentials.
        if ($child.FullName -eq (Join-Path $taskSource 'app\data')) { continue }
        Get-PlainFiles $child.FullName
    }
}
function Wait-CheckFile([string]$Path, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out waiting for $([IO.Path]::GetFileName($Path))." }
        Start-Sleep -Milliseconds 250
    }
}
try {
    $taskTop = @(Get-ChildItem -LiteralPath $taskSource -Force)
    if ($taskTop.Count -ne 2 -or -not (Test-Path -LiteralPath (Join-Path $taskSource 'EasyClaude.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $taskSource 'app') -PathType Container)) { throw 'Release root must contain only EasyClaude.exe and app.' }
    New-Item -ItemType Directory -Path $taskCheckRoot -Force | Out-Null
    foreach ($file in @(Get-PlainFiles $taskSource)) {
        $relative = $file.FullName.Substring($taskSource.Length + 1)
        if ($file.Name -in @('README.txt', 'README.md', 'SHA256SUMS.txt', 'ACCEPTANCE.json', 'RELEASE-NOTES.md', 'COMPONENTS.md', 'release.json') -or
            $file.Extension -in @('.vbs', '.lnk', '.zip', '.dpapi', '.db', '.log', '.yml', '.yaml')) { throw "Unexpected release artifact: $relative" }
        $target = Join-Path $taskCheckRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw "File changed while copying: $relative" }
        $taskReport.filesCopiedAndVerified++
    }
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskRuntime 'bin\manifest.json') -Raw | ConvertFrom-Json
    $taskRequired = @('easyclaude-mihomo.exe', 'easyclaude-opencode.exe', 'geo/Country.mmdb', 'geo/geoip.dat', 'geo/geosite.dat', 'geo/ASN.mmdb')
    foreach ($name in $taskRequired) {
        $entry = $taskManifest.Files.PSObject.Properties[$name]
        if ($null -eq $entry -or (Get-FileHash -LiteralPath (Join-Path $taskRuntime ('bin/' + $name)) -Algorithm SHA256).Hash -ne $entry.Value.Sha256) { throw "Bundled component mismatch: $name" }
        $taskReport.componentsVerified++
    }
    $taskExe = Join-Path $taskCheckRoot 'EasyClaude.exe'
    $taskReport.version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe).ProductVersion -split '\+')[0]
    $taskUi = Join-Path $taskRuntime 'data\tmp\ui-check'
    Write-Host 'Running direct-EXE WPF checks with automatic runtime root discovery...'
    # Deliberately omit --root: this validates double-click startup under the source checkout as well as after relocation.
    $taskProcess = Start-Process -FilePath $taskExe -ArgumentList @('--ui-check', ('"' + $taskUi + '"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
    if (-not $taskProcess.WaitForExit(30000)) { throw 'UI checks timed out.' }
    if (Test-Path -LiteralPath (Join-Path $taskUi 'error.txt')) { throw 'WPF checks failed.' }
    $taskUiResults = @(Get-Content -LiteralPath (Join-Path $taskUi 'results.json') -Raw | ConvertFrom-Json)
    if ($taskUiResults.Count -eq 0 -or @($taskUiResults | Where-Object { -not $_.ok }).Count -gt 0) { throw 'WPF checks failed.' }
    $taskReport.uiChecks = $taskUiResults.Count
    if ($Lifecycle) {
        Write-Host 'Testing real services, WebView2, restart and exit with isolated fake credentials...'
        $taskProfileDirectory = Join-Path $taskRuntime 'data\profiles'
        New-Item -ItemType Directory -Path $taskProfileDirectory -Force | Out-Null
        Copy-Item -LiteralPath $ProfilePath -Destination (Join-Path $taskProfileDirectory 'profile.yml')
        $taskResult = Join-Path $taskRuntime 'data\tmp\lifecycle.json'
        $null = Start-Process -FilePath $taskExe -ArgumentList @('--lifecycle-check', ('"' + $taskResult + '"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
        Wait-CheckFile $taskResult 150
        $taskLifecycle = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json
        if (-not $taskLifecycle.ok) { throw "Lifecycle check failed: $($taskLifecycle | ConvertTo-Json -Compress)" }
        $taskReport.lifecycle = $taskLifecycle
    }
    $taskReport.ok = $true
    Write-Output ([PSCustomObject]$taskReport)
} finally {
    foreach ($taskProcess in @(Get-Process -Name EasyClaude,easyclaude-mihomo,easyclaude-opencode -ErrorAction SilentlyContinue)) {
        if ($taskProcess.Path -and $taskProcess.Path.StartsWith($taskCheckRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            if (-not $taskProcess.WaitForExit(5000)) { Stop-Process -Id $taskProcess.Id -Force; $null = $taskProcess.WaitForExit(5000) }
        }
    }
    $full = [IO.Path]::GetFullPath($taskCheckRoot)
    if (-not $full.StartsWith((Join-Path $taskRoot 'dist') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $full) -notmatch '^\.verify-[0-9a-f]{32}$') { throw 'Invalid verification cleanup target.' }
    if (Test-Path -LiteralPath $full) {
        if (@(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) { throw 'Reparse point in verification cleanup target.' }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
