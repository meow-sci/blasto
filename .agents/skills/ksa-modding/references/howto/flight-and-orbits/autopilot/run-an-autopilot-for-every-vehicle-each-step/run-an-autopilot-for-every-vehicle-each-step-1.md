---
type: How-To
title: Step a per-vehicle autopilot from a PrepareWorker prefix with takeover checks and a menu off switch
description: Run guidance for every vehicle right before its worker snapshot, check per actuator whether the player took over, count failures instead of throwing every frame, and release craft when the mod is switched off
category: flight-and-orbits
group: autopilot
topic: run-an-autopilot-for-every-vehicle-each-step
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/guidance-vehicle-step-loop]
techniques: [harmony-prefix, conditional-weak-table]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - harmony-postfix KSA.Vehicle.Dispose
lifecycle: [frame:dispatch-solvers, frame:draw-ui, event:save-load]
game_types: [KSA.Vehicle, KSA.Universe, KSA.FlightComputer, KSA.KittenEva, KSA.Program, KSA.ManualControlInputs]
tags: [autopilot, prepare-worker, takeover, fault-counter, menu, per-vehicle-state]
---

Outcome: one function steps every guided vehicle each frame (ascent, boostback, deorbit, landing), including craft the player is not controlling, with a per-craft state table, takeover detection and a fault policy.
KSA-AdvancedFlightComputer's guidance driver is the demonstrating code.[Per-vehicle autopilot step in a Vehicle.PrepareWorker prefix](../../../../mods/ksa-advancedflightcomputer/features/guidance-vehicle-step-loop.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

See the [overview](overview.md): a prefix on `Vehicle.PrepareWorker(SimStep)` runs on the main thread for every vehicle, after `ApplyInputEvents` and before that vehicle's snapshot; an engine command written there is the one the physics step sees. `PrepareWorker` clears `EngineOn` while `BurnMode` is Auto, so a controller that sets engine commands must hold the stock burn mode in Manual
(see [approach 4](run-an-autopilot-for-every-vehicle-each-step-4.md)). A kitten on EVA is a `Vehicle` too (`KittenEva`), and `KittenEva.PrepareWorker` calls `base.PrepareWorker`, so the prefix runs for kittens.[^ksa-kitteneva]
The nearest-orbit job queued after the `RaisePendingAlerts` loop walks every vehicle's `BurnPlan` and flight-plan patches on its own worker, so flight-computer fields and manual inputs are safe in the prefix but adding or removing burns races it.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2189-L2221)

## Lifecycle and timing

| Point | Lifecycle | Thread |
|---|---|---|
| Autopilot step | `Vehicle.PrepareWorker` prefix inside `ExecuteNextVehicleSolvers` | main, `frame:dispatch-solvers` |
| Menu | `Program.DrawProgramMenusHook` postfix, inside `DrawMenuBar` (the main bar from `OnDrawUiThreadSafe`, or a hovered secondary viewport's bar) | main, `frame:draw-ui` |
| Panel | `[StarMapAfterGui]` | main, `frame:draw-ui` |
| Save load / dispose release | `UncompressedSave.Load` and `Vehicle.Dispose(bool)` postfixes | main |

At the step the previous step's results are applied, the player's input is drained and the next worker has not been queued: writes land in the next physics step. Every panel action is queued as a request that the next prefix applies, because a write from the panel draw is erased by the copy-back. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch the prefix** and wrap the step; skip kittens and the whole thing when the mod is off:

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

2. **Create state only where needed.** Per-vehicle state lives in a `ConditionalWeakTable`; create it for the focused vehicle (`Program.ControlledVehicle`) or a booster adopted at separation, and reuse existing state elsewhere, so idle craft cost nothing:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L910-L933
    public static void ApplyAutopilot(Vehicle vehicle)
    {
#if DEBUG
        using var _perf = new PerfTracker.Scope("Guidance.ApplyAutopilot");
#endif
        // Do not create state while guidance is disabled. Release each vehicle in its PrepareWorker prefix so cleanup reaches the next worker snapshot.
        if (!ModActive)
        {
            if (VehicleAutopilotState.TryGet(vehicle, out VehicleAutopilotState disabledState))
            {
                _s = disabledState;
                HandBackVehicle(vehicle);
            }
            return;
        }

        // Use creates state, so reserve it for the focused vehicle and reuse existing state elsewhere.
        bool focused = ReferenceEquals(vehicle, Program.ControlledVehicle);
        if (focused)
            Use(vehicle);
        else if (VehicleAutopilotState.TryGet(vehicle, out VehicleAutopilotState state))
            _s = state;
        else if (!TryAdoptBooster(vehicle))
            return;
```

3. **Order inside one step**: honour a pending switch-off (hand the craft back and return); retry a pending flight-computer reset; run the **takeover check per actuator**: attitude taken if the ownership comparison fails, engine taken if the burn mode reads Auto; either releases the craft without an engine cut and records why; then housekeeping (stage model refresh, booster hand-over,
   launch window), dispatch the active mode, readouts in their own guard.
4. **Write engine commands** through the private `Vehicle._manualControlInputs` with a validated `FieldRef` (see [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)), and attitude as in [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md).
5. **Fault policy.** A step that throws keeps the craft on its last attitude target and engine command (an engine cut in the air costs more than one bad step). Each failure adds 2 to a counter, each good step subtracts 1; at 60 the craft is released, so a steady fault releases after 30 steps. A craft with a queued engine cut, or held on the ground outside an ascent, is released at the first failure. Log each fault kind once.
6. **Provide an off switch outside the driver.** A postfix on the empty public `Program.DrawProgramMenusHook()` (called on purpose inside `DrawMenuBar`) draws an **Enabled** and **Show panel** menu; keep it in its own patch block so it loads even when the driver block fails. Turning Enabled off queues a release for every held craft, applied in each craft's next prefix.
7. **Release on events.** `SaveLoadObserver.SaveLoaded` releases every craft (a load replaces every vehicle); a `Vehicle.Dispose(bool)` postfix drops a disposed craft's resources.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs) | patch blocks, step wrapper, menu, rate postfix |
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs) | `ApplyAutopilot`: takeover, housekeeping, dispatch |
| [AdvancedFlightComputer/Features/Guidance/Modes/Lifecycle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Lifecycle.cs) | release paths and fault counting |
| [AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs) | per-craft state table |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceDriverTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceDriverTest.cs) | headless test of hooks, takeovers and fault release |

## Variations and gotchas

- **Do not throw from the prefix**: it is inside the game's frame loop.
- **Long solves run on background threads** owned by the mode; the step only publishes inputs and reads finished plans.
- **The stock burn mode must stay Manual** while you set the engine, or `PrepareWorker` clears `EngineOn` every step.
- **Release must reach the next snapshot**: do cleanup in the prefix, not from a draw hook.

## See also

- [Overview](overview.md); [booster adoption](run-an-autopilot-for-every-vehicle-each-step-2.md); [claims and takeover](run-an-autopilot-for-every-vehicle-each-step-4.md)
- [Per-vehicle autopilot step in a Vehicle.PrepareWorker prefix](../../../../mods/ksa-advancedflightcomputer/features/guidance-vehicle-step-loop.md)
- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)

[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
