param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

$AppId = "1940340"

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
        throw "Could not find Darkest Dungeon II. Re-run with -GameDir pointing at the game install folder."
    }

    return $found
}

$resolvedGameDir = Resolve-DD2GameDir $GameDir
$configPath = Join-Path $resolvedGameDir "doorstop_config.ini"
$backupPath = Join-Path $resolvedGameDir "doorstop_config.ini.dd2steammp.bak"

if (!(Test-Path -LiteralPath $backupPath)) {
    throw "DD2 Steam MP backup was not found: $backupPath"
}

Copy-Item -Force -LiteralPath $backupPath -Destination $configPath

Write-Host "Restored Doorstop config from: $backupPath"
Write-Host "DD2SteamMP files were left in place for log inspection. They are inert after the config restore."
