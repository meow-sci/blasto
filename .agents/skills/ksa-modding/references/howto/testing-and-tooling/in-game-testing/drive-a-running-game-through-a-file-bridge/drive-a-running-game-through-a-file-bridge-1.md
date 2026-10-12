---
type: How-To
title: A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures
description: Build the reference command bridge that polls a JSON inbox from StarMapAfterOnFrame, steps an exact number of simulated seconds, loads a save and is constructed only on a developer install
category: testing-and-tooling
group: in-game-testing
topic: drive-a-running-game-through-a-file-bridge
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/file-command-bridge
  - mods/ksacars/features/file-drop-command-bridge
  - mods/ksarmory/features/file-command-bridge-and-mcp
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/unattended-scenario-runner
  - mods/ksagolf/features/putting-session
  - mods/ksarmory/features/build-deploy-package-release
  - mods/ksarmory/features/own-log-file-and-developer-switch
techniques: [starmap-lifecycle, file-ipc, reflection]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterOnFrame]
lifecycle: [startup:all-mods-loaded, frame:after-on-frame]
game_types: [KSA.Universe, KSA.SimSpeed, KSA.SimStep, KSA.GameSaves, KSA.Program, KSA.Constants]
tags: [testing, bridge, file-ipc, developer-only, step, load-save, marker-file]
---

Outcome: a folder-based command channel into a running KSA. A client writes `{"id":"1","cmd":"step","seconds":5}`, the mod runs it on a
frame, and `out/1.json` appears with `ok`, `error` and `data`. Commands run strictly one at a time, so a numbered list of files is a
script. You also get the two commands every test script needs, `step` (advance exactly N simulated seconds) and `load` (load a save
and wait until a craft is flown), and a developer-install gate that keeps all of it off players' machines.

Prerequisite: a StarMap mod with a working frame hook. This is the pattern in [KSAGolf](../../../../mods/ksagolf/features/file-command-bridge.md)
(shared `Logs/bridge`) and [KSACars](../../../../mods/ksacars/features/file-drop-command-bridge.md) (per-mod `Logs/bridge/KSACars`);
KSAStructures and KSArmory use the same code, see [article 3](drive-a-running-game-through-a-file-bridge-3.md).

## Background: how the game does it

KSA has no external control channel of its own. What it has are the terminal actions the console registers, and the bridge calls the
same public methods those actions call:

- `GameSaves.LoadSaveGame(string)` is registered as the terminal action `load` and logs `save '<name>' not found` instead of throwing
  when the name is unknown ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283-L302)).
- `Universe.SetSimulationSpeed(SimSpeed)` assigns the speed field and nothing else ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703)).
  `Universe.IsPaused()` is `_simulationSpeed == 0.0` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)). The `SimSpeed` constructor only
  *names* anything under 1e-4 "paused" ([current/decomp/KSA/SimSpeed.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimSpeed.cs#L25-L40)), which is why the mods keep pause (exactly 0) and slow-motion (clamped to a floor) as separate calls.
- `Universe.GetLastSimStep()` returns the `SimStep` (`PreviousTime`, `NextTime`, `DeltaTime`) that `ApplyVehicleSolvers` last applied
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384), [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs#L3-L10)). That is simulated, not wall, time.
- The user folder is `Constants.DocumentsFolderPath` = `My Games/Kitten Space Agency` under Documents
  ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283)); the mods search for it themselves and put the bridge under a `Logs` folder there.

## Lifecycle and timing

| What | Where | Why |
|---|---|---|
| Construct the bridge | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), only if `Build.Developer` | No object, no polling and no file access at all on a player install |
| Poll and run | `[StarMapAfterOnFrame]` (`frame:after-on-frame`) on the main thread | The one hook KSA always calls: it runs in menus, with the HUD hidden and during a screenshot, when `StarMapAfterGui` does not. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) |
| `dtSim` argument | The consumed step, `0` when paused or outside a flight scene | `step` sums it; it is simulated time the engine really applied |
| Load a save | Called from the frame postfix; the world is rebuilt during and after the call | The closure waits for a flown craft instead of assuming |

Because the postfix runs after the render and while the next physics step is already executing on worker threads
(`frame:after-on-frame`), a command's effect on the world is drawn on the following frame, and a command must not write simulated state
that the solver is reading. Safe commands: speed, save load, camera, settings. For anything that mutates vehicles, queue it for a safe point
(see [Change vehicles without racing the solver](../../../vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/overview.md)).

## Steps

1. **Gate the whole feature on a marker file.** Define `Build.Developer` once, from a file named `developer` beside the DLL:

   ```csharp
   // src/KSAGolf/Ksa/Build.cs L14-L33
       /// <summary>
       /// Whether this is a developer's install: a <c>developer</c> file beside the assembly, which
       /// <c>tools/deploy.sh</c> writes and the release archive never carries. It shows the tools
       /// that exist for working on the mod -- the bridge, the scenario runner, the research
       /// switches -- which a player has no use for.
       /// </summary>
       public static bool Developer { get; } = ResolveDeveloper();

       private static bool ResolveDeveloper()
       {
           try
           {
               string? dir = Path.GetDirectoryName(typeof(Build).Assembly.Location);
               return dir is not null && File.Exists(Path.Combine(dir, "developer"));
           }
           catch
           {
               return false;
           }
       }
   ```

   Have the deploy script, and only the deploy script, create it:

   ```bash
   # tools/deploy.sh L64-L67
   # Marks this as a developer's install, which starts the bridge and shows the panel's developer
   # controls (Build.Developer). package.sh stages from the build output, never from here, so
   # the release archive cannot carry it.
   : > "$TARGET/developer"
   ```

   KSArmory, KSACars and KSAStructures use the same lines in their own `tools/deploy.sh` (e.g. [tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh#L68-L71)). The release
   archive is staged from the build output folder, never from the install folder, so nothing writes `developer` into it; the
   package script's own gates are for `.pdb` files and stray DLLs ([tools/package.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/package.sh#L60-L72)).

2. **Construct the bridge only when the gate is open:**

   ```csharp
   // src/KSAGolf/Ksa/KSAGolfMod.cs L57-L61
           if (Build.Developer) _bridge = new Bridge(_config, _golf, _builder);

           Log.Info(Build.Developer
                        ? "developer install: the bridge and the developer controls are on"
                        : "player install");
   ```

3. **Pick the folders.** The bridge lives under the mod's log folder, resolved by the mod's own `Log` class (Documents or `XDG_DATA_HOME`,
   falling back to temp: [src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs#L185-L260)). KSACars and KSAStructures add a per-mod
   subfolder, `Path.Combine(Log.Folder, "bridge", "KSACars")`, so two mods built from the same tooling never answer each other's commands
   ([src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L32-L34)); KSAGolf and KSArmory share `Logs/bridge`
   ([src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L68-L70)), and whichever mod reads a file first answers it. Prefer the per-mod subfolder.

4. **Parse in a KSA-free type.** `BridgeCommand.TryParse` needs only `System.Text.Json`, requires an `id` usable as a file name
   and a `cmd`, keeps arguments in a case-insensitive dictionary and returns a plain-string reason on refusal. Because it names no game type it is
   unit-tested ([Unit-test mod logic without launching the game](../../offline-tests/unit-test-mod-logic-without-game/overview.md)):

   ```csharp
   // src/KSAGolf/Sim/BridgeCommand.cs L29-L72
       public static bool TryParse(string text, out BridgeCommand? command, out string trouble)
       {
           command = null;
           trouble = string.Empty;

           try
           {
               using JsonDocument doc = JsonDocument.Parse(text);
               if (doc.RootElement.ValueKind != JsonValueKind.Object)
               {
                   trouble = "a command is a JSON object";
                   return false;
               }

               Dictionary<string, JsonElement> args = new(StringComparer.OrdinalIgnoreCase);
               foreach (JsonProperty property in doc.RootElement.EnumerateObject())
               {
                   args[property.Name] = property.Value.Clone();
               }

               string id = Text(args, "id");
               string name = Text(args, "cmd").ToLowerInvariant();

               if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
               {
                   trouble = "a command needs an id that can be a file name";
                   return false;
               }

               if (name.Length == 0)
               {
                   trouble = "a command needs a cmd";
                   return false;
               }

               command = new BridgeCommand(args) { Id = id, Name = name };
               return true;
           }
           catch (JsonException e)
           {
               trouble = $"not JSON: {e.Message}";
               return false;
           }
       }
   ```

5. **Poll once per 0.1 s of player time and run one command at a time.** A running command is a closure `(dtPlayer, dtSim) -> Reply?`; while one
   is installed nothing else is read, which is what makes a sequence of files a script:

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

6. **Answer atomically.** Write `<id>.json.part`, then `File.Move` it over the final name, so the client never reads half a reply:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L213-L236
       private void Answer(BridgeCommand command, Reply reply)
       {
           _running = null;
           _current = null;

           Directory.CreateDirectory(Outbox);

           Dictionary<string, object?> body = new()
           {
               ["id"] = command.Id,
               ["cmd"] = command.Name,
               ["ok"] = reply.Ok,
               ["error"] = reply.Ok ? null : reply.Error,
               ["data"] = reply.Data,
           };

           // Written aside and moved, so the other end never reads half a reply.
           string final = Path.Combine(Outbox, command.Id + ".json");
           string partial = final + ".part";
           File.WriteAllText(partial, JsonSerializer.Serialize(body, JsonOptions));
           File.Move(partial, final, overwrite: true);

           if (!reply.Ok) Log.Warn($"bridge: {command.Name} ({command.Id}) failed -- {reply.Error}");
       }
   ```

7. **Dispatch.** A `switch` on the command name returns either a `Reply` (answer now) or `null` after installing `_running`:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L184-L211
       private void Start(BridgeCommand command)
       {
           _current = command;
           Log.Info($"bridge: {command.Name} ({command.Id})");

           Reply? now = command.Name switch
           {
               "status" => Status(),
               "pause" => KsaWorld.SetPaused(true) ? Done() : Failed("the world would not pause"),
               "resume" => KsaWorld.SetPaused(false) ? Done() : Failed("the world would not resume"),
               "speed" => KsaWorld.SetSimulationSpeed(command.Number("x", 1.0)) ? Done() : Failed("speed refused"),
               "set" => Set(command),
               "get" => Get(command),
               "kitten_pose" => KittenPose(command),
               "kitten_prop" => KittenProp(command),
               "golf" => GolfCommand(command),
               "course" => CourseCommand(command),
               "player_capture" => PressTheButton(),
               "frame" => Frame(command),
               "orbit_place" => OrbitPlace(command),
               "step" => BeginStep(command),
               "capture" => BeginCapture(command),
               "load" => BeginLoad(command),
               _ => Failed($"no command '{command.Name}'"),
           };

           if (now is { } reply) Answer(command, reply);
       }
   ```

8. **Call it from the frame postfix with the simulated step.** Passing 0 while paused or outside a flight scene means a `step` closure can just sum the argument:

   ```csharp
   // src/KSAGolf/Ksa/KSAGolfMod.cs L74-L96
       [StarMapAfterOnFrame]
       public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
       {
           try
           {
               if (_disabled || _ui is null) return;

               if (KsaWorld.InFlight)
               {
                   double speed = KsaWorld.SimulationSpeed;
                   if (Math.Abs(speed - _lastSimSpeed) > 1e-9)
                   {
                       Log.Info($"simulation speed {_lastSimSpeed:F2}x -> {speed:F2}x"
                                + (KsaWorld.IsPaused ? " (paused)" : ""));
                       _lastSimSpeed = speed;
                   }
               }

               // A no-op on every frame the GUI pass ran. See StepOnce.
               StepOnce(dtPlayer);

               // After the step, so a command sees this frame's world and its simulated step.
               _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? _lastSimStep : 0.0);
   ```

9. **`pause`, `resume` and `speed`** are one call each. KSAGolf keeps "paused" (exactly 0) and "slow" (clamped to 0.001) as separate methods so a speed argument never silently becomes a pause:

   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L161-L167
       public static bool SetSimulationSpeed(double speed)
       {
           if (!double.IsFinite(speed) || speed <= 0.0) return false;

           Universe.SetSimulationSpeed(new SimSpeed(Math.Max(speed, SlowestSimSpeed)));
           return true;
       }
   ```
   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L182-L193
       public static bool SetPaused(bool paused)
       {
           try
           {
               Universe.SetSimulationSpeed(new SimSpeed(paused ? 0.0 : 1.0));
               return true;
           }
           catch
           {
               return false;
           }
       }
   ```

   `SetSimulationSpeed` has no engine floor; KSA's own UI rolls in tenths, so a bridge can go slower than any player can. A paused world means `Universe.GetLastSimStep().DeltaTime` is 0, which the bridge relies on.

10. **`step`: advance an exact number of simulated seconds.** Unpause, add each frame's `dtSim`, pause when the sum reaches the request, and fail after a wall-time bound so a stuck world cannot hang the script:

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

    The result is exact to within one engine step (the last frame can overshoot by up to one `DeltaTime`); the reply carries `advanced_s` so a test can assert it. KSACars and KSAStructures do the same ([src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L398-L422)).

11. **`load`: load a save and wait until a craft is flown.** `LoadSaveGame` returns immediately and the world is rebuilt over the next frames, so reply from a closure:

    ```csharp
    // src/KSAGolf/Ksa/Bridge.cs L620-L651
        private Reply? BeginLoad(BridgeCommand command)
        {
            string save = command.String("save");
            if (save.Length == 0) return Failed("load needs a save");

            try
            {
                GameSaves.LoadSaveGame(save);
            }
            catch (Exception e)
            {
                return Failed($"could not load '{save}': {e.Message}");
            }

            _scene = null;
            double waited = 0.0;

            _running = (dtPlayer, _) =>
            {
                waited += dtPlayer;

                // A beat past the craft appearing, for the world to settle round it.
                if (waited > 3.0 && KsaWorld.InFlight)
                {
                    return Done(new() { ["craft"] = KsaWorld.DisplayName(KsaWorld.ControlledVehicle!) });
                }

                return waited > 60.0 ? Failed("no craft after 60 s") : null;
            };

            return null;
        }
    ```

    The three-second beat after the craft appears lets the world settle. An unknown save name only logs an error inside the game, so the failure surfaces here as `no craft after 60 s`.

12. **Write the client.** The reference client is 25 lines of Python: write `<id>.json.part`, rename, poll `out/<id>.json`, delete it, raise on `ok: false`:

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

    The same from a shell (adapted from that function; the user folder comes from [tools/ksa-user-dir.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-user-dir.sh), which searches `/mnt/c/Users` under WSL and the XDG folders on Linux):

    ```bash
    # adapted from KSAGolf/tools/ksa-mcp/server.py send()
    BR="$(tools/ksa-user-dir.sh)/Logs/bridge"
    id=$(date +%s%N)
    printf '{"id":"%s","cmd":"step","seconds":5}' "$id" > "$BR/in/$id.json.part"
    mv "$BR/in/$id.json.part" "$BR/in/$id.json"
    until [ -f "$BR/out/$id.json" ]; do sleep 0.05; done
    cat "$BR/out/$id.json"      # {"id":"...","cmd":"step","ok":true,"data":{"advanced_s":5.0..}}
    ```

13. **Check the log.** Each command logs `bridge: step (<id>)` when it starts and `bridge: <cmd> (<id>) failed -- <reason>` when it is refused. On a player install the log says `player install` and the `in/` folder is never read ([src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L59-L61)).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | Inbox poll, command table, closures, atomic replies |
| [src/KSAGolf/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/BridgeCommand.cs) | KSA-free command parser |
| [src/KSAGolf/Ksa/Build.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Build.cs) | `Developer` marker check |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/deploy.sh) | Writes the marker on install |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-mcp/server.py) | Python client (`send`) and MCP server |
| [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs) | Same protocol with a per-mod folder, plus `spawn`, `drive`, `site` |

## Variations and gotchas

- **Two mods, one folder.** With the shared `Logs/bridge` two developer installs fight over files; a command answered by the wrong mod looks like missing reply fields. Use `bridge/<ModName>/`.
- **Stale files.** Delete leftovers from `in/` before a session; the oldest name goes first, so a stale `step` runs before your command.
- **`dtSim` is 0 in menus.** A `step` issued before a flight scene is loaded just waits, then fails after `seconds * 20 + 30` wall seconds. Issue `load` first.
- **`load` can only fail late.** The engine logs an unknown save name; the bridge learns only from its timeout.
- **`Logs` vs `logs`.** The decompiled `Constants.LogsFolderPath` is `logs`, the mods write `Logs`; harmless on Windows, a separate folder on a case-sensitive file system. Use one spelling in the mod and in the client.
- **Do not mutate vehicles from the postfix.** KSAGolf's own `orbit_place` calls `Vehicle.Teleport` here and races the vehicle solver, as its feature page documents ([note](../../../../mods/ksagolf/features/file-command-bridge.md)); queue teleports for a safe point instead.
- **C# changes still need a restart.** The bridge saves relaunches for questions, not for code. Shader changes can be hot reloaded, see [article 3](drive-a-running-game-through-a-file-bridge-3.md).
- **Game updates.** `GameSaves.LoadSaveGame`, `Universe.SetSimulationSpeed`, `Universe.GetLastSimStep` and `Universe.IsPaused` are public; the only reflection the reference bridge needs is the screenshot field covered in [Take clean screenshots from code](../take-screenshots-from-code/overview.md).

## See also

- [Overview](overview.md), [article 2: generic get/set and composed commands](drive-a-running-game-through-a-file-bridge-2.md), [article 3: lean variant](drive-a-running-game-through-a-file-bridge-3.md), [article 4: manifest key](drive-a-running-game-through-a-file-bridge-4.md), [article 5: DEBUG flag](drive-a-running-game-through-a-file-bridge-5.md)
- [Fly scripted scenarios unattended](../run-unattended-scenarios-and-report-a-verdict/overview.md) (uses this bridge's `load` and `step` closures)
- [Set simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md)
- Mod features: [KSAGolf file command bridge](../../../../mods/ksagolf/features/file-command-bridge.md), [KSACars file-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md), [KSArmory unattended scenario runner](../../../../mods/ksarmory/features/unattended-scenario-runner.md), [KSArmory own log file and developer switch](../../../../mods/ksarmory/features/own-log-file-and-developer-switch.md), [KSArmory file command bridge and MCP](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md), [KSAStructures dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [KSAGolf putting session](../../../../mods/ksagolf/features/putting-session.md)
