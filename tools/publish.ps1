param([string]$OutputDirectory = 'app', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'easyclaude.root'))) { throw 'Missing Easy Claude root marker.' }
$taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskPrefix = $taskRoot.TrimEnd('\') + '\'
if ($taskOutput -ne (Join-Path $taskRoot 'app') -and
    -not $taskOutput.StartsWith((Join-Path $taskRoot '.tools') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be app or a staging program directory inside .tools.'
}
$taskExe = Join-Path $taskOutput 'EasyClaude.exe'
foreach ($taskProcess in @(Get-Process -Name EasyClaude -ErrorAction SilentlyContinue)) {
    if ($taskProcess.Path -eq $taskExe) { throw 'Exit Easy Claude from its tray menu before publishing.' }
}
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) {
    $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$taskSdk = & $taskDotnet --version
if ($LASTEXITCODE -ne 0 -or $taskSdk -notmatch '^10\.') { throw 'Publishing requires the .NET 10 SDK.' }
$taskStage = Join-Path $taskRoot ('.tools\publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
$taskPublishArgs = @('publish', (Join-Path $taskRoot 'src\EasyClaude\EasyClaude.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:NuGetAudit=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:PublishTrimmed=false', '-o', $taskStage, '--nologo')
if ($NoRestore) { $taskPublishArgs += '--no-restore' }
& $taskDotnet @taskPublishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE); existing program and data preserved. Staging: $taskStage" }
$taskBackup = Join-Path $taskRoot ('.tools\previous-app-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $taskOutput) { Move-Item -LiteralPath $taskOutput -Destination $taskBackup }
try { Move-Item -LiteralPath $taskStage -Destination $taskOutput }
catch {
    if (Test-Path -LiteralPath $taskBackup) { Move-Item -LiteralPath $taskBackup -Destination $taskOutput }
    throw
}
Write-Output "Published: $taskExe"
Write-Output 'The program includes .NET 10. WebView2 uses the system runtime; data and components were preserved.'
