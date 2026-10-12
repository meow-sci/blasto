---
type: How-To
title: Command other vehicles through queued writes performed in a PrepareWorker prefix
description: Queue attitude, pulse mode, staging and quiet commands per vehicle from the frame hooks and replay them in one Vehicle.PrepareWorker prefix, the moment before KSA snapshots the flight computer
category: flight-and-orbits
group: autopilot
topic: drive-the-flight-computer-and-attitude-modes-from-a-mod
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/fly-another-vehicle
  - mods/ksarmory/features/prepareworker-prefix-command-window
techniques: [harmony-prefix, command-queue]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleReferenceFrameEx
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.PhysicsBubble
  - KSA.SequenceList
tags: [attitude, prepare-worker, double-buffer, queued-writes, staging, pulse-mode, custom-target]
---

Outcome: a mod can point, pulse, stage and quiet vehicles it does not own (rockets the player built) without any of the writes being erased. KSArmory queues every such write in a set or dictionary keyed by `Vehicle` and
performs it from a prefix on the public virtual `Vehicle.PrepareWorker(SimStep)`.[Command vehicles from a Vehicle.PrepareWorker prefix](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md)[Fly any vehicle from a mod through its flight computer and input actions](../../../../mods/ksarmory/features/fly-another-vehicle.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

The flight computer is double-buffered across the frame. `Universe.ApplyVehicleSolvers` copies the worker's result over the live object; `ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls `PrepareWorker` for every vehicle, and the snapshot is taken at the end of that method (`VehicleUpdateState.PrepareFromVehicle` ->
`VehicleUpdateData.Prepare` -> `NewFlightComputer.CopyFrom(flightComputer)`). Every StarMap hook runs after that snapshot, so a field written there is neither seen by the step in flight nor kept: next frame's apply overwrites it (the author measured
`before Manual/None -> after Auto/Custom` every frame).[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) [^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) [^ksa-vehicleupdatestate] [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306)

## Lifecycle and timing

| Order | What | Where |
|---|---|---|
| 1 | Previous worker finished | `JobSystems.VehicleSolver.Wait()` in `PrepareFrame` |
| 2 | Result copied over the live vehicle, including `FlightComputer.CopyFrom(NewFlightComputer)` | `ApplyVehicleSolvers` -> `UpdateFromTaskResultsUnsynchronized` |
| 3 | Keys and queued input events applied | `Glfw.PollEvents`, `InputEvents.ApplyInputEvents` |
| 4 | Part trees flushed | `PartTree.FlushDirtyDerived` |
| 5 | **The prefix runs**, once per vehicle | `ExecuteNextVehicleSolvers` -> `PrepareVehicleWorkers` -> `Vehicle.PrepareWorker` |
| 6 | `PrepareWorker` body: held keys cleared on the controlled craft if inactive, ImGui wants the keyboard or speed > 30x; throttle ramp; snapshot | `Vehicle.PrepareWorker` |
| 7 | Worker queued | `ExecuteNextVehicleSolvers` |
| 8 | StarMap GUI and frame hooks (writes here are lost) | `Program.OnFrame` |

Staging goes through the same window for another reason: for the controlled craft, while the `Sequence` or `EngineControl` gauge canvas is enabled (both start enabled), the worker's post-work recomputes `PerformanceSequences` by walking the live `SequenceList`,
so `ActivateNextSequence` from a hook races that walk. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install the prefix** from `[StarMapAllModsLoaded]` on `typeof(Vehicle).GetMethod("PrepareWorker", [typeof(SimStep)])`, with a private Harmony id; keep an `Installed` flag. A method that is only referenced to pin the signature makes a KSA signature change a build error.
2. **Queue from the frame hooks**: a static set or dictionary per command (`Wanted`, `Quieted`, `Pulsed`, `Staging`, `Shoves`, ...) keyed by `Vehicle`; `Release(craft)` clears the standing entries.
3. **Replay in a fixed order** in the prefix: stage, throttle, tank contents, mass, shove, pulse mode, then quiet (returns) or aim. Start each branch with a cheap `Count` or `Contains` test because the prefix runs for every vehicle every frame, and catch everything:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L341-L354, L381-L414
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

// ...
        // Before everything else, and whatever else this craft is doing: the mode decides how a
        // translation command already standing is spent, and it is restated every frame.
        if (Pulsed.Contains(__instance))
        {
            VehicleCommand.SetPulseMode(__instance, Pulsing.Contains(__instance));
        }
        else if (Restore.Remove(__instance))
        {
            VehicleCommand.SetPulseMode(__instance, pulsing: false);
        }

        if (Quieted.Contains(__instance))
        {
            VehicleCommand.ReleaseAttitude(__instance);

            // Releasing the actuator is necessary and not sufficient. PhysicsStates'
            // TryToPutOnRails returns a coasting vehicle to rails only when the bubble origin
            // is Cci, and in a Ccf bubble there is no path back at all -- so a bus quieted
            // inside one stays integrated for the rest of the coast, which is what 3ay
            // measured as 276 of 376 probes off rails against a pointed arm's 282.
            //
            // Asserting rails is the other half. It does not leave the bubble; it makes the
            // bubble irrelevant, because a rails Freefall vehicle takes ApplyFreefallMotion and
            // an exact conic whatever the frame. docs/ACCURACY-PLAN.md 3bv.
            if (Railed.Contains(__instance)) VehicleCommand.TryAssertRails(__instance);

            return;
        }

        if (Wanted.Count == 0) return;
        if (!Wanted.TryGetValue(__instance, out Aim aim)) return;

        VehicleCommand.TryAim(__instance, aim.DirectionCci, aim.RollReferenceCci);
    }
```

4. **Point with a Custom target**: build the target rotation with rows forward, across, third; re-express in `EclBody` and write all of `AttitudeFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget`, `AttitudeMode`, then set the profile and force `BurnMode` to Manual (an Auto burn fights for attitude):

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L71-L99
    doubleQuat target2Cci = doubleQuat.CreateFromRotationMatrix(new double4x4(
        forward.X, forward.Y, forward.Z, 0.0,
        across.X, across.Y, across.Z, 0.0,
        third.X, third.Y, third.Z, 0.0,
        0.0, 0.0, 0.0, 1.0));

    doubleQuat frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(parent.GetCce2Cci());
    doubleQuat target2Frame = doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse());

    FlightComputer computer = craft.FlightComputer;
    computer.AttitudeFrame = VehicleReferenceFrame.EclBody;
    computer.AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom;
    computer.CustomAttitudeTarget = VehicleReferenceFrame.EclBody.QuaternionToEulerAngles(target2Frame);
    computer.AttitudeMode = FlightComputerAttitudeMode.Auto;

    // Re-derive the pointing deadband from the thrusters actually aboard. KSA widens it to
    // whatever one control period of the minimum impulse can produce -- a stability guard, so
    // the tracker is not asked to settle inside its own quantum -- but it only ever raises it.
    // A stack that staged carries the guard sized for its boost RCS onto a bus whose thrusters
    // are two orders finer, and nothing lowers it again: measured at 11.4 degrees of deadband
    // against a live rate bit worth 0.07. Assigning the profile puts it back, and KSA's own max
    // re-establishes the real floor on the same frame.
    computer.SetAttitudeProfile(FlightComputerAttitudeProfile.Strict);

    // An automatic burn would fight this for the attitude and run its own throttle.
    computer.BurnMode = FlightComputerBurnMode.Manual;

    return true;
}
```

5. **Carry the roll reference across frames.** The engine's planet-direction roll reference reverses when the nose points straight up or down and spins a rising rocket; supply a stable roll reference from the mod.
6. **Let go** with `AttitudeMode = Manual`, `AttitudeTrackTarget = None`, `CustomAttitudeTarget = 0`; to return a coasting craft to rails also assert `GetPhysicsStatesMutable().Props.SetOnRails(true)` (see [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)).
7. **On any throw**, drop the craft from every set and log once, so the prefix never throws into `PrepareFrame`.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L36-L143) | queues and `Install` |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L164-L337) | `Hold`, `Quiet`, `QuietOnRails`, `PulseMode`, `ThrottleKey`, `Stage`, `Shove`, `Release` |
| [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs) | the writes: `TryAim`, `ReleaseAttitude`, `TryAssertRails`, `SetPulseMode`, `Stage` |

## Variations and gotchas

- **The deadband only ever rises.** KSA widens the pointing deadband to what one control period of the minimum impulse can produce; a stack carrying the guard sized for boost RCS onto a bus with finer thrusters stays wide until `SetAttitudeProfile(Strict)` resets it.
- **The prefix must not touch ImGui or Vulkan** and must be fast: it is inside KSA's frame loop.
- **A write is lost only because it is on the wrong side of the snapshot**; the copy is lossless.

## See also

- [Overview](overview.md); [orient to star](drive-the-flight-computer-and-attitude-modes-from-a-mod-1.md); [solver-phase drain](drive-the-flight-computer-and-attitude-modes-from-a-mod-3.md)
- [Command vehicles from a Vehicle.PrepareWorker prefix](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md), [Fly any vehicle from a mod through its flight computer and input actions](../../../../mods/ksarmory/features/fly-another-vehicle.md)
- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md), [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
