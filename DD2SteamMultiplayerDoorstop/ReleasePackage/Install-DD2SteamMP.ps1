param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

$AppId = "1940340"
$PayloadDir = Join-Path $PSScriptRoot "DD2SteamMP"

function Get-SteamLibraryRoots {
    $roots = New-Object System.Collections.Generic.List[string]

    $registryPaths = @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )

    foreach ($registryPath in $registryPaths) {
        try {
            $props = Get-ItemProperty -Path $registryPath -ErrorAction Stop
            foreach ($name in @("SteamPath", "InstallPath")) {
                $value = $props.$name
                if (![string]::IsNullOrWhiteSpace($value) -and (Test-Path -LiteralPath $value)) {
                    $roots.Add((Resolve-Path -LiteralPath $value).Path)
                }
            }
        } catch {
        }
    }

    $defaultSteam = "C:\Program Files (x86)\Steam"
    if (Test-Path -LiteralPath $defaultSteam) {
        $roots.Add((Resolve-Path -LiteralPath $defaultSteam).Path)
    }

    $allRoots = New-Object System.Collections.Generic.List[string]
    foreach ($root in ($roots | Select-Object -Unique)) {
        $allRoots.Add($root)
        $libraryFile = Join-Path $root "steamapps\libraryfolders.vdf"
        if (!(Test-Path -LiteralPath $libraryFile)) {
            continue
        }

        foreach ($line in Get-Content -LiteralPath $libraryFile) {
            if ($line -match '^\s*"path"\s*"(.+)"') {
                $path = $matches[1] -replace "\\\\", "\"
                if (Test-Path -LiteralPath $path) {
                    $allRoots.Add((Resolve-Path -LiteralPath $path).Path)
                }
            }
        }
    }

    return $allRoots | Select-Object -Unique
}

function Get-InstallDirFromManifest([string]$ManifestPath) {
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match '^\s*"installdir"\s*"(.+)"') {
            return $matches[1]
        }
    }

    return $null
}

function Find-DD2GameDir {
    foreach ($libraryRoot in Get-SteamLibraryRoots) {
        $manifest = Join-Path $libraryRoot "steamapps\appmanifest_$AppId.acf"
        if (!(Test-Path -LiteralPath $manifest)) {
            continue
        }

        $installDir = Get-InstallDirFromManifest $manifest
        if ([string]::IsNullOrWhiteSpace($installDir)) {
            continue
        }

        $candidate = Join-Path $libraryRoot "steamapps\common\$installDir"
        if (Test-Path -LiteralPath (Join-Path $candidate "Darkest Dungeon II.exe")) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Resolve-DD2GameDir([string]$InputPath) {
    if (![string]::IsNullOrWhiteSpace($InputPath)) {
        if (!(Test-Path -LiteralPath $InputPath)) {
            throw "GameDir does not exist: $InputPath"
        }

        $resolved = (Resolve-Path -LiteralPath $InputPath).Path
        if (!(Test-Path -LiteralPath (Join-Path $resolved "Darkest Dungeon II.exe"))) {
            throw "GameDir does not look like Darkest Dungeon II root: $resolved"
        }

        return $resolved
    }

    $found = Find-DD2GameDir
    if ([string]::IsNullOrWhiteSpace($found)) {
        throw "Could not find Darkest Dungeon II. Re-run with -GameDir `"F:\SteamLibrary\steamapps\common\Darkest Dungeon(R) II`" adjusted to your install path."
    }

    return $found
}

if (!(Test-Path -LiteralPath $PayloadDir)) {
    throw "Package payload folder was not found: $PayloadDir"
}

$requiredPayloadFiles = @(
    "DD2SteamMultiplayerDoorstop.dll",
    "DD2SteamMultiplayerHost.dll",
    "dd2steammp_manifest.json"
)

foreach ($fileName in $requiredPayloadFiles) {
    $path = Join-Path $PayloadDir $fileName
    if (!(Test-Path -LiteralPath $path)) {
        throw "Required package file is missing: $path"
    }
}

$resolvedGameDir = Resolve-DD2GameDir $GameDir
$configPath = Join-Path $resolvedGameDir "doorstop_config.ini"
$backupPath = Join-Path $resolvedGameDir "doorstop_config.ini.dd2steammp.bak"
$targetModDir = Join-Path $resolvedGameDir "DD2SteamMP"

if (!(Test-Path -LiteralPath $configPath)) {
    throw "doorstop_config.ini was not found in $resolvedGameDir. Install BepInEx/Doorstop first, then run this installer again."
}

New-Item -ItemType Directory -Force -Path $targetModDir | Out-Null

Get-ChildItem -LiteralPath $PayloadDir -File | ForEach-Object {
    Copy-Item -Force -LiteralPath $_.FullName -Destination $targetModDir
}

if (!(Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $configPath -Destination $backupPath
}

$lines = Get-Content -LiteralPath $configPath
$targetSet = $false
$enabledSet = $false
$lines = $lines | ForEach-Object {
    if ($_ -match "^\s*target_assembly\s*=") {
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

Write-Host "Installed DD2 Steam MP to: $targetModDir"
Write-Host "Doorstop config backup: $backupPath"
Write-Host "Start Darkest Dungeon II through Steam. F7 opens the multiplayer panel."
