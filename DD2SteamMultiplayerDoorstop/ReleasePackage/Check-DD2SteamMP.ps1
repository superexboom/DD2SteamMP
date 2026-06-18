param(
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"

$AppId = "1940340"
$FailureCount = 0
$WarningCount = 0

function Write-Check([string]$Level, [string]$Message) {
    switch ($Level) {
        "OK" {
            Write-Host "[OK]   $Message" -ForegroundColor Green
        }
        "WARN" {
            $script:WarningCount++
            Write-Host "[WARN] $Message" -ForegroundColor Yellow
        }
        "FAIL" {
            $script:FailureCount++
            Write-Host "[FAIL] $Message" -ForegroundColor Red
        }
        default {
            Write-Host "[$Level] $Message"
        }
    }
}

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
            Write-Check "FAIL" "GameDir does not exist: $InputPath"
            return $null
        }

        $resolved = (Resolve-Path -LiteralPath $InputPath).Path
        if (!(Test-Path -LiteralPath (Join-Path $resolved "Darkest Dungeon II.exe"))) {
            Write-Check "FAIL" "GameDir does not look like Darkest Dungeon II root: $resolved"
            return $null
        }

        return $resolved
    }

    $found = Find-DD2GameDir
    if ([string]::IsNullOrWhiteSpace($found)) {
        Write-Check "FAIL" "Could not auto-detect Darkest Dungeon II. Re-run with -GameDir pointing at the game install folder."
        return $null
    }

    return $found
}

function Get-ConfigValue([string[]]$Lines, [string]$Key) {
    foreach ($line in $Lines) {
        if ($line -match ("^\s*" + [regex]::Escape($Key) + "\s*=\s*(.*)$")) {
            return $matches[1].Trim()
        }
    }

    return $null
}

function Read-JsonFile([string]$Path) {
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        Write-Check "FAIL" "Could not parse JSON: $Path ($($_.Exception.Message))"
        return $null
    }
}

Write-Host "DD2 Steam MP install check"
Write-Host ""

$packageManifestPath = Join-Path $PSScriptRoot "DD2SteamMP\dd2steammp_manifest.json"
$packageManifest = $null
if (Test-Path -LiteralPath $packageManifestPath) {
    $packageManifest = Read-JsonFile $packageManifestPath
    if ($packageManifest -ne $null) {
        Write-Check "OK" "Package version: $($packageManifest.componentVersion), protocol=$($packageManifest.protocolVersion)"
    }
} else {
    Write-Check "WARN" "Package manifest was not found next to this checker: $packageManifestPath"
}

$resolvedGameDir = Resolve-DD2GameDir $GameDir
if ([string]::IsNullOrWhiteSpace($resolvedGameDir)) {
    exit 1
}

Write-Check "OK" "Game directory: $resolvedGameDir"

$configPath = Join-Path $resolvedGameDir "doorstop_config.ini"
$backupPath = Join-Path $resolvedGameDir "doorstop_config.ini.dd2steammp.bak"
$modDir = Join-Path $resolvedGameDir "DD2SteamMP"
$gameExe = Join-Path $resolvedGameDir "Darkest Dungeon II.exe"
$doorstopProxy = Join-Path $resolvedGameDir "winhttp.dll"
$bepInExPreloader = Join-Path $resolvedGameDir "BepInEx\core\BepInEx.Preloader.dll"

if (Test-Path -LiteralPath $gameExe) {
    Write-Check "OK" "Game executable exists."
} else {
    Write-Check "FAIL" "Game executable is missing: $gameExe"
}

if (Test-Path -LiteralPath $doorstopProxy) {
    Write-Check "OK" "Doorstop proxy exists: winhttp.dll"
} else {
    Write-Check "WARN" "Doorstop proxy winhttp.dll was not found. Doorstop may not load unless another proxy is installed."
}

if (Test-Path -LiteralPath $configPath) {
    Write-Check "OK" "doorstop_config.ini exists."
    $configLines = Get-Content -LiteralPath $configPath
    $enabled = Get-ConfigValue $configLines "enabled"
    $target = Get-ConfigValue $configLines "target_assembly"

    if ($enabled -match "^(?i:true)$") {
        Write-Check "OK" "Doorstop enabled=true."
    } else {
        Write-Check "FAIL" "Doorstop enabled is not true. Current value: $enabled"
    }

    if ($target -eq "DD2SteamMP\DD2SteamMultiplayerDoorstop.dll") {
        Write-Check "OK" "Doorstop target points at DD2SteamMP."
    } else {
        Write-Check "FAIL" "Doorstop target mismatch. Current value: $target"
    }
} else {
    Write-Check "FAIL" "doorstop_config.ini is missing. Install Doorstop/BepInEx first."
}

if (Test-Path -LiteralPath $backupPath) {
    Write-Check "OK" "DD2 Steam MP backup exists."
} else {
    Write-Check "WARN" "Backup config was not found yet: $backupPath"
}

if (Test-Path -LiteralPath $bepInExPreloader) {
    Write-Check "OK" "BepInEx preloader exists for chainload compatibility."
} else {
    Write-Check "WARN" "BepInEx preloader was not found. DD2 Steam MP can still try to run, but existing BepInEx mods will not be chainloaded."
}

if (Test-Path -LiteralPath $modDir) {
    Write-Check "OK" "Installed payload folder exists: $modDir"
} else {
    Write-Check "FAIL" "Installed payload folder is missing: $modDir"
}

$requiredFiles = @(
    "DD2SteamMultiplayerDoorstop.dll",
    "DD2SteamMultiplayerHost.dll",
    "dd2steammp_manifest.json"
)

foreach ($fileName in $requiredFiles) {
    $path = Join-Path $modDir $fileName
    if (Test-Path -LiteralPath $path) {
        Write-Check "OK" "Installed file exists: $fileName"
    } else {
        Write-Check "FAIL" "Installed file is missing: $fileName"
    }
}

$installedManifestPath = Join-Path $modDir "dd2steammp_manifest.json"
if (Test-Path -LiteralPath $installedManifestPath) {
    $installedManifest = Read-JsonFile $installedManifestPath
    if ($installedManifest -ne $null) {
        Write-Check "OK" "Installed version: $($installedManifest.componentVersion), protocol=$($installedManifest.protocolVersion)"
        if ($packageManifest -ne $null) {
            if ($installedManifest.componentVersion -eq $packageManifest.componentVersion -and
                [int]$installedManifest.protocolVersion -eq [int]$packageManifest.protocolVersion) {
                Write-Check "OK" "Installed version matches this package."
            } else {
                Write-Check "FAIL" "Installed version does not match this package. Installed=$($installedManifest.componentVersion)/$($installedManifest.protocolVersion), package=$($packageManifest.componentVersion)/$($packageManifest.protocolVersion)"
            }
        }
    }
}

$logPath = Join-Path $modDir "doorstop_host.log"
if (Test-Path -LiteralPath $logPath) {
    Write-Check "OK" "Runtime log exists: $logPath"
    $recentLog = Get-Content -LiteralPath $logPath -Tail 40
    $loaded = $recentLog | Where-Object { $_ -match "Doorstop entry loaded|Host runner started|Bootstrap.EnsureStarted" }
    if ($loaded) {
        Write-Check "OK" "Recent log contains DD2 Steam MP startup entries."
    } else {
        Write-Check "WARN" "Recent log exists but does not contain startup entries in the last 40 lines."
    }
} else {
    Write-Check "WARN" "Runtime log does not exist yet. Start the game once after installation."
}

Write-Host ""
Write-Host "Summary: $FailureCount failure(s), $WarningCount warning(s)."

if ($FailureCount -gt 0) {
    exit 1
}

exit 0
