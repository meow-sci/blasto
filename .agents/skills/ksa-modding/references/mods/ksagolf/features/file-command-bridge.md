---
type: Mod Feature
title: File command bridge and MCP server
description: Driving a running game from outside through JSON command files dropped beside the log and answered in an outbox, gated to developer installs, with a stdlib MCP server that turns the commands into agent tools
mod: ksagolf
category: testing-and-tooling
techniques: [starmap-lifecycle, reflection, file-ipc, mcp-server]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAllModsLoaded]
lifecycle: [frame:after-on-frame, frame:draw-ui, frame:celestials]
game_types: [KSA.Universe, KSA.SimSpeed, KSA.GameSaves, KSA.Vehicle, KSA.Orbit, KSA.KittenEva]
tags: [bridge, ipc, json, mcp, automation, developer-mode, agent-tooling, wsl]
---

KSAGolf can be driven by a script or an AI agent while the game keeps running: pause, step the
world for an exact number of simulated seconds, set any panel setting by name, load a save, put a
craft in orbit, hold the camera for a composed shot, pose a kitten, swing the putter, build a course
and take screenshots. Commands are JSON files written into `<user dir>/Logs/bridge/in/`; answers
appear in `.../out/`. A Python MCP server (`tools/ksa-mcp/server.py`, registered in `.mcp.json`)
wraps each command as a tool and returns pictures inline.

The bridge only exists on a developer's install: `tools/deploy.sh` writes an empty `developer` file
beside the DLL, the release package never contains one, and `Build.Developer` checks for it once at
load.[^golf-build][^golf-deploy]

## How it works

**Transport: files, not a socket.** No port, no listener, no firewall prompt, and it works across
the WSL to Windows boundary unchanged.[^golf-bridge]

| Step | Detail |
|---|---|
| Write | The client writes `<id>.json.part` then renames it to `<id>.json` so the mod never reads half a file[^golf-mcp-server] |
| Poll | Every 0.1 s of player time the mod lists `in/*.json`, takes the oldest by ordinal name, reads and deletes it |
| Parse | `Sim/BridgeCommand.TryParse`: a JSON object with an `id` that is a valid file name and a `cmd`; arguments are case-insensitive; refusals are plain strings, unit-tested headlessly[^golf-bridgecommand][^golf-bridgecommand-tests] |
| Run | Immediate commands answer in the same frame; long ones install a `_running` delegate `(dtPlayer, dtSim) -> Reply?` that is polled every frame until it returns a reply, which serialises commands into a script |
| Answer | `out/<id>.json` with `id`, `cmd`, `ok`, `error`, `data`, written as `.part` and moved into place |

| Command | What it does | Engine call |
|---|---|---|
| `status`, `get`, `set` | State, and any `Config` field by name (reflection over public fields) | |
| `pause`, `resume`, `speed` | `Universe.SetSimulationSpeed(new SimSpeed(...))`; 0 is paused | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) |
| `step` | Unpause, accumulate the consumed simulated step until `seconds` have passed, pause again | |
| `load` | `GameSaves.LoadSaveGame(name)`, then wait until a craft is flown | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs) |
| `orbit_place` | Circular orbit over the sunlit side or beside another craft: `Orbit.CreateFromStateCci` then `Vehicle.Teleport(orbit, attitude, bodyRates)` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) |
| `frame` | Holds the main view on a `SceneCamera` (east/north/up of the flown craft, aimed at a craft or body, with a FOV); `release` hands it back | see [Visual testing captures](visual-testing-captures.md) |
| `capture`, `player_capture` | Screenshots with manifests | see [Visual testing captures](visual-testing-captures.md) |
| `kitten_pose`, `kitten_prop` | Bone turns through the pose-processor list, meshes on cosmetic attachments | see [Kitten pose processor](kitten-pose-processor.md), [Kitten held props](kitten-held-props.md) |
| `golf` | `drop`, `pickup`, `address`, a `swing` array of mouse dx per frame, `wait_s` | see [Putting session](putting-session.md) |
| `course` | `enter`, `place`, `rotate`, `mirror`, `undo`, `save`, `load`, aim at cell `i`,`j` | see [Minigolf course builder](minigolf-course-builder.md) |

The MCP server is stdlib-only Python speaking JSON-RPC over stdio. It finds the user directory with
`tools/ksa-user-dir.sh`, launches the game through `tools/run.sh` only if none is running, never
closes a game it did not launch, converts Windows paths in replies to WSL paths, and adds
client-side composites such as `variants` (pause, set a field, capture, restore, diff).[^golf-mcp-server]
`.mcp.json` registers it as server `ksa`.[^golf-mcp-json]

## Lifecycle and timing

| Call | Hook | Why there |
|---|---|---|
| `Bridge.Update(dtPlayer, dtSim)` | `[StarMapAfterOnFrame]`, Harmony postfix on `Program.OnFrame` (`frame:after-on-frame`), after `StepOnce` | It is the one hook KSA always calls, so commands run with the HUD hidden (screenshots hide it) and in menus; running after the step means a command sees this frame's world and simulated step[^golf-mod] |
| `Bridge.DriveCamera()` | Inside `StepOnce`, i.e. the first of the `StarMapAfterGui` postfix, the `Program.OnFrameCelestials` prefix or the frame postfix | Moving a borrowed camera is part of the step, before the render reads the camera |
| Construction | `[StarMapAllModsLoaded]`, only if `Build.Developer` | No bridge object, no polling, on a player install |
| "Capture for Claude" button | Sets a static flag in the UI pass; the next `Update` starts the capture | A capture must not start inside the UI pass |

`dtSim` passed to the bridge is the consumed simulation step (zero when paused or outside flight),
so `step` measures simulated time the engine actually applied, not wall time.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | Inbox poll, command table, long-running command delegates, atomic replies |
| [src/KSAGolf/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/BridgeCommand.cs) | Parsing, typed argument access, `Config` field get/set by reflection |
| [src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs#L14-L33) | `developer` marker file check |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L57-L61) | Bridge constructed only on a developer install |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-mcp/server.py) | MCP server and CLI over the bridge |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/deploy.sh#L64-L67) | Writes the `developer` marker |

## Snippets

```csharp
// src/KSAGolf/Ksa/Bridge.cs L81-L123
    public void Update(double dtPlayer, double dtSim)
    {
        try
        {
            if (_running is not null && _current is not null)
            {
                if (_running(dtPlayer, dtSim) is { } reply) Answer(_current, reply);
                return;
            }

            if (_playerAsked)
            {
                _playerAsked = false;
                StartPlayerCapture();
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

```csharp
// src/KSAGolf/Ksa/Bridge.cs L594-L618
    private Reply? BeginStep(BridgeCommand command)
    {
        double seconds = command.Number("seconds", 1.0);
        if (!(seconds > 0.0)) return Failed("seconds must be positive");

        double advanced = 0.0;
        double waited = 0.0;
        KsaWorld.SetPaused(false);

        _running = (dtPlayer, dtSim) =>
        {
            waited += dtPlayer;
            advanced += Math.Max(dtSim, 0.0);

            if (advanced >= seconds)
            {
                KsaWorld.SetPaused(true);
                return Done(new() { ["advanced_s"] = Math.Round(advanced, 3) });
            }

            return waited > (seconds * 20.0) + 30.0 ? Failed($"only {advanced:F2} s passed") : null;
        };

        return null;
    }
```

```python
# tools/ksa-mcp/server.py L70-L97
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

| API | Decomp | Purpose |
|---|---|---|
| `Universe.SetSimulationSpeed(SimSpeed)`, `Universe.IsPaused()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | Pause, resume, speed, step |
| `GameSaves.LoadSaveGame(string)` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs) | The same entry point as the console `load` command |
| `Orbit.CreateFromStateCci`, `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | `orbit_place` |
| `Program.OnFrame` (StarMap postfix) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | The always-called hook the bridge runs from |

## Pitfalls and version notes

- Two developer installs built from the same tooling share `Logs/bridge`, and whichever mod reads a
  command first answers it; missing reply fields or a capture that never answers point to another
  mod taking the command.[^golf-claude-md]
- The bridge lives on the frame postfix, which runs after the render; a command's effect on the
  world is drawn on the next frame.
- `GameSaves.LoadSaveGame` is called from the frame postfix; `Universe.DeserializeSave` sets the
  simulation speed to 0 and waits for all job systems before destroying vehicles, then sets it back
  to 1.[^ksa-universe] The bridge waits three seconds after a craft appears before answering.
- **`orbit_place` races the vehicle solver.** It calls `Vehicle.Teleport(orbit, attitude, rates)`
  directly from the frame postfix, when the next vehicle step is already running on worker threads.
  With an orbit, `Teleport` calls `RemoveFromCurrentBubble` -> `PhysicsBubble.RemoveVehicle`, which
  removes the vehicle's state from the bubble's `_vehicleStates` list and from its `ConstraintSim`;
  `VehicleUpdateTask.Run` is iterating and stepping those on the workers at that moment. The mod's
  own `KsaWorld.WaitForVehicleSolvers` comment describes the resulting "Collection was modified"
  failure for a vehicle dispose. The engine's own `Vehicle.TeleportToLocation` instead queues an
  `InputEvents.TeleportInputData` that `InputEvents.ApplyInputEvents` applies in the next
  `PrepareFrame`, before `ExecuteNextVehicleSolvers`; queuing a `TeleportInputData` with the orbit
  and attitude would avoid the race.[^ksa-vehicle][^ksa-inputevents][^ksa-physicsbubble]
- C# changes still need a game restart; the bridge saves relaunches for questions, not for code.[^golf-visual-testing]

## See also

- [Visual testing captures](visual-testing-captures.md)
- [Developer panel and menu-bar entry](developer-panel-and-menu-bar.md)
- [Mod log file with rotation and a verbose toggle](mod-log-file-with-rotation.md)
- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Packaging, deploy and release pipeline](packaging-and-release.md)
- [Stdlib Python stdio MCP server wrapping the file bridge, with image tools](../../../howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-2.md) - how-to article demonstrating this feature
- [Load a save from a command file and wait until a craft is flown again](../../../howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-2.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature
- [Get and set every config field by reflection, and script gameplay as composed commands](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-2.md) - how-to article demonstrating this feature
- [Bridge commands that load a save and step an exact number of simulated seconds](../../../howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-2.md) - how-to article demonstrating this feature

[^golf-bridge]: src/KSAGolf/Ksa/Bridge.cs
[^golf-bridgecommand]: src/KSAGolf/Sim/BridgeCommand.cs
[^golf-bridgecommand-tests]: tests/KSAGolf.Tests/BridgeCommandTests.cs
[^golf-build]: src/KSAGolf/Ksa/Build.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-mcp-server]: tools/ksa-mcp/server.py
[^golf-mcp-json]: .mcp.json
[^golf-deploy]: tools/deploy.sh
[^golf-visual-testing]: docs/VISUAL-TESTING.md
[^golf-claude-md]: CLAUDE.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
