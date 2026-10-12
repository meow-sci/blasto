---
type: Mod Feature
title: Developer file bridge and MCP server for driving a running game
description: On a developer install the mod polls a folder for JSON commands (load, step, camera, lighting, shader reload, screenshot capture with manifests) and a stdlib Python MCP server turns that folder into tools an agent can call
mod: ksastructures
category: testing-and-tooling
techniques: [reflection, json-config, external-process, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterOnFrame
  - reflection KSA.Program._screenshotCapture
  - reflection KSA.FileReference.DoLoad
lifecycle: [startup:all-mods-loaded, frame:after-on-frame, frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.ScreenshotCapture
  - KSA.FileReference
  - KSA.ShaderReference
  - KSA.GameSaves
  - KSA.Universe
  - KSA.SimSpeed
  - KSA.Camera
  - KSA.Constants
tags: [automation, mcp, json, screenshots, shader-reload, agent, wsl, bridge]
---

KSAStructures' visual work (lighting under ground, the lift's car against its rider, the camera
roll) was judged from pictures taken by an agent driving the running game. On a developer install
the mod reads JSON commands from a folder and answers in another; `tools/ksa-mcp/server.py` is a
dependency-free MCP server that writes those files and hands back replies and screenshots. A player
install never starts it.[^mod-bridge][^mod-server]

## How it works

1. **Only on a developer install.** `Build.Developer` is true when a file named `developer` sits
   beside the DLL; `tools/deploy.sh` writes it and the release package never carries it.
   `[StarMapAllModsLoaded]` creates the `Bridge` only then.[^mod-build][^mod-entry][^mod-deploy]
2. **Files, not a socket.** Commands are `*.json` files in `<log folder>/bridge/KSAStructures/in`,
   taken oldest first by name every 0.1 s of player time; replies go to `out/<id>.json`, written as
   `.part` and renamed so the reader never sees half a file. The folder is per mod so sibling mods'
   bridges never answer each other, and it crosses the WSL boundary without a port.[^mod-bridge]
3. **Parsing in `Sim/`.** `BridgeCommand.TryParse` requires an `id` usable as a file name and a
   `cmd`, keeps arguments case-insensitively, and sets `Settings` fields by name through reflection;
   it has no KSA types and is unit-tested.[^mod-bridgecommand][^mod-bridge-tests]
4. **Commands.** `status`, `pause`/`resume`/`speed` (through `Universe.SetSimulationSpeed`, below the
   UI's 0.1x floor), `set`/`get` (settings), `camera` (a `SceneCamera` composed shot relative to the
   flown craft), `office_view` (a view at building-frame coordinates), `office_light` (darkening,
   shadows, lamp scale, fill), `office_put` (move the flown craft into the building through the
   `PrepareWorker` window), `reload_shaders`, `step`, `capture` and `load`.[^mod-bridge][^mod-ksaworld]
5. **Commands that take frames** return a closure in `_running` that is called every frame until it
   produces a reply; the queue waits, so a list of commands runs as a script. `step` unpauses, sums
   the consumed sim step, and pauses again; `load` calls `GameSaves.LoadSaveGame` and waits for a
   flown craft.[^mod-bridge][^ksa-gamesaves]
6. **Screenshots through KSA's own capture.** `ScreenshotCapture.Request(scale, flags)` is public,
   but the instance is the private `Program._screenshotCapture`, read once by reflection (KSA's own
   `screenshot` terminal action reaches the same field).[^mod-ksaworld][^ksa-program][^ksa-screenshot]
   The bridge asks with `warm=24` so the UI is hidden for the warm-up frames, then moves the newest
   `ksa_*.png` out of `Documents/My Games/Kitten Space Agency/exports/screenshots` and writes a
   manifest beside it: time, pause state, speed, field of view, and where the flown craft falls on
   screen from `camera.EgoToClipDouble`.[^mod-bridge]
7. **Shader hot reload for a mod shader.** KSA's own reloader maps paths through "Content", which a
   mod's shader is not under, so `reload_shaders` invokes the internal `FileReference.DoLoad` on the
   `ShaderReference` by reflection, reports the module handle before and after, and drops the dark
   pass's pipelines so they rebuild.[^mod-bridge][^ksa-filereference] See
   [core shader library include](./core-shader-library-include.md).
8. **The MCP side.** `server.py` speaks MCP's JSON-RPC over stdio with only the standard library,
   exposes `ksa_*` tools (`initialize`, `tools/list`, `tools/call`), and doubles as a CLI
   (`server.py cli status`). It is registered in the repo's `.mcp.json`. Captures come back inline;
   multi-frame captures are turned into a contact sheet, a GIF and a temporal-noise map by
   `tools/vis/vis.py`, which crops around the craft using the manifest's screen position.[^mod-server][^mod-mcpjson][^mod-vis]

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `startup:all-mods-loaded` | `[StarMapAllModsLoaded]` | Create the bridge on a developer install |
| `frame:after-on-frame` | `[StarMapAfterOnFrame]` -> `Bridge.Update(dtPlayer, dtSim)` | Poll, run or advance a command; runs in menus and with the UI hidden because it is the only hook KSA always calls |
| the step (usually `frame:draw-ui`) | `Bridge.DriveCamera()` | Hold a `camera`/`office_view` pose |
| `StarMapUnload` | `Bridge.Release()` | Hand a held view back |

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L23-L181) | Polling, dispatch, atomic replies |
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L302-L348) | `reload_shaders` |
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L350-L572) | `step`, `load`, `capture` and manifests |
| [src/KSAStructures/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/BridgeCommand.cs) | Command parsing and settings by name |
| [src/KSAStructures/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/SceneCamera.cs#L12-L55) | Composed-shot pose source |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L340-L397) | `TryRequestScreenshot` |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/ksa-mcp/server.py) | MCP server and CLI |
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/vis/vis.py) | Crops, diffs, sheets, temporal noise |

## Snippets

Polling the inbox from the one hook that always runs:

```csharp
// src/KSAStructures/Ksa/Bridge.cs L90-L125
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
            else Log.Warn($"bridge: {e.Message}");
        }
    }
```

Reaching KSA's screenshot capture:

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L362-L386
    public static bool TryRequestScreenshot(int scale = 1, string flags = "")
    {
        try
        {
            if (!_lookedForShots)
            {
                _lookedForShots = true;
                _shots = typeof(Program)
                         .GetField("_screenshotCapture", BindingFlags.NonPublic | BindingFlags.Instance)
                         ?.GetValue(Program.Instance) as ScreenshotCapture;

                if (_shots is null && !_warnedAboutShots)
                {
                    _warnedAboutShots = true;
                    Log.Warn("screenshots: Program._screenshotCapture did not resolve; "
                             + "scripted captures will write nothing");
                }
            }

            if (_shots is null) return false;

            // A capture already running is dropped by the engine with its own warning, so asking
            // again while one is in flight costs nothing and loses only that frame.
            _shots.Request(scale, flags);
            return true;
```

The other end, in Python:

```python
# tools/ksa-mcp/server.py L69-L96
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

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `Program._screenshotCapture` (private), terminal action `screenshot` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1660-L1672) | Capture instance |
| `ScreenshotCapture.Request(int, string)` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L92-L110) | Flags `ui`, `hud`, `warm=N` |
| `FileReference.DoLoad` (internal), `ShaderReference.DoLoad`, `Compile` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L181) | Recompile a shader asset |
| `GameSaves.LoadSaveGame` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283) | Load a save by name |

## Pitfalls and version notes

- **It reaches any running game with this build**, so the mod's notes ask the developer to check
  before sending commands to a game somebody is playing.
- **One private field is the whole screenshot dependency**; if it moves, captures return
  "KSA would not take a screenshot" and nothing else breaks.
- **`DoLoad` and failure.** The mod's comment says a missing file makes `DoLoad` destroy the old
  module and keep nothing. In 2026.10.10.5554 that cannot happen: `ShaderReference.Compile` throws
  for a missing file, a missing mod, renderer or path, and `ShaderModuleUtils.FromFile` throws for
  every non-success compile status and otherwise returns a module, so `DoLoad` either throws before
  it destroys anything or installs the new module and then destroys the old one.[^ksa-shaderreference][^ksa-shadermoduleutils]
  The bridge's file check is harmless, and its `catch (TargetInvocationException)` is what turns a
  compile error into a failed reply with the old module still installed.
- **Reload timing.** `reload_shaders` runs in `[StarMapAfterOnFrame]`, after this frame's command
  buffer was submitted. Destroying the old `VkShaderModule` then does not affect pipelines already
  built from it, so the dark pass keeps drawing with its old pipelines until `DarkPass.Release()`
  drops them and the next frame rebuilds against the new module (the dropped pipelines are never
  destroyed; see [the dark pass](./underground-dark-compute-pass.md)).[^mod-bridge]
- **Player-time polling** keeps the bridge responsive while paused, which `step` relies on.

## See also

- [Level horizon camera controller](./level-horizon-camera-controller.md) - how held views are driven
- [Core shader library include](./core-shader-library-include.md) - what `reload_shaders` recompiles
- [Per-mod log file](./mod-log-file.md) - the folder the bridge lives beside
- [Headless tests and CI](./headless-tests-and-ci.md) - `BridgeCommand` tests
- [Stdlib Python stdio MCP server wrapping the file bridge, with image tools](../../../howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-2.md) - how-to article demonstrating this feature
- [Load a save from a command file and wait until a craft is flown again](../../../howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-2.md) - how-to article demonstrating this feature
- [Reload a mod shader: a bridge command that invokes FileReference.DoLoad by reflection and drops cached pipelines](../../../howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/reload-a-mod-shader-in-a-running-game-1.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature
- [A lean bridge with shader hot reload, held camera shots and a UI flag instead of work in the UI pass](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-3.md) - how-to article demonstrating this feature
- [Bridge commands that load a save and step an exact number of simulated seconds](../../../howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-2.md) - how-to article demonstrating this feature
- [Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-1.md) - how-to article demonstrating this feature
- [A SceneCamera shot relative to the craft or a building, held by the dev bridge](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-3.md) - how-to article demonstrating this feature
- [Return contact sheets, GIFs and temporal-noise maps inline through an MCP server](../../../howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-2.md) - how-to article demonstrating this feature

[^mod-bridge]: src/KSAStructures/Ksa/Bridge.cs
[^mod-bridgecommand]: src/KSAStructures/Sim/BridgeCommand.cs
[^mod-ksaworld]: src/KSAStructures/Ksa/KsaWorld.cs
[^mod-build]: src/KSAStructures/Ksa/Build.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-server]: tools/ksa-mcp/server.py
[^mod-vis]: tools/vis/vis.py
[^mod-mcpjson]: .mcp.json
[^mod-deploy]: tools/deploy.sh
[^mod-bridge-tests]: tests/KSAStructures.Tests/BridgeCommandTests.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-screenshot]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-shadermoduleutils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
