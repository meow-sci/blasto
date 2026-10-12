---
type: Mod Feature
title: File-drop command bridge
description: Drive a running game from outside by dropping JSON command files into a folder beside the mod's log and reading JSON replies back, polled once a frame from the frame postfix, with multi-frame commands for stepping the world, spawning craft, driving cars and taking screenshots
mod: ksacars
category: testing-and-tooling
techniques: [starmap-lifecycle, reflection, file-ipc]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterOnFrame, reflection KSA.Program._screenshotCapture]
lifecycle: [startup:all-mods-loaded, frame:after-on-frame]
game_types:
  - KSA.GameSaves
  - KSA.Universe
  - KSA.Vehicle
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.VehicleSave
  - KSA.Orbit
  - KSA.ConstraintSim
  - KSA.EVADoor
  - KSA.CrewAssignmentWindow
  - KSA.ScreenshotCapture
  - KSA.Program
  - KSA.Constants
  - KSA.Celestial
tags: [testing, automation, bridge, json, screenshots, spawning, save-load, developer-tools]
---

KSACars has a developer-only "bridge" that lets an agent or script in a terminal operate a game that
stays running: load a save, pause, step the world a set number of simulated seconds, park a car at
a latitude and longitude, seat kittens, drive with held inputs, flip and unflip, switch lights and
scoops, read back a full telemetry report, and take screenshots with a manifest of what each shows.
The transport is a pair of folders: commands are JSON files dropped into `in/`, replies appear in
`out/`. "Files, not a socket: a folder needs no port and works across the WSL boundary."[^ksacars-bridge]
The [MCP server](./ksa-mcp-server.md) is the other end.

## How it works

**Enabling.** `KSACarsMod.OnFullyLoaded` creates the bridge only when `Build.Developer` is true,
that is, when a `developer` file sits beside the DLL (written by `tools/deploy.sh`, never shipped).[^ksacars-mod]

**Folders.** `<log folder>/bridge/KSACars/in` and `.../out`, under the same folder the
[mod log](./mod-log-file.md) resolved; a per-mod subfolder so two mods built from the same tooling
never answer each other's commands.

**Protocol.**

| Step | Detail |
|---|---|
| Command file | `in/<anything>.json`: a JSON object with `id` (must be a valid file name), `cmd`, and arguments beside them. Writers write `<id>.json.part` and rename, so the mod never reads half a file |
| Pickup | Every 0.1 s of player time, when no command is running: the lexically first `*.json` in `in/` is read and deleted |
| Parsing | `BridgeCommand.TryParse` (KSA-free, unit tested) validates and exposes `Number`, `String`, `Flag`, `Has`[^ksacars-bridgecommand] |
| Reply | `out/<id>.json` with `id`, `cmd`, `ok`, `error`, `data`; written to `.part` then `File.Move`d |
| Ordering | One command at a time; a multi-frame command holds the queue until it answers, so a sequence of files is a script |

**Commands** (`Bridge.Start` switch): `status`, `pause`, `resume`, `speed`, `site` (teleport a craft
to lat/lon via `Vehicle.TeleportToLocation`), `step` (unpause until `seconds` of simulated time have
passed, counted from `Universe.GetLastSimStep().DeltaTime`, then pause), `capture`, `load`
(`GameSaves.LoadSaveGame`, then wait until a craft is flown), `spawn`, `drive`, `ground` (height
field survey, seabed depth), `save` (`GameSaves.MakeUncompressedSave`).[^ksa-gamesaves]

**Spawning** (`CraftSpawner.SpawnParked`): loads a craft from `DefaultVehicleSaves.FindSave` or the
player's `VehicleSaves`, builds an orbit 500 m above the flown craft with
`Orbit.CreateFromStateCci`, and inside `using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking()`
calls `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, root, orbit)`, adds it to
`parent.Children`, recomputes derived data, and calls `TeleportToLocation` to set it on the ground.
The mod's notes explain why `parent.Children.Add` is required (otherwise `UpdatePerFrameData` never
runs and the craft sits at the barycentre).[^ksacars-craftspawner][^ksacars-notes]

**Driving and crew** (`drive`): `Buggies.Hold` overrides the keys for N simulated seconds;
`seat_kittens` calls `Vehicle.SetSeatCrew`, `crew=true` calls `CrewAssignmentWindow.FillSeats` (the
launch window's Fill Seats), `eva=true` calls `EVADoor.PerformEvaForSeat`; the reply is
`Buggies.Report`, a dictionary of speed, RPM, gear, steer, suspension travel, hub heights, rails
state, crew, hand-IK miss distance and more.[^ksacars-buggies][^ksa-evadoor][^ksa-crewassignmentwindow]

**Screenshots** (`capture`): `KsaWorld.TryRequestScreenshot` reflects the private
`Program._screenshotCapture` once and calls the public `ScreenshotCapture.Request(scale, flags)` with
`warm=24`, which makes KSA hide the UI for 24 warm-up frames before capturing. The bridge then waits
for a new `ksa_*.png` in `<Constants.DocumentsFolderPath>/exports/screenshots`, moves it into
`out/<id>/NN-label.png`, and writes a JSON manifest (time, pause, speed, FOV, craft and its screen
position) for the frame the picture was taken. Series can be spaced in simulated seconds (stepping
a paused world between frames) or rendered frames.[^ksacars-ksaworld][^ksa-screenshotcapture]

## Lifecycle and timing

`Bridge.Update(dtPlayer, dtSim)` is called from `[StarMapAfterOnFrame]` (a postfix on
`Program.OnFrame`, after the render), "the one hook KSA always calls, so it runs in the menu and with
the UI hidden"; `dtSim` is `Universe.GetLastSimStep().DeltaTime` in a flight scene and 0 when paused
or in menus.[^ksacars-mod] It runs on the main thread after the frame's car step, "so a command sees
this frame's world". A running command is a closure `Func<double, double, Reply?>` advanced once per
frame until it returns a reply, which is how `step`, `load` and `capture` span many frames without
blocking the game.

Like every StarMap frame hook, the bridge runs while the physics step dispatched earlier in the
frame is in flight, so no command writes vehicle state directly. Effects land at the following
points: input holds and righting are consumed by the next `Vehicle.PrepareWorker` prefix;
`TeleportToLocation` is queued through `InputEvents.TeleportInputBuffer` and applied in the next
`PrepareFrame`; a screenshot request is picked up by `ScreenshotCapture.OnPrepareFrame` at the start
of the next frame.[^ksa-program] The one exception is `spawn`: it builds the craft at once inside
`ConstraintSim.UnlockShapesBlocking`, which spins until the in-flight vehicle step releases the
shape registry, so a spawn stalls that frame on the step (see
[spawning a parked craft](./spawn-parked-craft.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs) | Polling, dispatch, multi-frame commands, atomic replies, capture manifests |
| [src/KSACars/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BridgeCommand.cs) | KSA-free JSON command parsing |
| [tests/KSACars.Tests/BridgeCommandTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BridgeCommandTests.cs) | Refusals tested headlessly[^ksacars-bridgecommand-tests] |
| [src/KSACars/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftSpawner.cs) | Library craft parked at lat/lon |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L391-L441) | Screenshot request through the reflected capture object |

## Snippets

```csharp
// src/KSACars/Ksa/Bridge.cs L45-L80
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

```csharp
// src/KSACars/Ksa/CraftSpawner.cs L32-L42
            Orbit orbit = Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), posCci, velCci,
                                                   new byte4(255, 80, 80, 255));

            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();
            Vehicle craft = Vehicle.CreateVehicle(system, platform.Body2Cce, bodyRates: default,
                                                  parent, id, parts.Root, orbit);
            parent.Children.Add(craft);
            craft.Parts.RecomputeAllDerivedData();
            craft.UpdateAfterPartTreeModification();
            craft.UpdatePerFrameData();
            craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);
```

```csharp
// src/KSACars/Ksa/KsaWorld.cs L406-L430
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

## Game APIs touched

| API | Purpose |
|---|---|
| `GameSaves.LoadSaveGame`, `MakeUncompressedSave` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L274-L290)) | Console `load` and `save` equivalents |
| `ScreenshotCapture.Request(scale, flags)` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L92-L125)) and `Program._screenshotCapture` (private) | Framebuffer capture with `warm=N` UI-hiding warm-up |
| `Vehicle.CreateVehicle(system, body2Cce, bodyRates, parent, id, root, orbit)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1426)) | Build a craft from a part tree[^ksa-vehicle] |
| `Vehicle.TeleportToLocation`, `Orbit.CreateFromStateCci`, `IParentBody.Children` | Place it on the ground; give it an orbit; join the orbiter tree |
| `ConstraintSim.UnlockShapesBlocking` ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L135)) | Hold the Bepu shapes registry while building a vehicle[^ksa-constraintsim] |
| `DefaultVehicleSaves.FindSave`, `VehicleSaves.Refresh` / `AsSpan`, `VehicleSave.Load` | Load a craft from either library |
| `Vehicle.SetSeatCrew`, `CrewAssignmentWindow.FillSeats` ([current/decomp/KSA/CrewAssignmentWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CrewAssignmentWindow.cs#L176)), `EVADoor.PerformEvaForSeat` ([current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L109)) | Seat kittens; get one out |
| `Universe.SetSimulationSpeed`, `IsPaused`, `GetLastSimStep`, `Constants.DocumentsFolderPath` | Pause, speed, simulated time, screenshot folder |

## Pitfalls and version notes

- **Developer installs only.** A marker file gates it, so players never get a folder-watching
  command channel.
- **Write atomically on both sides** (`.part` then rename/move), or a reader can see half a file.
- **Count simulated time from `GetLastSimStep().DeltaTime`**, not the player clock; `step` waits on
  the world, with a timeout of 20x the request plus 30 s.
- **Screenshots go through KSA's own capture**, which names files to the second; the bridge moves
  each file away before requesting the next so two captures cannot collide.
- **Reflection on `Program._screenshotCapture`** is resolved once and degrades to "no captures" with
  a warning if KSA renames it.
- **Spawning a vehicle needs more than `CreateVehicle`**: add it to the parent's children (and, per
  the mod's notes, to a physics bubble for a craft in flight); the bridge relies on
  `TeleportToLocation` to put it in the world.[^ksacars-notes]
- The bridge loads saves from the frame postfix; KSA itself loads saves from UI code. This has worked
  for the authors through the bridge, but no engine guarantee covers it (unverified beyond the repo's
  own use).

## See also

- [KSA MCP server over the bridge](./ksa-mcp-server.md)
- [Mod log file](./mod-log-file.md)
- [Vehicle library presets](./vehicle-library-presets.md)
- [Spawn a saved craft parked at a latitude and longitude](./spawn-parked-craft.md) (`spawn` in detail)
- [Once-per-frame car step and registry](./frame-step-latch-and-car-registry.md) (the step the bridge runs after)
- [Drive with the flight keys](./drive-with-flight-keys.md) (`Buggies.Hold`)
- [Headless simulation tests](./headless-sim-tests.md)
- [Load a save from a command file and wait until a craft is flown again](../../../howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-2.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature
- [Bridge commands that load a save and step an exact number of simulated seconds](../../../howto/testing-and-tooling/in-game-testing/run-unattended-scenarios-and-report-a-verdict/run-unattended-scenarios-and-report-a-verdict-2.md) - how-to article demonstrating this feature
- [Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-1.md) - how-to article demonstrating this feature

[^ksacars-bridge]: src/KSACars/Ksa/Bridge.cs
[^ksacars-bridgecommand]: src/KSACars/Sim/BridgeCommand.cs
[^ksacars-bridgecommand-tests]: tests/KSACars.Tests/BridgeCommandTests.cs
[^ksacars-craftspawner]: src/KSACars/Ksa/CraftSpawner.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-gamesaves]: KSA.GameSaves (decompiled)
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-evadoor]: KSA.EVADoor (decompiled)
[^ksa-crewassignmentwindow]: KSA.CrewAssignmentWindow (decompiled)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled)
[^ksa-program]: KSA.Program (decompiled)
