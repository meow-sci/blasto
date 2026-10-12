---
type: How-To
title: Bridge commands that load a save and step an exact number of simulated seconds
description: Script a deterministic in-game check from outside by loading a save through a closure that waits for a flown craft and stepping the world by summing the engine's last simulation step
category: testing-and-tooling
group: in-game-testing
topic: run-unattended-scenarios-and-report-a-verdict
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksacars/features/file-drop-command-bridge
  - mods/ksagolf/features/file-command-bridge
  - mods/ksastructures/features/dev-file-bridge-and-mcp
techniques: [file-ipc, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, startup:game-loaded]
game_types: [KSA.GameSaves, KSA.Universe, KSA.SimSpeed, KSA.SimStep, KSA.Program]
tags: [testing, bridge, load-save, step, simulation-time, deterministic]
---

Outcome: a test script made of files, with no scenario code in the mod: `load` a named save and get a reply only when a craft is flown, `step` exactly N simulated seconds and pause, `status`/`get` and a task-specific
command to read the result, and the script itself decides pass or fail. It is the interactive alternative to [article 1](run-unattended-scenarios-and-report-a-verdict-1.md): the logic lives in the shell or Python caller instead
of in a state machine inside the mod. The two closures are identical in KSACars, KSAGolf and KSAStructures; this article follows KSACars ([file-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md)).
Prerequisite: the bridge from [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md), where the full protocol is built.

## Background: how the game does it

- **Pause is exactly zero.** `Universe.SetSimulationSpeed(SimSpeed)` assigns the speed ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703)) and `Universe.IsPaused()` is `_simulationSpeed == 0.0` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)). The `SimSpeed` constructor only *names* a tiny speed "paused" ([current/decomp/KSA/SimSpeed.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimSpeed.cs#L25-L40)); the bridge keeps pause (0) and slow motion (clamped to 0.001) as separate calls so a requested speed never becomes a pause.
  The engine has no floor: only the in-game controls stop at tenths, so a script can go slower than any player.
- **The step is simulated time actually applied.** `Universe.GetLastSimStep()` returns the `SimStep` that `ApplyVehicleSolvers` last assigned ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1948), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384)); `DeltaTime` is 0 while paused. Summing it per frame counts world time under any timewarp.
- **Loading is asynchronous in effect.** `GameSaves.LoadSaveGame` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L283-L302)) starts the replacement of the world; the craft the player flies appears frames later. `Program.ControlledVehicle` is the handle ([src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L218-L226) wraps it as `InFlight`).

## Lifecycle and timing

Both closures run in `Bridge.Update` from `[StarMapAfterOnFrame]` (`frame:after-on-frame`), once per frame, with `dtPlayer` and `dtSim` (the last step, or 0 when paused or outside a flight scene) as arguments:

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L59-L71
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
        }
```

So `step` accumulates `dtSim` frame by frame and `load` accumulates `dtPlayer` for its 3 s settle and 60 s timeout. A command's side effects (speed change, save load) land on the following frame. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **`step`.** Unpause, add each frame's `dtSim`, pause when the total reaches the request. The reply says what actually passed. A wall-time bound (`seconds * 20 + 30`) turns a stuck world into a failure:

   ```csharp
   // src/KSACars/Ksa/Bridge.cs L398-L422
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

2. **`load`.** Call `LoadSaveGame`, reject at once on an exception, then reply only when a craft is flown and at least three seconds have passed (a beat for the world to settle; it also means the reply cannot come from the instant of the call), failing after 60 s:

   ```csharp
   // src/KSACars/Ksa/Bridge.cs L424-L452
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

   ```

   KSAStructures' version additionally forgets a held camera because the craft it was set from is gone with the old world ([src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L380-L408)).

3. **`pause`, `resume`, `speed` and `status`** are one-line commands (shown in the dispatch table, [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs#L86-L100)). `status` reports `paused`, `speed` and `in_flight` so a script can assert preconditions.

4. **Write the test as a file sequence.** Adapted: command names and keys are from this bridge (`load.save`, `step.seconds`, `site` and `drive` are KSACars-specific); the save name is a placeholder.

   ```json
   {"id":"001","cmd":"load","save":"car-test"}
   {"id":"002","cmd":"pause"}
   {"id":"003","cmd":"drive","throttle":1.0,"seconds":5}
   {"id":"004","cmd":"step","seconds":5}
   {"id":"005","cmd":"status"}
   ```

   The caller (a shell loop like the one in bridge article 1, or the Python `send`) writes each file, waits for its reply, and asserts on `ok`, `data.advanced_s` and the returned state. Exit non-zero on the first `ok: false`.

5. **Expect in the log:** `bridge: load (001)`, then `bridge: step (004)`, and no `failed --` lines.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Bridge.cs) | `BeginStep`, `BeginLoad`, dispatch |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | Hook and `dtSim` computation |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs) | `SimStepSeconds`, `SetPaused`, `SetSimulationSpeed`, `InFlight` |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | The same closures in KSAGolf |

## Variations and gotchas

- **Exact to one step.** The last frame can overshoot by up to one engine step; assert on `advanced_s` rather than the request.
- **Frames the hook did not see.** KSAGolf and KSAStructures pass a *consumed* step (`ConsumeSimStep`, which differences universe time since the last consumption) because a frame in which their step hook never ran still advanced the world; KSACars passes `GetLastSimStep().DeltaTime` directly. If your bridge runs from a hook that can be skipped (the GUI hooks while the HUD is hidden), use the consumed form ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L80-L97)).
- **Timewarp.** Under 100x the step is large and the overshoot with it; pause before a precise `step` or request a small warp.
- **Auto-warp and the speed ceiling.** The `simspeed` terminal action (`Universe.SetSimulationSpeedDirect`, private) refuses a speed while KSA's own warp-to-a-time is active (`Universe.IsAutoWarpActive`) and above `SimSpeed.MaxSpeed`; the bare `Universe.SetSimulationSpeed(SimSpeed)` the bridge calls checks neither. Check `IsAutoWarpActive` (mirrored in KSAGolf's `KsaWorld`) before relying on `speed`, and keep requested speeds below `MaxSpeed` yourself; the scenario runner has an explicit option for the auto-warp case.
- **`load` of an unknown save** only logs inside the game; you see it as `no craft after 60 s`.

## See also

- [Overview](overview.md), [article 1](run-unattended-scenarios-and-report-a-verdict-1.md)
- [Set simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md), [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- [KSACars file-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md), [KSAGolf file command bridge](../../../../mods/ksagolf/features/file-command-bridge.md)
