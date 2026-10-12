---
type: How-To
title: Write flight-computer and kinematic fields in a Vehicle.PrepareWorker prefix
description: Queue per-vehicle commands from frame hooks and apply them in a Harmony prefix on Vehicle.PrepareWorker, the last point before the worker snapshots the vehicle
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/prepareworker-prefix-command-window
  - mods/ksarmory/features/per-part-store-mass
techniques: [prepareworker-window, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAllModsLoaded]
lifecycle: [frame:dispatch-solvers, frame:draw-ui, frame:apply-solvers]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.VehicleUpdateState
  - KSA.VehicleUpdateData
  - KSA.SimStep
  - KSA.Universe
tags: [prefix, snapshot, flight-computer, double-buffer, commands]
---
Outcome: you can aim, throttle, stage, pulse or shove any vehicle from a mod and have the engine keep the
result. KSArmory flies other people's rockets this way: the ballistic computer points them, trims them,
works the throttle and fires stages. A write to the flight computer or kinematics from `StarMapAfterGui` is
gone a frame later; KSArmory measured `before Manual/None -> after Auto/Custom` every frame.

Prerequisites: StarMap (to install at `StarMapAllModsLoaded`), Harmony. Demonstrated by
[ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md) and, for the in-window write to the double-buffered `Props` (the mass case, articles 6 and 7), [ksarmory/per-part-store-mass](../../../../mods/ksarmory/features/per-part-store-mass.md). The autopilot use is in
[Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../take-control-of-another-vehicle/overview.md) approach 2; staging with it is in [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md).

## Background: how the game does it

`Vehicle.PrepareWorker(SimStep)` is public and virtual. `Universe.PrepareVehicleWorkers` calls it for every
vehicle immediately before the job is queued [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2155)[^ksa-universe]. Its body clears held keys on
the controlled craft when input is inactive, ramps throttle, sets `EngineOn=false` while the burn mode is
Auto, and ends in `_threadWorkerUpdateState.PrepareFromVehicle(...)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2422)[^ksa-vehicle], which
copies bubble origin, `KinematicStates`, `NewFlightComputer.CopyFrom(flightComputer)` and the manual
inputs into the worker snapshot [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306)[^ksa-vehicleupdatestate].

On the other side, `ApplyVehicleSolvers` -> `Vehicle.UpdateFromTaskResultsUnsynchronized` copies the
worker's kinematics, properties and flight computer back over the live vehicle
(`FlightComputer.CopyFrom(updateData.NewFlightComputer)`) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2545). A write to the live
object after the snapshot is therefore not in the worker's copy and is overwritten by the commit.

## Lifecycle and timing

| Order | What | Where |
|---|---|---|
| 1 | previous step finished and applied | `Wait`, `ApplyVehicleSolvers` (Program.cs L2158, L2167) |
| 2 | keys and queued input events applied | `Glfw.PollEvents`, `ApplyInputEvents` (L2179-L2181) |
| 3 | **prefix runs once per vehicle** | `ExecuteNextVehicleSolvers` (L2220) -> `PrepareWorker` |
| 4 | snapshot | `PrepareFromVehicle` |
| 5 | StarMap GUI and frame hooks | after L2220: queue here for the next prefix |

Inside the prefix the vehicle holds the step applied this frame, the worker is idle, this frame's key
presses are in, and the writes you make are in the snapshot a few lines later and integrated by this
step. Mod logic that decides things (in `StarMapAfterGui`) only queues; the prefix applies. It runs inside
the frame loop, so it must not throw and must not touch ImGui or Vulkan. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Declare queues keyed by `Vehicle` (reference equality). Standing commands (hold an attitude) live in a
   dictionary re-applied every frame; one-shots (stage, shove) are removed when applied.
2. Install the prefix once at `StarMapAllModsLoaded`, resolving the method by name and parameter list and
   recording failure for the UI:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L112-143
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? target = typeof(Vehicle).GetMethod(
                nameof(Vehicle.PrepareWorker), [typeof(SimStep)]);

            if (target is null)
            {
                Trouble = "KSA has no Vehicle.PrepareWorker(SimStep) to hook";
                Log.Warn($"attitude control unavailable: {Trouble}");
                return;
            }

            MethodInfo prefix = typeof(AttitudeHook).GetMethod(
                nameof(BeforePrepareWorker), BindingFlags.NonPublic | BindingFlags.Static)!;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Installed = true;
            Trouble = "";
            Log.Info("attitude control hooked into Vehicle.PrepareWorker");
        }
        catch (Exception e)
        {
            Trouble = e.Message;
            Log.Error("could not hook attitude control; the ballistic computer cannot fly", e);
        }
    }
```

3. Write the prefix. Order matters; KSArmory stages, nudges throttle, pays tank leaks, pays store mass,
   shoves, then sets pulse mode and aims. Keep every branch behind a cheap `Count` or `Contains` test
   because it runs for every vehicle every frame, and catch everything:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L341-362
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

4. Compile-pin the signature so a game update breaks the build instead of the runtime:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L432-436

    // Never called. It exists so the compiler emits a reference to the patched method, which is
    // what puts Vehicle.PrepareWorker in docs/KSA-API-SURFACE.md and turns a signature change in
    // KSA into a build error here rather than a rocket that quietly stops steering.
    private static void PinTheSignature(Vehicle vehicle, SimStep step) => vehicle.PrepareWorker(step);
```

5. From your decision code call small queue methods (`Hold`, `Quiet`, `Stage`, `Shove`, ...) in whichever
   frame hook is convenient; they are plain collections and need no locking because everything runs on the
   main thread.
6. Watch for `Manual/None` flip-flopping in the log: if it appears, a write is landing in the wrong window.

## Complete example

- [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs)[^ksarmory-attitudehook] - the queue API, install, prefix
- [src/KSArmory/Ksa/StoreMass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/StoreMass.cs#L67-L99)[^ksarmory-storemass] and [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L162-L180)[^ksarmory-leaks] - two more users of the same window (articles 6 and 7)

## Variations and gotchas

- **`FlightComputer.CopyFrom` is lossless** (burn mode, attitude mode and frame, track target, custom
  target, manual thrust mode, controller state), so a write is lost only because it is on the wrong side
  of the snapshot.
- **Stage from this window, not from a hook.** For the controlled craft, while the `Sequence` or `EngineControl` gauge
  canvas is enabled (both start enabled, so by default on every step), the worker's `RunVehiclePostWorkInner` calls
  `PerformanceSequences.RecomputeForFlight` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796)[^ksa-physicsbubble], which walks the live `SequenceList`; `ActivateNextSequence`
  from a hook races that walk. See [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md).
- **Burn mode.** `PrepareWorker` itself clears `EngineOn` while `BurnMode == Auto`, which is why an
  autopilot holds Manual while it writes engine commands.
- **Every vehicle, every frame.** Do `Count` tests first; the prefix is hot.
- **Burn plans.** The window is settled for the vehicle solver only. `FlightComputer.AddBurn` or
  `RemoveBurn` here races the nearest-orbit worker; stock edits burn plans in `ApplyInputEvents`.
- **Pause and warp.** With simulation speed > 30x or ImGui capturing the keyboard, `PrepareWorker` clears
  held keys on the controlled craft; throttle keys you emulate must account for that.

## See also

- [Overview](overview.md), [Article 4](change-vehicles-without-racing-the-solver-4.md), [Article 6](change-vehicles-without-racing-the-solver-6.md), [Article 7](change-vehicles-without-racing-the-solver-7.md)
- [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md), [ksarmory/per-part-store-mass](../../../../mods/ksarmory/features/per-part-store-mass.md)
- [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../take-control-of-another-vehicle/overview.md), [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md), [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../../modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md)

[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: current/decomp/KSA/VehicleUpdateState.cs (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-storemass]: KSArmory: src/KSArmory/Ksa/StoreMass.cs
[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-leaks]: KSArmory: src/KSArmory/Ksa/Leaks.cs
