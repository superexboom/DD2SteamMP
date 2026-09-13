# DD2SteamMP MCP

`mcp_server.py` is a dependency-free stdio MCP wrapper for the local
`DD2SteamMP/command.txt` endpoint and `doorstop_host.log`.

By default it targets the local installation:

```text
F:\SteamLibrary\steamapps\common\Darkest Dungeon® II\DD2SteamMP
```

Override paths for another machine with:

```powershell
$env:DD2_STEAM_MP_DIR = 'Z:\SteamLibrary\steamapps\common\Darkest Dungeon® II\DD2SteamMP'
python .\automation\mcp_server.py
```

The Arena test tools are host-side only:

- `dd2mp.arena_status`
- `dd2mp.arena_start` with `battleConfigurationId` and `bossModifierId`
- `dd2mp.arena_cancel`
- `dd2mp.arena_rng_test`
- `dd2mp.combat_detail`
- `dd2mp.log_tail`
- `dd2mp.command` for existing local commands

`dd2mp.arena_start` uses the current party and asks the Unity host to run the
same launch path as the F7 Arena panel. It can therefore exercise the
`ALTAR_OF_HOPE -> DRIVING` normalization, enemy ordainment scope, and Arena RNG
context without injecting Unity calls from Python.

`dd2mp.combat_detail` is intentionally verbose: it records every current
combat actor's team/rank, controller type, boss modifier, selected skill and
cached valid skill targets. This is the first command to run when a PVE Arena
turn appears stuck or an enemy-control panel is grey.
