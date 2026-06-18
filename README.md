# DD2 Steam MP

[中文说明](README.zh-CN.md)

DD2 Steam MP is an experimental Steam lobby and Steam Networking multiplayer host for *Darkest Dungeon II*. It adds cooperative control, voting, mirror HUDs, and custom PvP/debug combat flows on top of a game that was not originally built for multiplayer.

This project is not affiliated with Red Hook Studios. It is a research/debugging mod, not a finished commercial-quality multiplayer product.

## Current Scope

- Steam friends lobby creation, invite overlay, lobby membership, and version checks.
- Steam Networking message transport.
- Doorstop bootstrap that loads DD2SteamMP first and then chainloads BepInEx for compatibility with regular BepInEx plugins.
- In-game control panel, lobby status, player assignment, and host/client UI language toggle.
- Combat input routing for assigned hero slots and PvP enemy-side control.
- Voting/sync flows for route choices, story choices, loot, inn choices, lair continuation, dialogs, altar/confession flows, and selected run interactions.
- Client mirror HUDs for combat, map, inventory/run status, store/loadout flows, and custom debug-demo setup.
- Custom debug-demo/PvP battle setup, including hero presets, monster presets, waves, torch/confession options, and selected arena modifiers.

## Important Limitations

- Clients do not run a fully synchronized native DD2 scene. The practical target is remote control plus mirror UI, not deterministic Unity scene replication.
- The host remains authoritative for game state.
- All players need compatible DD2SteamMP builds. Lobby version is checked by the mod.
- This is a Doorstop host, not a normal `BepInEx/plugins` DLL.
- Game updates can break internal hooks and UI adapters.

## Controls

The in-game UI exposes the supported actions. The historical debug hotkeys are:

| Key | Action |
| --- | --- |
| `F6` | Mirror HUD / client-facing play UI |
| `F7` | Host/control panel |
| `F8` | Dump lobby state |
| `F9` | Create friends-only lobby |
| `F10` | Open Steam invite overlay |
| `F11` | Leave lobby |

Some panels expose their own buttons instead of requiring command-file input.

## Release Package Layout

The public release package is intentionally DLL-only:

```text
Darkest Dungeon II/
└─ DD2SteamMP/
   ├─ DD2SteamMultiplayerDoorstop.dll
   ├─ DD2SteamMultiplayerHost.dll
   └─ DD2DebugDemoCore.dll
```

This package does not include PowerShell installers. Manual Doorstop configuration is still required.

## Manual Installation

1. Install BepInEx 5 for the normal Unity/Mono build of *Darkest Dungeon II*.
2. Copy the `DD2SteamMP` folder from the release zip into the game directory.
3. Back up `doorstop_config.ini`.
4. Change the Doorstop target assembly to:

```ini
target_assembly=DD2SteamMP\DD2SteamMultiplayerDoorstop.dll
```

5. Keep Doorstop enabled.
6. Start the game through Steam.
7. Check `DD2SteamMP/doorstop_host.log` after launch.

The DD2SteamMP Doorstop entrypoint starts the host and then chainloads the original BepInEx preloader, so existing BepInEx plugins can still load.

## Requirements

- *Darkest Dungeon II* on Steam
- Steam running under the same account that launches the game
- BepInEx 5.x / Doorstop already installed
- Unity/Mono BepInEx build, not IL2CPP
- Compatible DD2SteamMP build on every player machine
- .NET SDK or build tools capable of building `net48`
- Local game assemblies from `Darkest Dungeon II_Data/Managed`

## Build

1. Copy `Directory.Build.props.example` to `Directory.Build.props`.
2. Set `BepInExDir` and `ManagedDir`.
3. Build the main host project:

```powershell
dotnet build .\DD2SteamMultiplayerHost\DD2SteamMultiplayerHost.csproj -c Release
dotnet build .\DD2SteamMultiplayerDoorstop\DD2SteamMultiplayerDoorstop.csproj -c Release
```

The host project also builds `DD2DebugDemoCore`.

## Source Layout

- `DD2SteamMultiplayerDoorstop`: Doorstop entrypoint, BepInEx chainloader, and early runtime patcher.
- `DD2SteamMultiplayerHost`: Steam lobby/networking, multiplayer session logic, UI, adapters, mirror HUDs, and PvP/debug-demo control.
- `DD2DebugDemoCore`: reusable debug-demo actor/loadout/equipment helpers.
- `DD2SteamMultiplayerRuntime`: older runtime adapter source kept for reference and compatibility work.

The repository excludes game assemblies, decompiled game source, exported assets, local install paths, and build output.

## Compatibility Notes

- This mod shares the same Doorstop entrypoint slot as BepInEx, so it must chainload BepInEx correctly.
- If another loader also replaces `target_assembly`, manual merging is required.
- Multiplayer behavior is tested incrementally. Treat new DD2 versions as unverified until logs and basic flows are checked.
