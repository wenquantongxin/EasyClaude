# One-time move of Easy Claude data from %LOCALAPPDATA%\EasyClaude (old layout) into <root>\data, then removes the old folder.
# Migrated: settings.json, egress.json, profiles\ (DPAPI-encrypted profile), opencode\ (isolated OpenCode: API key, sessions), logs\.
# Not migrated (regenerated): bin\ (re-vendored), mihomo\ (runtime home), webview2\ (browser cache).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'easyclaude.root'))) { throw "not an Easy Claude root: $root" }
if (Get-Process -Name 'EasyClaude', 'easyclaude-mihomo', 'easyclaude-opencode', 'EasyClaude.Smoke' -ErrorAction SilentlyContinue) {
    throw 'Easy Claude is running; exit it first (tray > 退出).'
}
$old = Join-Path $env:LOCALAPPDATA 'EasyClaude'
if (-not (Test-Path $old)) { 'nothing to migrate'; return }

$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force -Path $data | Out-Null
# Lock data\ down BEFORE copying secrets in, so every copied file inherits the private ACL.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls $data /inheritance:r /grant:r "*${sid}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed for $data" }

foreach ($name in 'settings.json', 'egress.json', 'profiles', 'opencode', 'logs') {
    $source = Join-Path $old $name
    if (-not (Test-Path $source)) { continue }
    $target = Join-Path $data $name
    if (Test-Path $target) { throw "target already exists, refusing to overwrite: $target" }
    Copy-Item $source $target -Recurse   # cross-volume: copy (inherits data\ ACL), verify, then delete the source
    $sourceCount = @(Get-ChildItem $source -Recurse -File -Force).Count + [int](Test-Path $source -PathType Leaf)
    $targetCount = @(Get-ChildItem $target -Recurse -File -Force).Count + [int](Test-Path $target -PathType Leaf)
    if ($sourceCount -ne $targetCount) { throw "copy verification failed for $name ($sourceCount vs $targetCount files)" }
    "migrated $name ($targetCount files)"
}
Remove-Item $old -Recurse -Force
"removed $old"
