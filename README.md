# DD2SteamMP

Steam lobby/networking multiplayer host for Darkest Dungeon II. Doorstop, host, runtime, and debug-demo core are kept together because they share one load/runtime chain.

## Build

1. Copy Directory.Build.props.example to Directory.Build.props.
2. Edit BepInExDir and ManagedDir to match your local Darkest Dungeon II installation.
3. Run:

``powershell
dotnet build .\DD2SteamMultiplayerHost\DD2SteamMultiplayerHost.csproj -c Release
``

## Notes

This repository contains source code only. Game assemblies, decompiled game source, exported assets, build outputs, and local install paths are intentionally excluded.