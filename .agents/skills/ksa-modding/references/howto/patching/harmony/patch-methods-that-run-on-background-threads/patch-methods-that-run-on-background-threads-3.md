---
type: How-To
title: Worker-side postfix merging commands from the main thread
description: "Answer a question on a vehicle worker thread from a concurrent dictionary that the main thread fills before dispatch: a postfix on PhysicsBubble.KittenWantsWake keeps burning cars off rails, with a prefix on Vehicle.PrepareWorker doing the main-thread writes"
category: patching
group: harmony
topic: patch-methods-that-run-on-background-threads
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
  - mods/ksacars/features/keep-thrusting-craft-off-rails
techniques: [harmony-postfix, harmony-prefix, background-thread, concurrent-dictionary]
hooks:
  - harmony-postfix KSA.PhysicsBubble.KittenWantsWake
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.FlightComputer.ComputeControl
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver, startup:all-mods-loaded]
game_types: [KSA.PhysicsBubble, KSA.VehicleUpdateState, KSA.Vehicle, KSA.FlightComputer]
tags: [harmony, postfix, worker-thread, concurrent-dictionary, rails, publish-before-dispatch]
---

Some questions are asked by the game on a worker thread, and the answer depends on something only the main thread
knows. The pattern in two KSA mods: the main thread writes the answer into a thread-safe store *before* the step is
dispatched, and a postfix on the worker-side method reads it. KSACars uses it to keep a car with burning rockets from
being put on rails above the atmosphere. KSA-AdvancedFlightComputer uses the same publish-then-read shape for the RCS
executor and guidance, through its single `ComputeControl` postfix (see [One worker-side postfix merging every writer](../coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-5.md)).

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated in [Keep a craft with mod thrust off rails](../../../../mods/ksacars/features/keep-thrusting-craft-off-rails.md) and
[One ComputeControl postfix as an actuator command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md).

## Background: how the game does it

KSA puts a craft "on rails" whenever it can; only a firing engine, an active actuator and a few other reasons keep it under
full physics. On rails, a `Freefall` craft is moved along its orbit analytically and nothing written to its velocity is
read. `PhysicsBubble` asks a private static method, `KittenWantsWake(VehicleUpdateState)`, in both places it decides
rails status: once for a lone vehicle at the start of its step, and per vehicle in every full-physics sub-step:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1287-L1294
if (vehicleState.AnyActuatorActive())
{
	situation = situation.WithOnRails(isOnRails: false);
}
if (KittenWantsWake(vehicleState))
{
	situation = situation.WithOnRails(isOnRails: false);
}
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1287-L1294)[^ksa-physicsbubble]

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1800-L1820
private static bool KittenWantsWake(VehicleUpdateState vehicleState)
{
	if (!vehicleState.IsKitten)
	{
		return false;
	}
	int result;
	if (vehicleState.ManualControlInputs.ThrusterCommandFlags == ThrusterMapFlags.None && !vehicleState.ManualControlInputs.GrabHeld)
	{
		LocomotionState locomotionState = vehicleState.LocomotionState;
		if (locomotionState.Mode != LocomotionMode.Ladder || locomotionState.LadderParked)
		{
			result = (vehicleState.LadderWorkerInputs.PendingAttach.Valid ? 1 : 0);
			goto IL_0057;
		}
	}
	result = 1;
	goto IL_0057;
	IL_0057:
	return (byte)result != 0;
}
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800-L1820)

For a non-kitten it returns `false`, so a patch that turns `false` into `true` for chosen vehicles wakes them without
touching anything else.

## Lifecycle and timing

| Piece | Hook | Thread | Lifecycle |
|---|---|---|---|
| Install | `[StarMapAllModsLoaded]` then `RailsHook.Install` | main | `startup:all-mods-loaded` |
| `Mark`, rebuild and `TakeOffRails` | Harmony prefix on `Vehicle.PrepareWorker`, inside `Universe.ExecuteNextVehicleSolvers` | main | `frame:dispatch-solvers` |
| `AfterWantsWake` | postfix on `PhysicsBubble.KittenWantsWake`, called from the bubble's step job | vehicle-solver worker | `physics:vehicle-solver` |

The mark is written on the main thread before the step is dispatched and read on worker threads during it, hence the
`ConcurrentDictionary`. The publish happens-before the read because the job is queued after the prefix has run.
See [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) and [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Pick the writer window.** `Vehicle.PrepareWorker` is the only thing in the settled window between
   `ApplyVehicleSolvers` and the snapshot that a mod can reach; a write from a StarMap GUI hook lands after the snapshot
   and is overwritten. KSACars hooks it with a prefix and pins the signature with a never-called method:

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L38-L60
    try
    {
        MethodInfo? target = typeof(Vehicle).GetMethod(nameof(Vehicle.PrepareWorker), [typeof(SimStep)]);
        if (target is null)
        {
            Log.Warn("cars cannot drive: KSA has no Vehicle.PrepareWorker(SimStep) to hook");
            return;
        }

        MethodInfo prefix = typeof(PhysicsHook).GetMethod(
            nameof(BeforePrepareWorker), BindingFlags.NonPublic | BindingFlags.Static)!;

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        Installed = true;
        Log.Info("car physics hooked into Vehicle.PrepareWorker");
    }
    catch (Exception e)
    {
        Log.Error("could not hook car physics; no car can drive", e);
    }
}
```

Source: [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs#L38-L60)[^ksacars-physicshook]

2. **Write the marks in the prefix.** The car code calls `RailsHook.Mark(craft, burning)` every physics window:

```csharp
// src/KSACars/Ksa/Buggies.cs L564-L566
// KSA puts a craft out of the air onto its orbit unless an engine of its own is firing.
bool burning = e.Rockets > 0.0 || e.Boosting;
RailsHook.Mark(craft, burning);
```

Source: [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L564-L566)[^ksacars-buggies]

3. **Keep the store thread-safe and the lookup keyed by the object the worker sees.** `Burning` is a
   `ConcurrentDictionary<Vehicle, byte>`; the worker reads the `Vehicle` through `VehicleUpdateState.ReadOnlyVehicle`:

```csharp
// src/KSACars/Ksa/RailsHook.cs L25-L40
// Written in the physics window and read from KSA's physics worker.
private static readonly ConcurrentDictionary<Vehicle, byte> Burning = new();

private static Harmony? _harmony;
private static bool _complained;

public static bool Installed { get; private set; }

/// <summary>Says whether a car's rockets are pushing, which is what keeps it off its orbit.</summary>
public static void Mark(Vehicle craft, bool burning)
{
    if (burning) Burning[craft] = 0;
    else Burning.TryRemove(craft, out _);
}

public static void Forget(Vehicle craft) => Burning.TryRemove(craft, out _);
```

Source: [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs#L25-L40)[^ksacars-railshook]

4. **Install after checking the shape of a private target.** The method is private, so there is no signature to pin; the
   install validates the return type and first parameter type and warns instead of patching when they moved:

```csharp
// src/KSACars/Ksa/RailsHook.cs L42-L67
public static void Install()
{
    if (Installed) return;

    try
    {
        MethodInfo? target = AccessTools.Method(typeof(PhysicsBubble), "KittenWantsWake");
        ParameterInfo[]? parameters = target?.GetParameters();
        if (target is null || target.ReturnType != typeof(bool)
            || parameters is not [{ } first, ..] || first.ParameterType != typeof(VehicleUpdateState))
        {
            Log.Warn("KSA's wake check has moved; a car's rockets stop pushing above the atmosphere");
            return;
        }

        MethodInfo postfix = typeof(RailsHook).GetMethod(nameof(AfterWantsWake),
                                                         BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        Installed = true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the wake check; a car's rockets stop pushing above the atmosphere ({e.Message})");
    }
}
```

Source: [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs#L42-L67)

5. **Write a postfix that cannot throw and only says yes.** The first parameter is matched by position (`__0`) whatever
   KSA names it; the postfix never turns KSA's `true` into `false`, so a kitten's own wake logic is untouched:

```csharp
// src/KSACars/Ksa/RailsHook.cs L85-L99
// Inside KSA's physics worker: nothing here may throw. The first parameter is matched by position,
// whatever KSA names it.
private static void AfterWantsWake(VehicleUpdateState __0, ref bool __result)
{
    try
    {
        if (!__result && __0?.ReadOnlyVehicle is { } craft && Burning.ContainsKey(craft)) __result = true;
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("keeping a burning car off its orbit failed", e);
    }
}
```

Source: [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs#L85-L99)

6. **Repair the state a railed vehicle left behind, in the prefix.** The feature page documents the details (on rails
   with no contact the kinematic state is stale, so the mod rebuilds it from the orbit; a landed car is only woken, because
   rebuilding from the orbit would leave it in space while the planet turns away).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs) | Store, install with validation, worker postfix |
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs) | The `PrepareWorker` prefix and the signature pin |
| [AdvancedFlightComputer/Core/VehicleCommandSink.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/VehicleCommandSink.cs)[^ksa-advancedflightcomputer-vehiclecommandsink] | The `ComputeControl` postfix, which reads per-vehicle commands published before dispatch |

## Variations and gotchas

- **Workers run many times per step.** The postfix is called per craft per full-physics sub-step; it must not throw,
  allocate or touch main-thread state.
- **Concurrent collections, not locks.** The main thread publishes, workers read; a `ConcurrentDictionary` or an
  immutable object swapped by reference needs no lock on the hot path.
- **Remove the mark when the reason ends.** Once the rockets are cut, KSA may rail the car again.
- **Name-based private targets can silently go away.** An overload or rename disables the hook; the install message
  tells the player what stops working.
- **For commands rather than questions** (AFC): a postfix on `FlightComputer.ComputeControl` runs on the worker's
  copy of the flight computer, so lookups must be keyed by an object the copy shares by reference
  ([ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md)); the copy is not private to the step.

## See also

- [Patches on planner tasks that run on pooled threads](patch-methods-that-run-on-background-threads-1.md), [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](patch-methods-that-run-on-background-threads-2.md), [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md), [Look the state up through __instance.BurnPlan in a ComputeControl prefix](patch-methods-that-run-on-background-threads-5.md)
- [Patching game methods that run on worker and pool threads](overview.md)
- [Keep a craft with mod thrust off rails](../../../../mods/ksacars/features/keep-thrusting-craft-off-rails.md), [One ComputeControl postfix as an actuator command sink](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-command-sink.md)
- Related tasks: write-worker-thread-safe-solver-patches, mutate-vehicles-at-a-physics-safe-point

[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksacars-physicshook]: KSACars/src/KSACars/Ksa/PhysicsHook.cs
[^ksacars-buggies]: KSACars/src/KSACars/Ksa/Buggies.cs
[^ksacars-railshook]: KSACars/src/KSACars/Ksa/RailsHook.cs
[^ksa-advancedflightcomputer-vehiclecommandsink]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/VehicleCommandSink.cs
