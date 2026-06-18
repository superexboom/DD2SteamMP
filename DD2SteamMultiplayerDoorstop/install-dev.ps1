param(
    [string]$GameDir = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot "Directory.Build.props")
    $managedDir = $props.Project.PropertyGroup.ManagedDir
    $dataDir = Split-Path -Parent $managedDir
    $GameDir = Split-Path -Parent $dataDir
}

$modDir = Join-Path $GameDir "DD2SteamMP"
$doorstopProject = Join-Path $projectRoot "DD2SteamMultiplayerDoorstop\DD2SteamMultiplayerDoorstop.csproj"
$hostProject = Join-Path $projectRoot "DD2SteamMultiplayerHost\DD2SteamMultiplayerHost.csproj"
$doorstopOut = Join-Path $projectRoot "DD2SteamMultiplayerDoorstop\bin\$Configuration\net48"
$hostOut = Join-Path $projectRoot "DD2SteamMultiplayerHost\bin\$Configuration\net48"
$configPath = Join-Path $GameDir "doorstop_config.ini"
$backupPath = Join-Path $GameDir "doorstop_config.ini.dd2steammp.bak"

function Invoke-CheckedBuild([string]$ProjectPath) {
    & dotnet build $ProjectPath -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $ProjectPath"
    }
}

function Get-RegexValue([string]$Path, [string]$Pattern, [string]$Description) {
    $text = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($text, $Pattern)
    if (!$match.Success) {
        throw "Could not read $Description from $Path"
    }

    return $match.Groups[1].Value
}

if (!$NoBuild) {
    Invoke-CheckedBuild $doorstopProject
    Invoke-CheckedBuild $hostProject
}

New-Item -ItemType Directory -Force -Path $modDir | Out-Null

Copy-Item -Force -LiteralPath (Join-Path $doorstopOut "DD2SteamMultiplayerDoorstop.dll") -Destination $modDir
Copy-Item -Force -LiteralPath (Join-Path $doorstopOut "DD2SteamMultiplayerDoorstop.pdb") -Destination $modDir
Copy-Item -Force -LiteralPath (Join-Path $hostOut "DD2SteamMultiplayerHost.dll") -Destination $modDir
Copy-Item -Force -LiteralPath (Join-Path $hostOut "DD2SteamMultiplayerHost.pdb") -Destination $modDir
Copy-Item -Force -LiteralPath (Join-Path $hostOut "DD2DebugDemoCore.dll") -Destination $modDir
Copy-Item -Force -LiteralPath (Join-Path $hostOut "DD2DebugDemoCore.pdb") -Destination $modDir

$protocolVersion = Get-RegexValue `
    (Join-Path $projectRoot "DD2SteamMultiplayerHost\Protocol\MultiplayerProtocol.cs") `
    'CurrentVersion\s*=\s*(\d+)' `
    "protocol version"
$componentVersion = Get-RegexValue `
    (Join-Path $projectRoot "DD2SteamMultiplayerHost\SteamLobbyClient.cs") `
    'LocalLobbyVersion\s*=\s*"([^"]+)"' `
    "component version"
$manifest = [ordered]@{
    packageName = "DD2SteamMP"
    componentVersion = $componentVersion
    protocolVersion = [int]$protocolVersion
    steamAppId = 1940340
    configuration = $Configuration
    builtAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    files = @(
        "DD2SteamMultiplayerDoorstop.dll",
        "DD2SteamMultiplayerHost.dll",
        "DD2DebugDemoCore.dll",
        "DD2SteamMultiplayerDoorstop.pdb",
        "DD2SteamMultiplayerHost.pdb",
        "DD2DebugDemoCore.pdb"
    )
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $modDir "dd2steammp_manifest.json") -Encoding UTF8

if (!(Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $configPath -Destination $backupPath
}

$lines = Get-Content -LiteralPath $configPath
$targetSet = $false
$enabledSet = $false
$lines = $lines | ForEach-Object {
    if ($_ -match "^target_assembly\s*=") {
        $targetSet = $true
        "target_assembly=DD2SteamMP\DD2SteamMultiplayerDoorstop.dll"
    } elseif ($_ -match "^\s*enabled\s*=") {
        $enabledSet = $true
        "enabled = true"
    } else {
        $_
    }
}

if (!$targetSet) {
    $lines += "target_assembly=DD2SteamMP\DD2SteamMultiplayerDoorstop.dll"
}

if (!$enabledSet) {
    $lines += "enabled = true"
}

Set-Content -LiteralPath $configPath -Value $lines -Encoding ASCII

Write-Host "Installed DD2SteamMP Doorstop host to: $modDir"
Write-Host "Original config backup: $backupPath"
