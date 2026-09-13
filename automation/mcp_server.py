"""Dependency-free MCP wrapper for the DD2SteamMP command file and HostLog.

The Unity host remains the only process that touches DD2 objects. This wrapper
only writes the already-existing local command.txt endpoint and reads the local
doorstop_host.log for deterministic test automation.
"""
import json
import os
import pathlib
import sys
import time


DEFAULT_GAME_DIR = r"F:\SteamLibrary\steamapps\common\Darkest Dungeon® II"
MP_DIR = pathlib.Path(os.environ.get("DD2_STEAM_MP_DIR", str(pathlib.Path(DEFAULT_GAME_DIR) / "DD2SteamMP")))
COMMAND_PATH = pathlib.Path(os.environ.get("DD2_STEAM_MP_COMMAND_PATH", str(MP_DIR / "command.txt")))
LOG_PATH = pathlib.Path(os.environ.get("DD2_STEAM_MP_LOG_PATH", str(MP_DIR / "doorstop_host.log")))


def read_tail(lines=200):
    try:
        count = max(1, min(int(lines), 5000))
        data = LOG_PATH.read_text(encoding="utf-8", errors="replace").splitlines()
        return data[-count:]
    except Exception as exc:
        return ["[mcp] log read failed: " + str(exc)]


def send_command(command, wait_seconds=8.0):
    command = (command or "").strip()
    if not command:
        return {"ok": False, "error": "command is empty"}
    try:
        before = LOG_PATH.stat().st_size if LOG_PATH.exists() else 0
        COMMAND_PATH.parent.mkdir(parents=True, exist_ok=True)
        COMMAND_PATH.write_text(command, encoding="utf-8", newline="\n")
        marker = "Command file request: " + command
        deadline = time.time() + max(0.5, min(float(wait_seconds), 30.0))
        while time.time() < deadline:
            tail = read_tail(300)
            if any(marker in line for line in tail):
                return {"ok": True, "command": command, "accepted": True, "logTail": tail[-40:]}
            time.sleep(0.25)
        return {"ok": True, "command": command, "accepted": False, "logBytesBefore": before, "logTail": read_tail(40)}
    except Exception as exc:
        return {"ok": False, "command": command, "error": str(exc)}


def call_tool(name, args):
    if name == "dd2mp.log_tail":
        return {"ok": True, "path": str(LOG_PATH), "lines": read_tail(args.get("lines", 200))}
    if name == "dd2mp.arena_status":
        result = send_command("arena_status")
        result["path"] = str(LOG_PATH)
        return result
    if name == "dd2mp.arena_start":
        config = str(args.get("battleConfigurationId", "mountain_boss_arms")).strip()
        modifier = str(args.get("bossModifierId", "none")).strip() or "none"
        return send_command("arena_start " + config + " " + modifier)
    if name == "dd2mp.arena_cancel":
        return send_command("arena_cancel")
    if name == "dd2mp.arena_rng_test":
        return send_command("arena_rng_test")
    if name == "dd2mp.combat_detail":
        return send_command("combatdetail")
    if name == "dd2mp.command":
        return send_command(str(args.get("command", "")))
    return {"ok": False, "error": "unknown tool: " + str(name)}


TOOLS = [
    {"name": "dd2mp.log_tail", "description": "Read the local DD2SteamMP HostLog tail.", "inputSchema": {"type": "object", "properties": {"lines": {"type": "integer"}}}},
    {"name": "dd2mp.arena_status", "description": "Ask the Unity host for Arena mode, scopes, RNG and launch status.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "dd2mp.arena_start", "description": "Start a host-authoritative Arena test from the current run using its current party. Optional bossModifierId may be none.", "inputSchema": {"type": "object", "properties": {"battleConfigurationId": {"type": "string"}, "bossModifierId": {"type": "string"}}}},
    {"name": "dd2mp.arena_cancel", "description": "Cancel a pending Arena launch and release all temporary state.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "dd2mp.arena_rng_test", "description": "Run the Arena RandomContainer snapshot/restore self-check without launching combat.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "dd2mp.combat_detail", "description": "Dump each current combat actor's team, controller, boss modifier, selected skill and valid skill targets to HostLog.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "dd2mp.command", "description": "Send one existing local DD2SteamMP command-file command.", "inputSchema": {"type": "object", "required": ["command"], "properties": {"command": {"type": "string"}}}},
]


def response(request_id, result=None, error=None):
    message = {"jsonrpc": "2.0", "id": request_id}
    if error:
        message["error"] = {"code": -32000, "message": error}
    else:
        message["result"] = result
    return json.dumps(message, ensure_ascii=True)


def handle(message):
    request_id = message.get("id")
    method = message.get("method")
    if method == "initialize":
        return response(request_id, {"protocolVersion": message.get("params", {}).get("protocolVersion", "2024-11-05"), "capabilities": {"tools": {}}, "serverInfo": {"name": "dd2-steammp", "version": "0.1.0"}})
    if method == "notifications/initialized":
        return None
    if method == "tools/list":
        return response(request_id, {"tools": TOOLS})
    if method != "tools/call":
        return response(request_id, error="Unsupported MCP method: " + str(method))
    params = message.get("params", {})
    name = params.get("name")
    result = call_tool(name, params.get("arguments") or {})
    return response(request_id, {"content": [{"type": "text", "text": json.dumps(result, ensure_ascii=False)}], "structuredContent": result})


def main():
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            reply = handle(json.loads(line))
            if reply:
                sys.stdout.write(reply + "\n")
                sys.stdout.flush()
        except Exception as exc:
            sys.stdout.write(response(None, error=str(exc)) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
