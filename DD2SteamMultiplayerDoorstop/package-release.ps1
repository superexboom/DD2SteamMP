param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutDir = "artifacts",
    [switch]$NoBuild,
    [switch]$NoPdb
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$packageTemplateDir = Join-Path $PSScriptRoot "ReleasePackage"
$doorstopProject = Join-Path $projectRoot "DD2SteamMultiplayerDoorstop\DD2SteamMultiplayerDoorstop.csproj"
$hostProject = Join-Path $projectRoot "DD2SteamMultiplayerHost\DD2SteamMultiplayerHost.csproj"
$doorstopOut = Join-Path $projectRoot "DD2SteamMultiplayerDoorstop\bin\$Configuration\net48"
$hostOut = Join-Path $projectRoot "DD2SteamMultiplayerHost\bin\$Configuration\net48"

function Get-RegexValue([string]$Path, [string]$Pattern, [string]$Description) {
    $text = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($text, $Pattern)
    if (!$match.Success) {
        throw "Could not read $Description from $Path"
    }

    return $match.Groups[1].Value
}

function Invoke-CheckedBuild([string]$ProjectPath) {
    & dotnet build $ProjectPath -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $ProjectPath"
    }
}

function Assert-SafePackagePath([string]$Path, [string]$RequiredParent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($RequiredParent)
    if (!$fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside package output root: $fullPath"
    }

    return $fullPath
}

if (!(Test-Path -LiteralPath $packageTemplateDir)) {
    throw "Release package template folder was not found: $packageTemplateDir"
}

if (!$NoBuild) {
    Invoke-CheckedBuild $doorstopProject
    Invoke-CheckedBuild $hostProject
}

$protocolVersion = Get-RegexValue `
    (Join-Path $projectRoot "DD2SteamMultiplayerHost\Protocol\MultiplayerProtocol.cs") `
    'CurrentVersion\s*=\s*(\d+)' `
    "protocol version"
$componentVersion = Get-RegexValue `
    (Join-Path $projectRoot "DD2SteamMultiplayerHost\SteamLobbyClient.cs") `
    'LocalLobbyVersion\s*=\s*"([^"]+)"' `
    "component version"

$outRoot = if ([System.IO.Path]::IsPathRooted($OutDir)) {
    $OutDir
} else {
    Join-Path $projectRoot $OutDir
}

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null
$outRoot = (Resolve-Path -LiteralPath $outRoot).Path

$stageRoot = Assert-SafePackagePath (Join-Path $outRoot "DD2SteamMP-$componentVersion") $outRoot
$payloadDir = Join-Path $stageRoot "DD2SteamMP"
$zipPath = Assert-SafePackagePath (Join-Path $outRoot "DD2SteamMP-$componentVersion.zip") $outRoot

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

Copy-Item -Force -LiteralPath (Join-Path $packageTemplateDir "Install-DD2SteamMP.ps1") -Destination $stageRoot
Copy-Item -Force -LiteralPath (Join-Path $packageTemplateDir "Uninstall-DD2SteamMP.ps1") -Destination $stageRoot
Copy-Item -Force -LiteralPath (Join-Path $packageTemplateDir "Check-DD2SteamMP.ps1") -Destination $stageRoot
Copy-Item -Force -LiteralPath (Join-Path $packageTemplateDir "README.md") -Destination $stageRoot

$runtimeFiles = @(
    @{ Source = Join-Path $doorstopOut "DD2SteamMultiplayerDoorstop.dll"; Name = "DD2SteamMultiplayerDoorstop.dll" },
    @{ Source = Join-Path $hostOut "DD2SteamMultiplayerHost.dll"; Name = "DD2SteamMultiplayerHost.dll" },
    @{ Source = Join-Path $hostOut "DD2DebugDemoCore.dll"; Name = "DD2DebugDemoCore.dll" }
)

if (!$NoPdb) {
    $runtimeFiles += @(
        @{ Source = Join-Path $doorstopOut "DD2SteamMultiplayerDoorstop.pdb"; Name = "DD2SteamMultiplayerDoorstop.pdb" },
        @{ Source = Join-Path $hostOut "DD2SteamMultiplayerHost.pdb"; Name = "DD2SteamMultiplayerHost.pdb" },
        @{ Source = Join-Path $hostOut "DD2DebugDemoCore.pdb"; Name = "DD2DebugDemoCore.pdb" }
    )
}

foreach ($file in $runtimeFiles) {
    if (!(Test-Path -LiteralPath $file.Source)) {
        throw "Build output is missing: $($file.Source)"
    }

    Copy-Item -Force -LiteralPath $file.Source -Destination (Join-Path $payloadDir $file.Name)
}

$manifest = [ordered]@{
    packageName = "DD2SteamMP"
    componentVersion = $componentVersion
    protocolVersion = [int]$protocolVersion
    steamAppId = 1940340
    configuration = $Configuration
    builtAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    files = @($runtimeFiles | ForEach-Object { $_.Name })
}

$manifestPath = Join-Path $stageRoot "dd2steammp_manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Copy-Item -Force -LiteralPath $manifestPath -Destination (Join-Path $payloadDir "dd2steammp_manifest.json")

Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -Force

Write-Host "Package staged at: $stageRoot"
Write-Host "Package zip: $zipPath"
Write-Host "Component version: $componentVersion"
Write-Host "Protocol version: $protocolVersion"
