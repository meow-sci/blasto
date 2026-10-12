---
type: Mod Feature
title: Per-vehicle autopilot step in a Vehicle.PrepareWorker prefix
description: Run a closed-loop autopilot for every vehicle on the main thread after the player's input is drained and before the flight computer is snapshotted for the physics worker, with per-craft state, takeover checks, fault counting and a menu-bar off switch
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, harmony-postfix, reflection, imgui-menu, conditional-weak-table]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - reflection KSA.Vehicle._manualControlInputs
lifecycle: [frame:dispatch-solvers, frame:draw-ui, event:save-load, event:vehicle-destroyed]
game_types: [KSA.Vehicle, KSA.Universe, KSA.FlightComputer, KSA.ManualControlInputs, KSA.Program, KSA.KittenEva]
tags: [autopilot, guidance, main-thread, physics-worker, fault-tolerance, menu-bar]
---

Powered guidance (ascent, boostback, deorbit and landing) is driven by one per-vehicle step that runs
every frame for every vehicle, including crafts the player is not controlling (a separated booster
flying back while the player rides the upper stage). The step decides attitude, throttle and staging
for the next physics step, so where it runs in the frame is the core design decision.

## How it works

### The hook

`GuidanceFeature.ApplyDriverPatches` installs a prefix on `Vehicle.PrepareWorker(SimStep)` that calls
`GuidanceWindow.ApplyAutopilot(vehicle)` ([AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L24-L32)).
In the decomp, `Universe.ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls
`PrepareWorker` for every vehicle on the main thread and then queues the vehicle solver job
([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2154)).[^ksa-universe] `PrepareWorker` applies held throttle keys,
**clears `EngineOn` while the burn mode is Auto**, and ends with
`_threadWorkerUpdateState.PrepareFromVehicle(..., _manualControlInputs)`, the snapshot the worker flies
from ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)).[^ksa-vehicle] A prefix there is the last main-thread point
before that vehicle's snapshot and comes after `InputEvents.ApplyInputEvents` has drained the
player's input (the mod's handle comment calls it "the only one after the drain"; the
`FlightComputer.RaisePendingAlerts` loop also runs after the drain, but it is not the last point before
the snapshot). An engine command written there is the one the physics step sees. Because the rest of
`PrepareWorker` clears `EngineOn` in Auto, guidance holds the stock burn mode in Manual while it flies
(see [vehicle control ownership](vehicle-control-ownership.md)).

The point matters because of the flight-computer copy-back. The worker flies a copy that
`PrepareFromVehicle` takes here, and `Vehicle.UpdateFromTaskResultsUnsynchronized` writes that copy
back over the vehicle's flight computer with `FlightComputer.CopyFrom` at the next
`ApplyVehicleSolvers` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530)). An attitude or burn-mode write from the panel draw lands
after this frame's snapshot and is erased by that copy-back, so every panel action is queued as a
request that the next prefix applies ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L907-L909),
[AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L1379-L1386); see
[flight-computer worker copy channels](flight-computer-worker-copy-channels.md)).

### Per-craft state

`VehicleAutopilotState` is stored per `Vehicle` in a `ConditionalWeakTable`. The game-facing code is one
`partial static class GuidanceWindow` whose methods work on an ambient `_s` set to the current
vehicle's state at the start of each step; state is created only for the focused vehicle or a booster
adopted at separation, so idle crafts cost nothing
([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L910-L934)).

### Order inside one step

1. If guidance was switched off, hand the craft back and return.
2. Retry a pending flight-computer reset; release a stock deorbit node that is no longer needed.
3. **Takeover check, per actuator:** attitude taken if `AttitudeOwnership.IsCurrent` fails, engine taken
   if the burn mode reads Auto. Either releases the craft without an engine cut and records why
   ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L956-L971)).
4. Housekeeping: refresh the stage model (about 4 Hz wall clock, only when flying, the panel is open or
   a stage has a landing target), booster hand-over at separation, launch window.
5. Dispatch the active mode (6-DOF, landing chain, ascent, boostback), then readouts in their own guard.

Engine on/off and throttle are written to the private `Vehicle._manualControlInputs` through a validated
`FieldRef` (see [validated reflection handles](validated-reflection-handles.md)).

### Faults

The prefix wraps the step: a step that throws keeps the craft on its last attitude target and engine
command (an engine cut in the air costs more than one bad step). Each failure adds 2 to a counter and
each good step subtracts 1; at `MaxFailedSteps * 2` = 60 the craft is released, so a steady fault
releases after 30 steps and faults on more than a third of steps release it eventually. A craft with a
queued engine cut, or held on the ground outside an ascent, is released at the first failure
([AdvancedFlightComputer/Features/Guidance/Modes/Lifecycle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Lifecycle.cs#L12-L144)). Each fault kind is
logged once ([warn-once logging](warn-once-logging-and-diagnostics.md)). `KittenEva` (a kitten on EVA is
a `Vehicle` too) is skipped.

### Menu and lifecycle

- A postfix on `Program.DrawProgramMenusHook()`, an empty public method the game calls inside
  `Program.DrawMenuBar` on purpose ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020)), draws the **AFC Guidance** menu with **Enabled**
  and **Show panel**; it is the driver's only off switch, so it lives in its own patch block that loads
  even when the driver block fails ([AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L114-L152)).
- Turning Enabled off queues a release for every held craft, applied in each craft's next
  `PrepareWorker` prefix, so cleanup reaches the next worker snapshot.
- `SaveLoadObserver.SaveLoaded` releases every craft (a load replaces every vehicle); the
  `Vehicle.Dispose(bool)` postfix drops a disposed craft's resources.

## Lifecycle and timing

| Point | Lifecycle | Thread |
|---|---|---|
| Autopilot step | `Vehicle.PrepareWorker` prefix inside `ExecuteNextVehicleSolvers` | Main, `frame:dispatch-solvers` |
| Menu | `Program.DrawProgramMenusHook` postfix, inside `DrawMenuBar`: the main bar from `OnDrawUiThreadSafe`, or a hovered secondary viewport's bar from `GameViewport.DrawImGui` inside `OnDrawUiViewports` | Main, `frame:draw-ui` |
| Panel | `[StarMapAfterGui]` | Main, `frame:draw-ui` |
| Save load / dispose release | `UncompressedSave.Load` / `Vehicle.Dispose(bool)` postfixes | Main |

At the step, the previous step's results are applied and the player's input for this frame is drained;
the vehicle worker for the next step has not been queued. Writes to the flight computer and manual
inputs land in the next physics step. Two jobs are already running: the cloth solvers, and the
nearest-orbit job that `PrepareFrame` queued after the `RaisePendingAlerts` loop, which walks every
vehicle's `BurnPlan` and flight-plan patches for cursor hover
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2189-L2221)).[^ksa-program] Flight-computer fields and manual inputs are
safe here; adding or removing burns races that job (see
[deorbit landing planner](deorbit-landing-planner.md)). The 6-DOF solver and telemetry run on background threads owned by the mode (see
[6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)); the step only publishes inputs and reads finished
plans.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs) | Patch blocks, step wrapper, menu, rate postfix |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | `ApplyAutopilot`: takeover, housekeeping, dispatch |
| [AdvancedFlightComputer/Features/Guidance/Modes/Lifecycle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Lifecycle.cs) | Release paths and fault counting |
| [AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs) | Per-craft state table |
| [docs/guidance/runtime.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/runtime.md) | Author's runtime description |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceDriverTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceDriverTest.cs) | Headless test of hooks, takeovers and fault release |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs L61-L95
    // Runs on the main thread inside Universe.PrepareVehicleWorkers for every vehicle. See the handle in GameReflection for why this is the site.
    private static void OnPrepareWorker(Vehicle __instance) => StepVehicle(__instance);

    private static void StepVehicle(Vehicle vehicle)
    {
        // The flag outlives a patch that failed to unpatch, and a kitten on EVA is a vehicle too.
        if (!SharedVehicleHooks.GuidanceEnabled || vehicle is KittenEva)
            return;

        try
        {
            GuidanceWindow.ApplyAutopilot(vehicle);
            GuidanceWindow.StepSucceeded(vehicle);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce($"guidance-step-{vehicle.Id}:{ex.GetType().Name}",
                $"[AFC] Guidance step failed on '{vehicle.Id}': {ex}");

            // A bad step keeps the craft on its last command, and faults that keep coming, or a fault on the ground, give it back. Counting reads the craft's situation, so it sits inside the guard that keeps a second fault out of the game's loop.
            try
            {
                if (!GuidanceWindow.CountFailedStep(vehicle))
                    return;
                LogHelper.WarnOnce($"guidance-step-release-{vehicle.Id}",
                    $"[AFC] Guidance released '{vehicle.Id}' after its steps kept failing.");
                GuidanceWindow.FailAutopilot(vehicle, ex);
            }
            catch (Exception release)
            {
                LogHelper.WarnOnce($"guidance-release-{vehicle.Id}:{release.GetType().Name}",
                    $"[AFC] Guidance could not release '{vehicle.Id}' after a failed step: {release}");
            }
        }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) | Per-vehicle main-thread hook before the worker snapshot |
| `Universe.PrepareVehicleWorkers` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141)) | Caller, once per vehicle per frame |
| `Vehicle._manualControlInputs` (`ManualControlInputs.EngineOn`, `EngineThrottle`) | Engine commands |
| `Program.DrawProgramMenusHook()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018)) | Empty menu-bar hook for mods |
| `FlightComputer.BurnMode` | Engine takeover signal |

## Pitfalls and version notes

- Anything written to `_manualControlInputs.EngineOn` is cleared by `PrepareWorker` itself while the
  burn mode is Auto; hold Manual while you fly.
- `PrepareWorker` is virtual; `KittenEva` overrides it and calls `base.PrepareWorker`, so a prefix on
  `Vehicle.PrepareWorker` still runs for every EVA kitten ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L325)). Skip
  them explicitly, as AFC does with `vehicle is KittenEva`.
- Do not write flight-computer settings from the panel draw: the worker copy-back at the next
  `ApplyVehicleSolvers` overwrites them. Queue a request and apply it in the prefix.
- Never cut the engine on a fault in the air; count faults and release only on a pattern.
- The author notes that only single-craft ascent has been flown in the game from this tree; the other
  modes are tested in the headless harness.

## See also

- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [UPFG ascent guidance](upfg-ascent-guidance.md)
- [Deorbit landing planner](deorbit-landing-planner.md)
- [Guidance panel and overlays](guidance-panel-and-overlays.md)
- [Post-solver vehicle tick](post-solver-vehicle-tick.md) (the other main-thread vehicle hook, before input)
- [Step a per-vehicle autopilot from a PrepareWorker prefix with takeover checks and a menu off switch](../../../howto/flight-and-orbits/autopilot/run-an-autopilot-for-every-vehicle-each-step/run-an-autopilot-for-every-vehicle-each-step-1.md) - how-to article demonstrating this feature
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
