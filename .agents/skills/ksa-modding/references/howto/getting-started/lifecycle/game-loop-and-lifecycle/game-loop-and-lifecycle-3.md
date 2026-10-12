---
type: How-To
title: Mutate simulation state safely relative to the physics step
description: Change vehicle state only in the settled window between solver apply and dispatch, via InputEvents buffers, a PrepareFrame seam or a Vehicle.PrepareWorker prefix, and step mod motion on the engine clock
category: getting-started
group: lifecycle
topic: game-loop-and-lifecycle
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/lib-physics-frame-hook
  - mods/ksacars/features/prepareworker-impulse-injection
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksarmory/features/consume-applied-sim-step
  - mods/ksa-advancedflightcomputer/features/guidance-vehicle-step-loop
  - mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels
  - mods/compendium/features/root-navigation-targets
  - mods/gatos/features/solver-phase-command-drain
  - mods/unscience/features/eternal-flame-resource-refill
  - mods/ohmystars/features/attitude-mode-handoff-patches
techniques: [harmony-prefix, harmony-transpiler, custom-physics, background-thread]
hooks:
  - harmony-prefix KSA.InputEvents.ApplyInputEvents
  - harmony-transpiler KSA.Program.PrepareFrame
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
  - harmony-prefix KSA.Vehicle.PrepareWorker
lifecycle: [frame:apply-solvers, frame:input, frame:dispatch-solvers, frame:draw-ui, thread:background]
game_types:
  - KSA.Program
  - KSA.Universe
  - KSA.Vehicle
  - KSA.InputEvents
  - KSA.JobSystems
  - KSA.SimStep
  - KSA.ConstraintSim
tags: [physics, threading, solver, vehicle, teleport, input-events, sim-step, frame]
---

The vehicle solver runs on worker threads from `PrepareFrame` of one frame to `PrepareFrame` of the
next, so every StarMap hook runs while a physics step is in flight. A position, velocity, attitude,
flight-computer or resource write made there is either ignored by the worker and overwritten a frame
later, or it races the worker, or it throws. This article shows the window in which writes stick and
the four ways mods reach it, plus how to advance mod-owned motion in step with the engine.

Prerequisites: approach 1 and 2 (StarMap entry class, Harmony patching). Demonstrated by unscience
(`PhysicsFrameHook` and its consumers), KSACars and KSArmory (`Vehicle.PrepareWorker` prefixes,
engine-step clock), KSA-AdvancedFlightComputer, Compendium and gatOS.

## Background: how the game does it

`PrepareFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) in order:[^ksa-program]

1. Waits for all four schedulers and applies the last step: `ApplyOrbitSolvers`, `ApplyVehicleSolvers`,
   `ApplyClothSolvers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2151-L2171). `ApplyVehicleSolvers` overwrites each vehicle's
   kinematic state and copies its flight computer back from the worker
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2440-L2441) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2530).
2. Polls GLFW and applies the `InputEvents` buffers [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2179-L2181): teleports, staging,
   vehicle input, sim speed, vehicle destruction and the rest [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140).
3. Queues the nearest-orbit job, which reads flight plans and cached orbit points concurrently
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2201).
4. Computes the next `SimStep` and dispatches: `ExecuteNextClothSolvers`, `PartTree` flushes,
   `ExecuteNextVehicleSolvers` (which calls `Vehicle.PrepareWorker` for every vehicle and snapshots
   origin, kinematics, flight computer and manual inputs) and `ExecuteNextOrbitSolvers`
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) [current/decomp/KSA/VehicleUpdateData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateData.cs#L84-L104).

The game itself never mutates simulated state from UI code: buttons and keys add structs to
`InputEvents` typed buffers, which step 2 applies. Structural changes during the step are refused
outright: the shared shape registry throws "stage the change and apply it at the frame sync point"
while the vehicle update runs [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L124-L133).

## Lifecycle and timing

The settled window is from the end of `ApplyClothSolvers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2170) to the snapshot
inside `ExecuteNextVehicleSolvers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2220) (`frame:apply-solvers` ->
`frame:input` -> `frame:dispatch-solvers`). No StarMap hook is inside it.

| Write path | Where it runs | Sees this step's results | Next step sees the write | Latency from a UI click | Used by |
|---|---|---|---|---|---|
| `InputEvents.<Buffer>.Add` | `ApplyInputEvents`, step 2 | yes | yes | next frame | KSACars, KSA-AdvancedFlightComputer, gatOS, Compendium |
| Own queue drained in a prefix on `InputEvents.ApplyInputEvents` | step 2 | yes | yes | next frame | Compendium |
| Handoff seam (transpiler replacing the `GetJobSimStep` call) | between steps 3 and 4 | yes | yes | next frame | unscience |
| Prefix on `Universe.ExecuteNextVehicleSolvers` | step 4, after cloth dispatch and `PartTree` flushes | yes | yes | next frame | gatOS, unscience (eternal-flame, kiwis-marbles), KSAGolf |
| Prefix on `Vehicle.PrepareWorker(SimStep)` | step 4, per vehicle, right before its snapshot | yes | yes | next frame | KSACars, KSArmory, KSA-AdvancedFlightComputer, OhMyStars |
| Write from a StarMap hook or UI | after dispatch | yes | no for kinematics, flight computer and anything else `UpdateFromTaskResultsUnsynchronized` writes back (overwritten at the next apply); fields apply never touches, such as `Vehicle._manualControlInputs`, are copied by the next `PrepareWorker` | next frame, for those fields only | (the bug); gatOS's throttle and RCS writes rely on the exception |
| `JobSystems.VehicleSolver.Wait()` then write | after dispatch | yes | no; the step already snapshotted, and the next apply overwrites. Exception: `Vehicle.Teleport` detaches the vehicle from its bubble, and `PhysicsBubble.ApplyResultsToVehicles` only walks the vehicles still in the bubble, so the pending result is dropped and the teleport stands | next frame (teleport only) | gatOS (welds, teleport only), KSArmory (only to avoid crashes on destroy) |

`Vehicle.PrepareWorker` is `public virtual` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423), so the compiler checks the
target; the rest of its body clears the controlled vehicle's held player input (when it is not the
active controlled vehicle, when ImGui wants the keyboard, or above 30x), ramps and clamps the manual
throttle, clears `_manualControlInputs.EngineOn` while `FlightComputer.BurnMode` is Auto (so an engine
command written in a prefix survives only in Manual), and picks high-fidelity ocean physics before
`PrepareFromVehicle` takes the snapshot at its
end [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423).

The window is settled for the vehicle solver, not for every job. Step 3 queues the nearest-orbit job
before step 4, and its `CelestialSystem.SetNearestOrbitPoint` walks the controlled vehicle's (and other
shown vehicles') `BurnPlan` burn list and flight-plan patches for cursor hover on its own worker
[current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L484) [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L573-L577).
Adding or removing burns (`FlightComputer.AddBurn` / `RemoveBurn`) from a prefix on
`ExecuteNextVehicleSolvers` or `PrepareWorker` therefore races that job. Stock edits burn plans only
in step 2 (`InputEvents.BurnUpdateBuffer`) and in the `FlightComputer.RaisePendingAlerts` loop, both
before the job is queued; do the same, or queue the edit.

## Steps

1. For anything the game already models as a command, add to the matching `InputEvents` buffer from
   main-thread code (a UI hook is fine). The buffers are plain arrays with no locking
   [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L943-L994). KSACars queues a vehicle action from inside the engine's key
   handling:

```csharp
// src/KSACars/Ksa/BoostHook.cs L105-L127
// Inside the engine's key handling: nothing here may throw.
private static void AfterKey(Vehicle __instance, GlfwKeyEvent keyEvent)
{
    try
    {
        if (keyEvent.Action == GlfwKeyAction.Repeat || !Input.Contains(in keyEvent, InputAction.Sprint)) return;
        if (Buggies.Of(__instance) is not { } car || !car.Drive.Profile.HasBoost) return;

        InputEvents.VehicleInputBuffer.Add(new InputEvents.VehicleInputData
        {
            Vehicle = __instance,
            Action = InputAction.Sprint,
            KeyAction = keyEvent.Action,
            Modifiers = keyEvent.Mods,
        });
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("reading the boost key failed", e);
    }
}
```

   Teleports go through `TeleportInputData`, whose `Apply` clears the burn plan and calls
   `Vehicle.Teleport` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L407-L423).

2. For commands the game has no buffer for, keep your own queue and drain it in the same window.
   Compendium drains its target requests in a prefix on `ApplyInputEvents`, and still routes the
   flight-computer part through the game's own buffer:

```csharp
// src/RootTargetPatches.cs L7-L15
[HarmonyPatch]
internal static class RootTargetPatches
{
    [HarmonyPatch(typeof(InputEvents), nameof(InputEvents.ApplyInputEvents))]
    [HarmonyPrefix]
    private static void ApplyRootTargetRequests()
    {
        RootNavigationTargets.ApplyRequests();
    }
```

```csharp
// src/RootNavigationTargets.cs L38-L70
// Apply vehicle changes alongside the game's own input events, not during GUI rendering.
public static void ApplyRequests()
{
    while (requests.TryDequeue(out var request))
    {
        if (request.Vehicle.IsDisposed || (request.Body != null && !IsLoadedRoot(request.Body)))
        {
            Console.WriteLine("Compendium: root target request discarded because its vehicle or body is no longer loaded.");
            continue;
        }
        if (request.Tracking.HasValue)
        {
            if (GetTarget(request.Vehicle) != request.Body || !request.Vehicle.IsControllable)
            {
                Console.WriteLine("Compendium: root tracking request discarded because the target changed or the vehicle is not controllable.");
                continue;
            }
            InputEvents.FlightComputerInputBuffer.Add(new InputEvents.FlightComputerInputData
            {
                Vehicle = request.Vehicle,
                EnumValue = request.Tracking.Value
            });
        }
        else if (request.Body != null)
        {
            Select(request.Vehicle, request.Body);
        }
        else
        {
            Clear(request.Vehicle);
        }
    }
}
```

3. For arbitrary edits (resize a vessel, weld two craft, teleport, load a save) use a handoff seam.
   unscience's `PhysicsFrameHook` replaces the `GetJobSimStep` call in `PrepareFrame` with its own
   method, after checking that the seven `Universe` calls appear exactly once and in order
   [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs#L73-L104):

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L106-L140
private static SimStep GetStepAndDispatch(double dtPlayer)
{
    // The previous ImGui frame has finished and worker results have committed. A
    // console load requested after ImGui.Image must never dispose its textures inline.
    // Replay the whole transaction here, before computing any time from the old world.
    _orbitReadersJoined = false;
    Action? worldChange = _pendingWorldChange;
    _pendingWorldChange = null;
    if (worldChange != null)
    {
        JoinOrbitReaders();
        IsReplayingWorldChange = true;
        try { worldChange(); }
        catch (Exception ex) { Console.WriteLine($"unscience saves: deferred world change failed: {ex}"); }
        finally { IsReplayingWorldChange = false; }
    }
    SimStep step = Universe.GetJobSimStep(dtPlayer);
    if (Universe.CurrentSystem == null) { Pending.Clear(); return step; }

    // Drain only the actions present at the handoff; actions queued by callbacks wait a frame.
    int count = Pending.Count;
    if (count > 0) JoinOrbitReaders();
    for (int i = 0; i < count; i++)
    {
        try { Pending.Dequeue()(); }
        catch (Exception ex) { Console.WriteLine($"unscience physics: queued mutation failed: {ex}"); }
    }
    if (BeforePhysics != null)
        foreach (Action<double, UniverseTime> callback in BeforePhysics.GetInvocationList())
        {
            try { callback(dtPlayer, step.PreviousTime); }
            catch (Exception ex) { Console.WriteLine($"unscience physics: callback failed: {ex}"); }
        }
    return step;
}
```

   Callers enqueue closures from UI code and the hook runs them before the next snapshot; godzilla
   resizes a vessel this way [godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs#L52-L60). Because the
   nearest-orbit job queued in step 3 may still be reading cached orbit points that `Vehicle.Teleport`
   disposes, any teleport at the seam first joins that one worker:

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L23-L36
/// <summary>
/// Waits for the nearest-orbit/performance job that PrepareFrame queues just before this
/// handoff. It reads flight plans and cached orbit points concurrently, and Teleport or other
/// flight-plan edits dispose those points. Joins at most once per frame; callbacks that move
/// vessels call it before mutating, and queued work and world changes join automatically.
/// Intended for the handoff only: the flag resets there, and later in the frame the same worker
/// may run unrelated editor performance jobs, which this does not wait for.
/// </summary>
public static void JoinOrbitReaders()
{
    if (_orbitReadersJoined) return;
    JobSystems.NearestOrbitAndPerformanceWorker.Wait();
    _orbitReadersJoined = true;
}
```

   A plain prefix on `ExecuteNextVehicleSolvers` is the simpler variant. It runs after the cloth jobs
   were kicked and the `PartTree` flushes ran, and before any vehicle is snapshotted; gatOS drains its
   remote "solver phase" commands there with `Priority.First`
   [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880).

4. For per-vehicle state that must be in this step's snapshot (control inputs, attitude commands,
   impulses, tank contents), prefix `Vehicle.PrepareWorker`. KSACars runs its whole car model there,
   once per frame per vehicle, writing velocity changes into
   `Vehicle.GetPhysicsStatesMutable().Kinematic` before the worker copies it:

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L77-L89
private static void BeforePrepareWorker(Vehicle __instance, SimStep simStep)
{
    try
    {
        Buggies.Physics(__instance, simStep.DeltaTime);
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("car physics failed inside the engine's step", e);
    }
}
```

   KSArmory stages, throttles, leaks propellant, sheds launcher mass and applies blast impulses from the
   same prefix, because "anywhere else the worker's copy goes over them":

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L339-L362
// Runs inside KSA's frame loop, immediately before the flight computer is snapshotted for the
// worker. Everything here is wrapped, because an exception at this point is not a log line.
private static void BeforePrepareWorker(Vehicle __instance)
{
    try
    {
        if (Staging.Count > 0 && Staging.Remove(__instance)) VehicleCommand.Stage(__instance);

        // The clear runs inside PrepareWorker after this and takes the held keys, never the throttle value, so the
        // step the key would have made is made here, at KSA's own 0.7 a second of the player's frame.
        if (ThrottleKeys.Count > 0 && ThrottleKeys.TryGetValue(__instance, out int key)
            && KsaWorld.DiscardsHeldControls(__instance, out _))
        {
            KsaWorld.TryNudgeThrottle(__instance, key * KsaWorld.PlayerDeltaTime * 0.7);
        }

        // Tank contents are written here for the same reason attitude is: anywhere else the
        // worker's copy goes over them.
        Leaks.Apply(__instance);

        // And a launcher's mass, lighter by what it has fired, for the same reason.
        StoreMass.Apply(__instance);

        if (Shoves.Count > 0 && Shoves.Remove(__instance, out (double3 Linear, double3 Angular, double Wind, double3 AwayEcl) shove))
```

5. Advance mod-owned motion (projectiles, effects, anything the mod integrates itself) on the step the
   engine applied, not on `dtPlayer`. `Universe.GetLastSimStep()` is the step whose results were just
   applied [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1948) [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2380-L2384); it is zero when paused and
   includes warp and the solver's speed throttle. It reports only the most recent step, so consume it
   once per step. KSArmory deduplicates on the step's `NextTime` and closes gaps left by frames where
   its hook did not run (HUD hidden, screenshots):

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L98-L115
public static double ConsumeSimStep()
{
    SimStep step = Universe.GetLastSimStep();

    if (_consumedThrough is { } from && !step.NextTime.Equals(from))
    {
        LastLongerOfStepAndSpan = Math.Max(step.DeltaTime, Span(from, step.NextTime));
    }

    _consumedThrough = step.NextTime;

    // The span, not just the step: a frame in which this mod's hook never ran still advanced
    // the world, and GetLastSimStep reports only the most recent one. Taking a screenshot is
    // exactly that case -- ScreenshotCapture sets Program.DrawUI false and KSA guards the call
    // this mod postfixes with it, so the hook is not called at all. Differenced in Int128
    // nanoseconds because absolute universe time is far too large to subtract as double.
    return _stepGate.Consume(step.NextTime, step.DeltaTime, Span);
}
```

```csharp
// src/KSArmory/Sim/StepGate.cs L60-L78
public double Consume(T nextTime, double delta, Func<T, T, double>? spanSeconds)
{
    if (_hasIntegrated && nextTime.Equals(_integratedThrough)) return 0.0;

    double taken = delta;

    // A span shorter than the step by more than rounding means the clock disagrees with the
    // step it just handed over -- a save loaded mid-session jumps it backwards -- and the step
    // is then the thing that moved the world.
    if (_hasIntegrated && spanSeconds is not null)
    {
        double span = spanSeconds(_integratedThrough, nextTime);
        if (double.IsFinite(span) && span > taken - RoundingSeconds) taken = span;
    }

    _integratedThrough = nextTime;
    _hasIntegrated = true;
    return taken;
}
```

   Positions read from the engine during the frame are at the step's `NextTime`
   (`Universe.GetElapsedTime()` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399-L2402)); KSArmory sub-steps each round inside
   the frame step and back-dates every world lookup to its sub-step's instant
   [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L499-L545).

6. Move heavy computation to your own thread, but publish into the window. KSA-AdvancedFlightComputer
   runs its 6-DOF solver on a background thread behind a locked request/result slot
   [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs#L61-L103)
   and collects the result inside its `Vehicle.PrepareWorker` prefix, so it lands just before the
   snapshot [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L1045-L1049).

## Complete example

- unscience: [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs) with consumers
  [godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs), [garrys-torch.lib/WeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.cs#L112-L123) and
  save loads deferred to the seam in [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs#L94-L170).
- KSACars: [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs) and the kinematic writes in
  [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L580-L640).
- KSArmory: [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs),
  [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L58-L145), [src/KSArmory/Sim/StepGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StepGate.cs).
- KSA-AdvancedFlightComputer: [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L24-L62).

## Variations and gotchas

- What goes wrong outside the window, per [overview](overview.md#why-main-thread-mutation-after-dispatch-is-racy):
  kinematics and flight-computer writes are silently reverted at the next apply; props, environment
  and module states race a copy-on-write on the worker; parachute fields race worker writes; shape,
  bubble and part changes throw. KSArmory measured the flight-computer case directly: attitude commands
  written from a StarMap hook were "before Manual/None -> after Auto/Custom, every single one"
  [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L9-L23).
- `JobSystems.VehicleSolver.Wait()` in a UI hook makes a write safe from crashes (KSArmory does it before
  destroying a vehicle [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2596-L2627); gatOS before welding
  [gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Welds/WeldManager.cs#L106-L112)) but not effective for ordinary writes: the step
  already took its snapshot and the next apply overwrites kinematics. The one exception is a
  `Vehicle.Teleport`, which gatOS's welds use: it calls `RemoveFromCurrentBubble` ->
  `PhysicsBubble.RemoveVehicle`, which drops the vehicle from the bubble's `_vehicleStates`
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L242-L251), and the next apply writes results only to vehicles in that list
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L881-L909), so the teleported state survives; the wait is what makes
  touching the bubble legal. Either way the wait stalls the main thread until the step finishes,
  which removes the overlap between physics and the rest of the frame. Otherwise use it only for teardown.
- A prefix on `ExecuteNextVehicleSolvers` runs after `ExecuteNextClothSolvers`; cloth is cosmetic, but
  anything a cloth job reads is already in flight.
- `PrepareWorker` is called for every vehicle, including kittens on EVA (`KittenEva` overrides it)
  [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L325); filter early, keep it allocation-free.
- `PrepareWorker` prefixes run per frame, not per substep. A force applied there is one impulse per
  frame that the engine then integrates through its own substeps; KSACars leaves its springs out above a
  0.1 s step for that reason [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L568-L572). For per-substep forces see
  approach 4.
- When paused the step is still dispatched with `DeltaTime == 0`, and on the frame the speed drops to
  zero one last real step is still applied. Gate on the consumed step, not on `Universe.IsPaused()`.
- Save loads replace every vehicle: drop per-vehicle state on load (approach 2, step 7) and never
  enqueue closures that capture a vehicle without re-checking `IsDisposed`, as the unscience consumers do.

## See also

- [Game loop and mod lifecycle overview](overview.md)
- [Approach 1: StarMap lifecycle attributes](game-loop-and-lifecycle-1.md)
- [Approach 2: Harmony patches on Program and Universe](game-loop-and-lifecycle-2.md)
- [Approach 4: run code inside the physics step](game-loop-and-lifecycle-4.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
