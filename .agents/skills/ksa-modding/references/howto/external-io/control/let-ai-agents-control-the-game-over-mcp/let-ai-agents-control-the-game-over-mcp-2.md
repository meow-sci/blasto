---
type: How-To
title: Stdlib Python stdio MCP server wrapping the file bridge, with image tools
description: Register a dependency-free Python MCP server for the repository that turns each tool call into a bridge command file for the mod and returns screenshots inline as base64 JPEG
category: external-io
group: control
topic: let-ai-agents-control-the-game-over-mcp
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksacars/features/ksa-mcp-server
  - mods/ksagolf/features/file-command-bridge
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
techniques: [file-ipc, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterOnFrame]
lifecycle: [startup:all-mods-loaded, frame:after-on-frame]
game_types: [KSA.Constants]
tags: [mcp, stdio, python, json-rpc, file-bridge, screenshots, developer-install]
---

Outcome: an agent working in the mod's repository can launch the game, load a save, drive the mod's features, step the simulation and look at screenshots that come back inline in the conversation, with no port opened in the game. The mod gains only a small file bridge; the MCP protocol lives in a separate process. KSACars, KSAGolf, KSAStructures and KSArmory all use this layout; this article follows KSACars. Demonstrated by [KSACars MCP server](../../../../mods/ksacars/features/ksa-mcp-server.md), with the bridge itself in [KSAGolf file command bridge](../../../../mods/ksagolf/features/file-command-bridge.md), [KSAStructures developer bridge and MCP](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md) and [KSArmory file command bridge and MCP](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md). The bridge protocol in depth is the file-bridge topic of the testing-and-tooling category.

## Background: how the game does it

Nothing in KSA provides remote control. The shared answer in these mods is a folder: the mod polls `<log folder>/bridge/<ModName>/in` for `*.json`, runs the oldest command inside the game loop and writes `out/<id>.json`. The log folder is found by the mod's own search for KSA's user directory (KSA's `Constants.LogsFolderPath` in [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L283-L283) is `<Documents>/My Games/Kitten Space Agency/logs`), and the external side repeats the search with a shell script that also understands WSL and Linux locations.

## Lifecycle and timing

| Piece | Runs |
|---|---|
| Bridge created | `[StarMapAllModsLoaded]`, only when a `developer` file sits beside the DLL (`Build.Developer`) |
| Bridge polled | `[StarMapAfterOnFrame]` (`frame:after-on-frame`): once per frame in every state, including menus and a hidden HUD, because that hook always fires. The mod lists the inbox every 0.1 s of player time |
| Multi-frame commands (`step`, `capture`, `load`) | A running delegate called each frame until it answers; the queue waits, so a list of commands is a script |
| MCP server | Outside the game, started by the agent from `.mcp.json`; synchronous, one tool call at a time, matching the bridge's single queue |

All bridge work is on the main thread, so there is no cross-thread hand-off to design: the files are the hand-off. For the frame order see [the per-frame sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Add the bridge to the mod, developer builds only.** A deploy script writes the empty `developer` marker; the release package never carries it. Create the bridge at load and poll it from the always-called hook:

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L49-L56
           BoostHook.Install();
           HudHook.Install();
           RailsHook.Install();

           if (Build.Developer) _bridge = new Bridge();
           Log.Info(Build.Developer
                        ? "a developer's install: the bridge is listening"
                        : "a player's install");
   ```

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L59-L70
       [StarMapAfterOnFrame]
       public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
       {
           try
           {
               if (_disabled) return;

               // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
               StepOnce();

               // After the step, so a command sees this frame's world.
               _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? KsaWorld.SimStepSeconds : 0.0);
   ```

2. **Use a per-mod folder, files not sockets, and atomic writes.** A folder of its own keeps sibling mods' bridges from answering each other's commands; no port means no firewall prompt and it crosses the WSL boundary unchanged. Replies are written as `.part` then moved so the reader never sees half a file.

   ```csharp
   // src/KSACars/Ksa/Bridge.cs L30-L33
       // A folder of its own: every mod built from the same tooling has a bridge, and two reading one
       // folder each answer whichever command they reach first.
       private static string Root => Path.Combine(Log.Folder, "bridge", "KSACars");
       private static string Inbox => Path.Combine(Root, "in");
   ```

   ```csharp
   // src/KSACars/Ksa/Bridge.cs L45-L77
       public void Update(double dtPlayer, double dtSim)
       {
           try
           {
               if (_running is not null && _current is not null)
               {
                   if (_running(dtPlayer, dtSim) is { } reply) Answer(_current, reply);
                   return;
               }

               _sincePoll += dtPlayer;
               if (_sincePoll < PollSeconds) return;
               _sincePoll = 0.0;

               if (!Directory.Exists(Inbox)) return;

               string? next = Directory.GetFiles(Inbox, "*.json").Order(StringComparer.Ordinal).FirstOrDefault();
               if (next is null) return;

               string text = File.ReadAllText(next);
               File.Delete(next);

               if (!BridgeCommand.TryParse(text, out BridgeCommand? command, out string trouble))
               {
                   Log.Warn($"bridge: refused {Path.GetFileName(next)} -- {trouble}");
                   return;
               }

               Start(command!);
           }
           catch (Exception e)
           {
               if (_current is { } command) Answer(command, Failed($"threw: {e.Message}"));
   ```

3. **Write the stdio MCP server in the standard library.** MCP over stdio is JSON-RPC, one message per line. Handle `initialize`, `ping`, `tools/list`, `tools/call`; ignore notifications; answer anything else with `-32601`. Echo the client's `protocolVersion`.

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

4. **Turn a tool call into a bridge command.** Write `<id>.json.part`, rename to `.json`, poll `out/<id>.json` every 50 ms, and raise a refusal carrying the mod's own error text on `ok: false` or on timeout. Remove an unanswered request so a late game does not run a stale command.

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

5. **Return failures as tool errors.** A tool that throws must still answer; the result carries `isError` so the agent sees the reason rather than a protocol failure.

   ```python
   # tools/ksa-mcp/server.py L263-L271
   def call(name: str, args: dict) -> tuple[list[dict], bool]:
       if name not in TOOLS:
           return [_text(f"no tool {name}")], True
       try:
           return TOOLS[name][3](args or {}), False
       except Refused as e:
           return [_text(str(e))], True
       except Exception as e:  # noqa: BLE001 -- a tool that throws must still answer
           return [_text(f"{type(e).__name__}: {e}")], True
   ```

6. **Own the game's lifetime carefully.** Launch only if no game process runs and record the PID; close only a game this server started. The player's session is never the agent's to end.

   ```python
   # tools/ksa-mcp/server.py L100-L142
   STATE = Path(__file__).resolve().parent / ".launched"


   def launch(save: str | None = None) -> str:
       """Starts the game if none is running. Never a second one, and never over the player's."""
       if game_running():
           note = "a game is already running; using it"
       else:
           log = open(Path(__file__).resolve().parent / "launch.log", "w")
           proc = subprocess.Popen([str(REPO / "tools" / "run.sh")], cwd=REPO, stdout=log,
                                   stderr=subprocess.STDOUT, start_new_session=True)
           STATE.write_text(str(proc.pid))
           note = "launched"

       deadline = time.time() + 300
       while True:
           try:
               send("status", timeout=5)
               break
           except Refused:
               if time.time() > deadline:
                   raise Refused("the game never answered on the bridge")
               time.sleep(2)

       if save:
           data = send("load", timeout=90, save=save)
           note += f"; loaded '{save}', flying {data.get('craft')}"
       return note


   def quit_game() -> str:
       """Closes a game this server launched, and refuses any other: that one is somebody's session."""
       if not STATE.exists():
           raise Refused("this server did not launch the running game; it is the player's to close")
       pid = int(STATE.read_text())
       try:
           os.killpg(pid, signal.SIGTERM)
       except ProcessLookupError:
           pass
       STATE.unlink(missing_ok=True)
       return "closed"


   ```

7. **Return pictures inline.** The capture tool asks the mod for one or several frames, loads the PNGs the bridge placed beside the reply (translating Windows paths for WSL) and returns base64 JPEG content. A series comes back as a labelled contact sheet, a GIF path and a temporal-noise map that reveals renderer noise in a paused world.

   ```python
   # tools/ksa-mcp/server.py L185-L187
   def _image(img, width: int) -> dict:
       return {"type": "image", "data": vis.jpeg_b64(img, width), "mimeType": "image/jpeg"}

   ```

8. **Register it for the repository** so any MCP client started there finds it, and add a CLI mode that writes image content to disk, so a shell can see what an MCP client would.

   ```json
   // .mcp.json L1-L8
   {
     "mcpServers": {
       "ksa": {
         "command": "python3",
         "args": ["tools/ksa-mcp/server.py"]
       }
     }
   }
   ```

   Find the user directory with a helper script (`KSA_USER_DIR` overrides it; it tries the Windows profile from WSL, then native Linux locations in the same order as the mod's log code) so no path in the repository names a person's home directory ([tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-user-dir.sh#L1-L12)).

## Complete example

| File | Role |
|---|---|
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-mcp/server.py) | MCP server, tool table, launch and quit, capture post-processing, CLI |
| [.mcp.json](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.mcp.json) | Registers the server |
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/vis/vis.py) | Image helpers: fit, JPEG base64, diff, temporal map, sheet, GIF |
| [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs) | In-game side: poll, run, answer |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | Creates and ticks the bridge |

## Variations and gotchas

- **Developer install only.** If the deployed build is a player build, no bridge runs and every call times out with "is the game running with this build?".
- **Windows-from-WSL assumptions.** Process detection uses `tasklist.exe`, launch uses a script that starts `StarMap.exe` from WSL, and reply paths are translated from `C:\` to `/mnt/c/`. On native Linux these need adapting.
- **One command at a time.** The synchronous server and single bridge queue mean no parallel tool calls; long commands get timeouts scaled to the request (for example `step` uses `seconds * 20 + 40`).
- **Image tools need Pillow** (`vis.py`); the MCP server itself is stdlib.
- **Other mods' variations.** KSAStructures adds `reload_shaders` for a mod shader (see [Reload a mod shader in a running game](../../../shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md)); KSArmory's server is about 540 lines with crops, same-instant diffs and contact sheets; KSAGolf adds golf and course-building commands. KSAStructures and KSArmory reach the game's `ScreenshotCapture` by reflection on the private `Program._screenshotCapture` (taking screenshots from code is covered in the testing-and-tooling category).
- **Compared with approach 1.** There is no authentication story to worry about (nothing listens), but there is also no way to serve a remote machine, and the tool surface is whatever the mod's bridge implements.

## See also

- [Overview](overview.md)
- [Approach 1: in-process Streamable HTTP endpoint](let-ai-agents-control-the-game-over-mcp-1.md)
- [KSACars MCP server](../../../../mods/ksacars/features/ksa-mcp-server.md)
- [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md)
- [Reload a mod shader in a running game](../../../shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md)
