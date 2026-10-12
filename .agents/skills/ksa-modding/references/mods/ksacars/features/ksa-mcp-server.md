---
type: Mod Feature
title: KSA MCP server over the bridge
description: A stdlib-only Python MCP server, registered in .mcp.json, that turns the mod's file-drop bridge into agent tools for launching, loading, stepping, driving and photographing a running game, returning screenshots inline with contact sheets and temporal-noise maps
mod: ksacars
category: testing-and-tooling
techniques: [file-ipc, mcp-server]
hooks: []
lifecycle: []
game_types: []
tags: [mcp, agents, automation, testing, screenshots, python, json-rpc, wsl]
---

`tools/ksa-mcp/server.py` exposes the [command bridge](./file-drop-command-bridge.md) to an AI agent
as Model Context Protocol tools, so the agent can launch the game, load a save, park a car, drive
it, step the world and look at screenshots that come back inline, "the way Blender's MCP returns a
render".[^ksacars-server] It is registered for the repository in `.mcp.json`
(`python3 tools/ksa-mcp/server.py`) and doubles as a shell tool:
`python3 tools/ksa-mcp/server.py cli status`.[^ksacars-mcp-json][^ksacars-claude]

## How it works

**Transport.** Stdlib only: MCP is JSON-RPC over stdin and stdout, one message per line. The server
handles `initialize` (echoing the client's `protocolVersion`, advertising `tools`), `ping`,
`tools/list` (name, description and a JSON schema per tool) and `tools/call`, and replies
`-32601` to anything else. Notifications (no `id`) are ignored.

**Talking to the game.** `send(cmd, timeout, **args)` writes `<bridge>/in/<time_ns>.json.part`,
renames it to `.json`, then polls `out/<id>.json` every 50 ms, deleting the reply once read and
raising `Refused` with the mod's own error text on `ok: false` or on timeout. The bridge folder is
`<KSA user dir>/Logs/bridge/KSACars`, found by `tools/ksa-user-dir.sh`, which searches the Windows
profile from WSL and the Linux locations in the same order as the mod's `Log.cs` (override with
`KSA_USER_DIR`).[^ksacars-userdir]

**Tools.**

| Tool | Bridge command or action |
|---|---|
| `ksa_status`, `ksa_pause`, `ksa_resume`, `ksa_speed`, `ksa_step` | `status`, `pause`, `resume`, `speed`, `step` |
| `ksa_launch`, `ksa_quit` | Starts `tools/run.sh` in its own session only if no `StarMap.exe`/KSA process is running (checked with `tasklist.exe`), records the PID in `.launched`; `ksa_quit` closes only a game it launched |
| `ksa_load`, `ksa_save`, `ksa_site`, `ksa_spawn`, `ksa_ground` | `load`, `save`, `site`, `spawn`, `ground` |
| `ksa_drive` | `drive`, with the full argument list (throttle, steer, seconds, crew, eva, flip, lights, scoop, boost, lift, camera...) documented in the tool description |
| `ksa_capture` | `capture`; one frame comes back as an inline JPEG; a series as a labelled contact sheet, a GIF path and a temporal-noise map |
| `ksa_log` | Tail of `KSACars.log`, filtered by a pattern |

**Images.** `tools/vis/vis.py` (Pillow) loads the PNGs the bridge moved into its `out/<id>/` folder
(Windows paths are converted to `/mnt/<drive>/...`), and builds `sheet` (labelled grid), `gif`,
`diff` (amplified same-instant difference) and `temporal` (per-pixel motion across a paused series:
"anything bright here is the renderer's own noise"). Images are returned as MCP
`{"type": "image", "mimeType": "image/jpeg", "data": <base64>}` content.[^ksacars-vis]

**CLI mode** writes any image content to `tools/ksa-mcp/last/NN.jpg` and prints text, so a shell
sees what an MCP client would.

## Lifecycle and timing

Outside the game process. Every tool call becomes one bridge command, which the mod executes in
`[StarMapAfterOnFrame]` one at a time; long commands (`step`, `load`, `capture`) get timeouts
scaled to the request (for example `seconds * 20 + 40` for `step`).[^ksacars-bridge] The server is
synchronous: one tool call at a time, matching the bridge's single queue.

## Key code

| Path | Role |
|---|---|
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-mcp/server.py) | MCP server, tool table, game launch and quit, capture post-processing, CLI |
| [.mcp.json](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.mcp.json) | Registers the server as `ksa` for the repository |
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/vis/vis.py) | Image helpers: fit, JPEG base64, diff, temporal map, sheet, GIF |
| [tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-user-dir.sh) | Finds the KSA user folder from WSL or Linux |
| [tools/run.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/run.sh) | Build, deploy, launch through StarMap, follow the log[^ksacars-run] |

## Snippets

```python
# tools/ksa-mcp/server.py L68-L95
def send(cmd: str, timeout: float = 30.0, **args) -> dict:
    """One command to the mod, and its reply. Raises Refused with the mod's own reason."""
    inbox, outbox = bridge() / "in", bridge() / "out"
    inbox.mkdir(parents=True, exist_ok=True)
    ident = f"{time.time_ns()}"
    body = {"id": ident, "cmd": cmd, **{k: v for k, v in args.items() if v is not None}}

    part = inbox / f"{ident}.json.part"
    part.write_text(json.dumps(body))
    part.rename(inbox / f"{ident}.json")

    reply_path = outbox / f"{ident}.json"
    deadline = time.time() + timeout
    while time.time() < deadline:
        if reply_path.exists():
            try:
                reply = json.loads(reply_path.read_text())
            except json.JSONDecodeError:
                time.sleep(0.05)
                continue
            reply_path.unlink(missing_ok=True)
            if not reply.get("ok"):
                raise Refused(reply.get("error") or "refused")
            return reply.get("data") or {}
        time.sleep(0.05)

    (inbox / f"{ident}.json").unlink(missing_ok=True)
    raise Refused(f"no reply to '{cmd}' in {timeout:.0f} s -- is the game running with this build?")
```

```python
# tools/ksa-mcp/server.py L294-L309
        if method == "initialize":
            version = (msg.get("params") or {}).get("protocolVersion", "2025-06-18")
            reply(ident, {"protocolVersion": version, "capabilities": {"tools": {"listChanged": False}},
                          "serverInfo": {"name": "ksa", "version": "0.1"}})
        elif method == "ping":
            reply(ident, {})
        elif method == "tools/list":
            reply(ident, {"tools": [{"name": n, "description": t[0],
                                     "inputSchema": {"type": "object", "properties": t[1], "required": t[2]}}
                                    for n, t in TOOLS.items()]})
        elif method == "tools/call":
            params = msg.get("params") or {}
            content, is_error = call(params.get("name", ""), params.get("arguments") or {})
            reply(ident, {"content": content, "isError": is_error})
        else:
            reply(ident, error={"code": -32601, "message": f"no method {method}"})
```

## Game APIs touched

None: the server only reads and writes files and starts or stops processes. Every game interaction
goes through the bridge, which runs inside the mod.

## Pitfalls and version notes

- **Windows-from-WSL assumptions.** Process detection uses `tasklist.exe`, launch uses
  `tools/run.sh` (which starts `StarMap.exe` from WSL) and reply paths are translated from `C:\` to
  `/mnt/c/`. On native Linux these need adapting.
- **Never close the player's game.** The server only quits a process it launched (PID file); a game
  already running is reused, not replaced.
- **The bridge must be enabled**, i.e. the deployed build must be a developer install; otherwise
  every call times out with "is the game running with this build?".
- Image handling needs Pillow (imported by `vis.py`); the MCP server itself is stdlib.
- The echoed `protocolVersion` defaults to `2025-06-18` when the client sends none.

## See also

- [File-drop command bridge](./file-drop-command-bridge.md)
- [Mod log file](./mod-log-file.md)
- [Package, deploy and release](./package-deploy-release.md)
- [Headless simulation tests](./headless-sim-tests.md)
- [Stdlib Python stdio MCP server wrapping the file bridge, with image tools](../../../howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-2.md) - how-to article demonstrating this feature
- [Return contact sheets, GIFs and temporal-noise maps inline through an MCP server](../../../howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-2.md) - how-to article demonstrating this feature

[^ksacars-server]: tools/ksa-mcp/server.py
[^ksacars-mcp-json]: .mcp.json
[^ksacars-vis]: tools/vis/vis.py
[^ksacars-userdir]: tools/ksa-user-dir.sh
[^ksacars-run]: tools/run.sh
[^ksacars-bridge]: src/KSACars/Ksa/Bridge.cs
[^ksacars-claude]: CLAUDE.md
