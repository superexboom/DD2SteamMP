param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot "Directory.Build.props")
    $managedDir = $props.Project.PropertyGroup.ManagedDir
    $dataDir = Split-Path -Parent $managedDir
    $GameDir = Split-Path -Parent $dataDir
}

$configPath = Join-Path $GameDir "doorstop_config.ini"
$backupPath = Join-Path $GameDir "doorstop_config.ini.dd2steammp.bak"

if (!(Test-Path -LiteralPath $backupPath)) {
    throw "Backup was not found: $backupPath"
}

Copy-Item -Force -LiteralPath $backupPath -Destination $configPath
Write-Host "Restored Doorstop config from: $backupPath"
