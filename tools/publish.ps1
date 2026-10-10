# Offline portable release assembly. Does not replace the local app/bin/data.
#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$NoRestore,
    [switch]$Replace,
    [string]$AppDirectory = 'app',
    [string]$ComponentDirectory = 'bin',
    [string]$DataDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'easyclaude.root') -PathType Leaf)) {
    throw 'Missing Easy Claude root marker.'
}
if ($NoRestore -and -not $Build) { throw '-NoRestore requires -Build.' }
function Resolve-InputPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $taskRoot $Path))
}
function Get-PlainFiles([string]$Path, [string[]]$ExcludedDirectories = @()) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point refused: $Path" }
    if (-not $item.PSIsContainer) { return $item }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
        if ($child.FullName -in $ExcludedDirectories) { continue }
        Get-PlainFiles $child.FullName $ExcludedDirectories
    }
}
function Write-Utf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}
function Remove-Staging([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = (Join-Path $taskRoot 'dist') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        ((Split-Path -Leaf $full) -notmatch '^\.(release|previous)-[0-9a-f]{32}$')) { throw 'Invalid staging cleanup target.' }
    if (Test-Path -LiteralPath $full) {
        $null = @(Get-PlainFiles $full)
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
$taskComponents = Resolve-InputPath $ComponentDirectory
$taskCatalog = Get-Content -LiteralPath (Join-Path $taskRoot 'src\EasyClaude.Core\dependencies.json') -Raw | ConvertFrom-Json
$taskManifest = Get-Content -LiteralPath (Join-Path $taskComponents 'manifest.json') -Raw | ConvertFrom-Json
$taskRequired = @('easyclaude-mihomo.exe', 'easyclaude-opencode.exe', 'geo/Country.mmdb', 'geo/geoip.dat', 'geo/geosite.dat', 'geo/ASN.mmdb')
Write-Host 'Verifying the six local runtime components (no downloads)...'
foreach ($name in $taskRequired) {
    $entry = $taskManifest.Files.PSObject.Properties[$name]
    if ($null -eq $entry) { throw "Component manifest entry missing: $name" }
    $file = @(Get-PlainFiles (Join-Path $taskComponents $name))
    if ($file.Count -ne 1 -or $file[0].PSIsContainer) { throw "Component is not a file: $name" }
    $artifact = $entry.Value
    $hash = (Get-FileHash -LiteralPath $file[0].FullName -Algorithm SHA256).Hash
    if ($hash -ne $artifact.Sha256 -or $file[0].Length -ne $artifact.Length) { throw "Component integrity mismatch: $name" }
    $expected = switch ($name) {
        'easyclaude-mihomo.exe' { $taskCatalog.mihomoVersion }
        'easyclaude-opencode.exe' { $taskCatalog.openCodeVersion }
        default { $null }
    }
    if ($null -ne $expected -and $artifact.Version -ne $expected) { throw "Component version mismatch: $name" }
}
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRoot 'src\EasyClaude\EasyClaude.csproj') -Raw
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numeric three-part application version.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $taskOutput = Join-Path $taskRoot 'Releases'
} else { $taskOutput = Resolve-InputPath $OutputDirectory }
$taskRelease = $taskOutput.TrimEnd('\')
if ($taskRelease -eq $taskRoot -or $taskRoot.StartsWith($taskRelease + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output cannot replace the source workspace or its parent.'
}
if (Test-Path -LiteralPath $taskRelease) {
    if (-not $Replace) { throw "Release directory already exists. Use -Replace to update it: $taskRelease" }
    $null = @(Get-PlainFiles $taskRelease)
    foreach ($process in @(Get-Process -Name EasyClaude,easyclaude-mihomo,easyclaude-opencode -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($taskRelease + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Exit the release application from its tray before replacing it.'
        }
    }
    if (@(Get-ChildItem -LiteralPath $taskRelease -Recurse -Directory -Filter data -Force).Count -gt 0 -and -not $DataDirectory) {
        throw 'Existing personal data must be explicitly preserved with -DataDirectory before replacing this release.'
    }
}
foreach ($taskPrivateName in @('app', 'bin', 'data', '.git', '.tools')) {
    $taskPrivatePath = Join-Path $taskRoot $taskPrivateName
    if ($taskRelease -eq $taskPrivatePath -or $taskRelease.StartsWith($taskPrivatePath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release output cannot be inside a local program, component, data or tooling directory.'
    }
}
$taskLicenseRoot = Join-Path $taskRoot 'docs\third-party'
foreach ($name in @('mihomo-LICENSE.txt', 'opencode-LICENSE.txt', 'runtime-LICENSE.txt', 'runtime-THIRD-PARTY-NOTICES.txt', 'wpf-LICENSE.txt', 'winforms-LICENSE.txt', 'yamldotnet-LICENSE.txt', 'webview2-LICENSE.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskLicenseRoot $name) -PathType Leaf)) { throw "Required third-party notice missing: $name" }
}
$taskStage = Join-Path $taskRoot ('dist\.release-' + [Guid]::NewGuid().ToString('N'))
$taskBackup = Join-Path $taskRoot ('dist\.previous-' + [Guid]::NewGuid().ToString('N'))
$taskPayload = $taskStage
$taskApp = Resolve-InputPath $AppDirectory
$taskCommitted = $false
try {
    New-Item -ItemType Directory -Path $taskPayload -Force | Out-Null
    if ($Build) {
        $taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
        $taskSdk = & $taskDotnet --version
        if ($LASTEXITCODE -ne 0 -or $taskSdk -notmatch '^10\.') { throw '-Build requires the .NET 10 SDK. Default packaging uses the existing self-contained app.' }
        $taskApp = Join-Path $taskPayload 'app'
        $taskArgs = @('publish', (Join-Path $taskRoot 'src\EasyClaude\EasyClaude.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:PublishTrimmed=false', '-o', $taskApp, '--nologo')
        if ($NoRestore) { $taskArgs += '--no-restore' }
        & $taskDotnet @taskArgs
        if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
    }
    $taskExe = Join-Path $taskApp 'EasyClaude.exe'
    $taskBinaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe).ProductVersion
    if (($taskBinaryVersion -split '\+')[0] -ne $taskVersion) { throw "EXE version $taskBinaryVersion does not match project version $taskVersion. Rebuild first." }
    foreach ($name in @('EasyClaude.dll', 'EasyClaude.Core.dll', 'EasyClaude.deps.json', 'EasyClaude.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'Microsoft.Web.WebView2.Wpf.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskApp $name) -PathType Leaf)) { throw "Self-contained application file missing: $name" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $taskApp 'EasyClaude.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (@($runtime.runtimeOptions.includedFrameworks).Count -ne 2) { throw 'Expected self-contained Core and Windows Desktop frameworks.' }
    $taskFiles = @(Get-PlainFiles $taskApp)
    $taskAppPrefix = $taskApp.TrimEnd('\') + '\'
    foreach ($file in $taskFiles) {
        $relative = $file.FullName.Substring($taskAppPrefix.Length)
        if ($file.Extension -notin @('.exe', '.dll', '.json', '.xml') -or
            ($file.Extension -eq '.json' -and $relative -notin @('EasyClaude.deps.json', 'EasyClaude.runtimeconfig.json'))) {
            throw "Unexpected file in app output; inspect it before packaging: $relative"
        }
        if ($relative -eq 'EasyClaude.exe') { continue }
        if (-not $Build) {
            $target = Join-Path (Join-Path $taskPayload 'app') $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    $taskPublicFiles = [ordered]@{}
    $taskCpu = 'x64'
    foreach ($name in $taskRequired) {
        $target = Join-Path (Join-Path $taskPayload 'app\bin') $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskComponents $name) -Destination $target
        $original = $taskManifest.Files.PSObject.Properties[$name].Value
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($hash -ne $original.Sha256) { throw "Component changed while copying: $name" }
        $source = [string]$original.Source
        if ($source -match '^https://[^\s]+$') {
            $uri = [Uri]$source
            if ($uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw "Unexpected component source URL: $name" }
        } else {
            # Retain honest local provenance without shipping usernames or absolute paths.
            $source = if ($name -eq 'easyclaude-opencode.exe') { 'bundled-local:OpenCode Desktop CLI; upstream npm integrity not independently checked' }
                else { 'bundled-local:Clash Verge Geo data; upstream digest not independently checked' }
        }
        if ($source -match 'amd64-v3') { $taskCpu = 'x64 with AVX2 (bundled mihomo amd64-v3)' }
        $info = Get-Item -LiteralPath $target
        $taskPublicFiles[$name] = [ordered]@{ Sha256 = $hash; Length = $info.Length; LastWriteUtc = $info.LastWriteTimeUtc; Source = $source; Version = $original.Version }
    }
    Write-Utf8 (Join-Path $taskPayload 'app\bin\manifest.json') ([ordered]@{ Files = $taskPublicFiles } | ConvertTo-Json -Depth 6)
    # Keep the runtime root marker inside app, so automatic root discovery cannot reach the source workspace.
    Write-Utf8 (Join-Path $taskPayload 'app\easyclaude.root') 'Easy Claude runtime root.'
    foreach ($file in @(Get-PlainFiles $taskLicenseRoot)) {
        $relative = $file.FullName.Substring($taskLicenseRoot.Length + 1)
        $target = Join-Path (Join-Path $taskPayload 'app\licenses') $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    # The standard .NET apphost loads app/EasyClaude.dll directly in the same process.
    # This preserves restart/firewall process identity and requires no VBS or extra launcher process.
    $taskDotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    $taskSdkDir = Get-ChildItem -LiteralPath (Join-Path $taskDotnetRoot 'sdk') -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Microsoft.NET.HostModel.dll') } |
        Sort-Object { [version](($_.Name -split '-')[0]) } -Descending | Select-Object -First 1
    $taskHostPack = Get-ChildItem -LiteralPath (Join-Path $taskDotnetRoot 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory |
        Sort-Object { [version](($_.Name -split '-')[0]) } -Descending | Select-Object -First 1
    if ($null -eq $taskSdkDir -or $null -eq $taskHostPack) { throw 'A local Windows x64 .NET SDK/host pack is required to generate the direct EXE entry point.' }
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $taskSdkDir.FullName 'Microsoft.NET.HostModel.dll'))
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost(
        (Join-Path $taskHostPack.FullName 'runtimes\win-x64\native\apphost.exe'),
        (Join-Path $taskPayload 'EasyClaude.exe'), 'app\EasyClaude.dll', $true, $taskExe, $false, $false, $null)
    if (Test-Path -LiteralPath (Join-Path $taskPayload 'app\EasyClaude.exe')) {
        # Source builds emit an apphost next to the DLL; only the root entry point is retained.
        Remove-Item -LiteralPath (Join-Path $taskPayload 'app\EasyClaude.exe')
    }
    Write-Host 'Checking the direct EXE entry point in an isolated copy...'
    $taskValidation = & (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $taskPayload
    Write-Host ('Passed: {0} WPF checks, {1} bundled components.' -f $taskValidation.uiChecks, $taskValidation.componentsVerified)
    if ($DataDirectory) {
        $taskDataSource = Resolve-InputPath $DataDirectory
        if (-not (Test-Path -LiteralPath $taskDataSource -PathType Container)) { throw '-DataDirectory must name a personal data directory.' }
        # Runtime homes, browser caches, logs and temporary files are recreated at the destination.
        $taskRegenerated = @('webview2', 'mihomo', 'logs', 'tmp') | ForEach-Object { Join-Path $taskDataSource $_ }
        $taskDataFiles = @(Get-PlainFiles $taskDataSource $taskRegenerated)
        $taskDataTarget = Join-Path $taskPayload 'app\data'
        New-Item -ItemType Directory -Path $taskDataTarget -Force | Out-Null
        Set-Acl -LiteralPath $taskDataTarget -AclObject (Get-Acl -LiteralPath $taskDataSource)
        foreach ($file in $taskDataFiles) {
            $relative = $file.FullName.Substring($taskDataSource.TrimEnd('\').Length + 1)
            $target = Join-Path $taskDataTarget $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { throw 'Personal data changed during migration; original release preserved.' }
        }
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskRelease) -Force | Out-Null
    if (Test-Path -LiteralPath $taskRelease) { Move-Item -LiteralPath $taskRelease -Destination $taskBackup }
    try { Move-Item -LiteralPath $taskStage -Destination $taskRelease }
    catch {
        if ((Test-Path -LiteralPath $taskBackup) -and -not (Test-Path -LiteralPath $taskRelease)) { Move-Item -LiteralPath $taskBackup -Destination $taskRelease }
        throw
    }
    $taskCommitted = $true
    Write-Output ([PSCustomObject]@{ Version = $taskVersion; Executable = (Join-Path $taskRelease 'EasyClaude.exe'); RuntimeFolder = (Join-Path $taskRelease 'app'); Cpu = $taskCpu; PersonalDataPreserved = [bool]$DataDirectory; Built = [bool]$Build })
} finally {
    Remove-Staging $taskStage
    if ($taskCommitted) { Remove-Staging $taskBackup }
}
