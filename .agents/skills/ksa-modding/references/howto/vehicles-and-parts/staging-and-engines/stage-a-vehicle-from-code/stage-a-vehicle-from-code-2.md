---
type: How-To
title: Stage inside the PrepareWorker prefix window so the sequence list is not mutated under the worker
description: Queue a stage request and run ActivateNextSequence from a Harmony prefix on Vehicle.PrepareWorker, the one point where the vehicle state is settled and the snapshot is not yet taken
category: vehicles-and-parts
group: staging-and-engines
topic: stage-a-vehicle-from-code
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/fly-another-vehicle
  - mods/ksarmory/features/prepareworker-prefix-command-window
techniques: [prepareworker-window]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.SequenceList, KSA.PhysicsBubble, KSA.SimStep]
tags: [staging, prepareworker, harmony, race, sequence-list]
---
Outcome: your UI or controller asks for a stage at any time; the stage fires at the last settled moment of the next frame,
inside a prefix on `Vehicle.PrepareWorker`. KSArmory's attitude hook does this for every command that must not race the worker,
staging included. Prerequisites: StarMap and Harmony. Demonstrated by [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md) and, for the staging command itself, [ksarmory/fly-another-vehicle](../../../../mods/ksarmory/features/fly-another-vehicle.md).

## Background: how the game does it

`PrepareWorker` is called per vehicle in `frame:dispatch-solvers`, immediately before its state is snapshotted for the worker
and before the vehicle job is queued; the prefix runs while the main thread owns everything. The worker's
`RunVehiclePostWorkInner` calls `PerformanceSequences.RecomputeForFlight` for the controlled vehicle while the `Sequence` or
`EngineControl` gauge canvas is enabled (both start enabled, so by default on every step) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L793-L796)[^ksa-physicsbubble], and that recompute walks the live `SequenceList`, so activating a sequence
(which changes its `Activated` flags and removes spent sequences) while a step is in flight is a race. See
[overview](overview.md).

## Lifecycle and timing

Request from anywhere on the main thread (`frame:draw-ui`, or any StarMap hook; these all run after the frame's dispatch, so the
request waits for the next one). The stage executes in the next frame's `frame:dispatch-solvers`, inside `Program.PrepareFrame`
after `ApplyVehicleSolvers` and `ApplyInputEvents` and before that vehicle's snapshot, and the staged parts' effects (engine
activation, the decoupler buffer) are then picked up by the worker step that this dispatch queues. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and
[Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md).

## Steps

1. **Patch the public virtual method** with a prefix. KSArmory pins the signature (so a change is a compile error) and
   records why in `Installed`/`Trouble`:

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

2. **Queue the request.** If the patch is not installed, fall back to staging directly, "because a stage that never fires is
   worse than one that can race":

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L239-254
    /// Fire this craft's next stage when its worker is next prepared. Staged directly when the hook
    /// is not installed, because a stage that never fires is worse than one that can race.
    /// </summary>
    public static void Stage(Vehicle craft)
    {
        if (!KsaWorld.IsAlive(craft)) return;

        if (!Installed)
        {
            VehicleCommand.Stage(craft);
            return;
        }

        Staging.RemoveWhere(v => !KsaWorld.IsAlive(v));
        Staging.Add(craft);
    }
```

3. **Drain the queue in the prefix**, wrapped in a try/catch because an exception here is thrown into the frame loop:

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L341-346
    private static void BeforePrepareWorker(Vehicle __instance)
    {
        try
        {
            if (Staging.Count > 0 && Staging.Remove(__instance)) VehicleCommand.Stage(__instance);

```

4. **The command itself** is the game's call, nothing more:

```csharp
// src/KSArmory/Ksa/VehicleCommand.cs L303-311
    /// <summary>
    /// Fire the next stage, which is how an engine is lit as well as how one is dropped. Called from
    /// <see cref="AttitudeHook"/>'s window; anything else asks <see cref="AttitudeHook.Stage"/>.
    /// </summary>
    public static void Stage(Vehicle craft)
    {
        if (!KsaWorld.IsAlive(craft)) return;
        craft.Parts.SequenceList.ActivateNextSequence(craft);
    }
```

## Complete example

- [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs)[^ksarmory-attitudehook]
- [src/KSArmory/Ksa/VehicleCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/VehicleCommand.cs)[^ksarmory-vehiclecommand]

## Variations and gotchas

- **Liveness.** Check `KsaWorld.IsAlive(craft)` both when queueing and in the prefix; the vehicle may be destroyed in between
  and the queue is pruned of dead vehicles.
- **Fallback races.** The direct call is only used when the hook could not install; gate your UI on `Installed` if you prefer a
  clear failure.
- **A prefix, not a postfix,** because the flight computer is snapshotted at the end of `PrepareWorker`; staging after it
  would be copied over or missed for a frame.
- **Never throw** from the prefix.

## See also

- [Overview](overview.md), [Article 1](stage-a-vehicle-from-code-1.md), [Article 3](stage-a-vehicle-from-code-3.md)
- [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md), [ksarmory/fly-another-vehicle](../../../../mods/ksarmory/features/fly-another-vehicle.md)
- `stage-automatically`

[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-vehiclecommand]: KSArmory: src/KSArmory/Ksa/VehicleCommand.cs
