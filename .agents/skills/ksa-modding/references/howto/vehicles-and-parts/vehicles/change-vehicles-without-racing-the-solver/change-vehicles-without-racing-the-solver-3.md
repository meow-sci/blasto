---
type: How-To
title: Destroy vehicles and fail parts safely from a frame hook
description: Join the solver, hold the shapes lock and dispose, queue part failures on the update state instead of splitting inline, and recognise the debris the engine creates
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/safe-vehicle-destruction-and-part-failure]
techniques: [job-barrier, reflection]
hooks:
  - starmap StarMapAfterGui
  - JobSystems.VehicleSolver.Wait()
  - reflection KSA.Vehicle._threadWorkerUpdateState
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types:
  - KSA.Universe
  - KSA.Vehicle
  - KSA.PartFailure
  - KSA.PartFailureEvent
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.ConstraintSim
  - KSA.JobSystems
tags: [destruction, part-failure, debris, threading, weapons]
---
Outcome: from a frame hook you can (a) destroy a craft with KSA's own explosion and debris, (b) break
specific parts off a surviving craft, and (c) remove a craft without leaving anything behind. KSArmory
found a different safe recipe for each and wrapped them in `KsaWorld`; this article reproduces them.

Prerequisite: StarMap. Demonstrated by [ksarmory/safe-vehicle-destruction-and-part-failure](../../../../mods/ksarmory/features/safe-vehicle-destruction-and-part-failure.md). The related
question of making vehicles survive is [Make a vehicle survive crashes and g-loads, or damage parts against crash tolerance](../make-a-vehicle-indestructible/overview.md).

## Background: how the game does it

- `Universe.DestroyVehicleFromEvent(Vehicle, VehicleDestructionEvent)` spawns the explosion, calls
  `PartFailure.ShedDebris(vehicle, 12)` (up to twelve pieces), re-points cameras and calls `DestroyVehicle`
  with `CrewDisposition.Kill` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971-L2018)[^ksa-universe]. `Universe.DestroyVehicle(vehicle, crew)` is
  the plain removal.
- Disposing a vehicle removes it from its bubble's vehicle list, which the worker enumerates; the engine
  surfaces a mod's mistake as "Collection was modified".
- Part failures are detected on the worker (`PartFailure.Detect`), stored in
  `VehicleUpdateState.PartFailureEvent` and applied on the main thread by
  `PhysicsBubble.ApplyRenderEventsToVehicles` right after the staged results [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L60-L80)[^ksa-partfailure]
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L935-L960)[^ksa-physicsbubble]. `PartFailureEvent.FailedParts` is a `List<Part>`;
  `DestroyWholeVehicle` selects whole-craft destruction [current/decomp/KSA/PartFailureEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailureEvent.cs#L1-L60)[^ksa-partfailureevent].
- `PartFailure.TrippedTheFragmentGuard(failed, total)` is `failed >= max(2, (int)(total * 0.5))`
  [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L77-L80).
- `Vehicle.GenerateSplitId` names a split-off piece `{id}_{n}`, first free `n` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1814-L1832)[^ksa-vehicle].

## Lifecycle and timing

| Operation | Safe where | Recipe |
|---|---|---|
| Destroy a craft | after joining the solver, in `frame:draw-ui` | `Wait()` then `DestroyVehicleFromEvent` |
| Remove a craft | after `Wait()` and under the shapes lock | `UnlockShapesBlocking` then `DestroyVehicle(.., EndMission)` |
| Break parts off | queued from any hook; applied by the engine at `frame:apply-solvers` of the next frame | set `VehicleUpdateState.PartFailureEvent` |

Why parts cannot be split inline: the worker's staged results (computed for the whole craft) are applied
at the next `ApplyVehicleSolvers`. A split between dispatch and apply lands those results on the
fragments; KSArmory measured an `ArgumentOutOfRangeException` from `FlightComputer.UpdateTvcParams`
every frame. A destroyed vehicle's staged results have nowhere to land, so destroying is safe after the
join. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) for the frame order.

## Steps

1. Wrap the barrier once, fail-soft:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2615-2627
    public static void WaitForVehicleSolvers()
    {
        try
        {
            JobSystems.VehicleSolver?.Wait();
        }
        catch (Exception e)
        {
            // Falling through to the destroy leaves the race in place; taking the frame down is
            // worse.
            Log.Warn($"could not join the vehicle solvers before a kill ({e.GetType().Name})");
        }
    }
```

2. Destroy: reuse the engine's own failure path, attributing it to collision damage. Call only after the
   barrier:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2668-2689
    public static void Destroy(Vehicle v, float blastSeverity)
    {
        if (!IsAlive(v)) return;
        try
        {
            var evt = new VehicleDestructionEvent
            {
                Cause = VehicleDestructionCause.Collision,
                PeakGLoad = blastSeverity,
                PeakDynamicPressure = blastSeverity,
            };
            Universe.DestroyVehicleFromEvent(v, evt);

            // The census holds a reference, and a disposed vehicle is not what a fresh walk would
            // have returned.
            InvalidateCensus();
        }
        catch (Exception e)
        {
            Log.Warn($"failed to destroy {DisplayName(v)}: {e.Message}");
        }
    }
```

3. Remove without debris: barrier, shapes lock, `DestroyVehicle` with `EndMission` (nothing aboard a spent
   stage died):

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2644-2659
    public static void Remove(Vehicle v)
    {
        if (!IsAlive(v)) return;
        try
        {
            WaitForVehicleSolvers();
            using ShapesUnlock shapes = ConstraintSim.UnlockShapesBlocking();

            Universe.DestroyVehicle(v, CrewDisposition.EndMission);
            InvalidateCensus();
        }
        catch (Exception e)
        {
            Log.Warn($"could not remove {DisplayName(v)} from the world: {e.Message}");
        }
    }
```

4. Break parts off: find the update state by reflection (verify the type once) and merge into any event
   already queued rather than replacing it:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3361-3408
    public static bool TryQueuePartFailure(Vehicle v, List<Part> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (!IsAlive(v) || parts.Count == 0 || !CanQueuePartFailures) return false;

        try
        {
            if (_updateStateField!.GetValue(v) is not VehicleUpdateState state)
            {
                // No update state means no physics bubble, so nothing is simulating it to break.
                Log.Debug(() => $"no update state on {DisplayName(v)}; its parts were not broken");
                return false;
            }

            // Before the engine splits it, so the pieces it names after this craft are known for
            // wreckage from the first frame they exist.
            Wreckage.Broke(v.Id);

            if (state.PartFailureEvent is { } pending)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    if (!pending.FailedParts.Contains(parts[i])) pending.FailedParts.Add(parts[i]);
                }

                return true;
            }

            InvalidateCensus();

            state.PartFailureEvent = new PartFailureEvent
            {
                FailedParts = parts,

                // Never set. The fragment guard was asked when the parts were queued, and a craft
                // it condemned was destroyed outright instead of reaching here.
                DestroyWholeVehicle = false,
            };

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"failed to queue {parts.Count} part failure(s) on {DisplayName(v)}: {e.Message}");
            return false;
        }
    }
```

5. Ask the engine whether the loss is fatal instead of copying its threshold, and destroy the whole craft
   if so:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3295-3306
    public static bool LosingThatManyPartsIsFatal(int failedCount, int partCount)
    {
        try
        {
            return PartFailure.TrippedTheFragmentGuard(failedCount, partCount);
        }
        catch (Exception e)
        {
            Log.Warn($"fragment guard unavailable ({e.GetType().Name}); treating {failedCount} lost parts as fatal");
            return true;
        }
    }
```

6. Batch. KSArmory collects kills during its step and applies them once, taking the barrier once per batch,
   whole-craft kills first so no part is isolated out of a corpse:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L4388-4421
    private void ApplyPendingKills()
    {
        if (_pendingKills.Count == 0 && _pendingPartKills.Count == 0) return;

        // Join the engine's vehicle solvers before disposing anything. Destroying a vehicle removes
        // it from the list those worker jobs are enumerating, and this hook runs while they are
        // live - see KsaWorld.WaitForVehicleSolvers for the frame order that makes that unavoidable
        // without the barrier.
        //
        // Taken once for the whole batch rather than per kill, which is most of why kills are
        // deferred into this list in the first place.
        KsaWorld.WaitForVehicleSolvers();

        foreach (Vehicle v in _pendingKills)
        {
            if (!KsaWorld.IsAlive(v)) continue;
            Announce($"destroyed {KsaWorld.DisplayName(v)}");
            KsaWorld.Destroy(v, blastSeverity: 50f);
        }

        // Whole-craft kills first: a craft queued dead may also have parts queued from an earlier
        // burst in the same frame, and isolating a part out of a corpse is a split with nothing
        // to split. IsAlive filters those out here rather than the two lists having to agree.
        //
        // These are handed to the engine rather than applied, and land on the next frame.
        foreach ((Vehicle v, List<Part> parts) in _pendingPartKills)
        {
            if (KsaWorld.IsAlive(v)) KsaWorld.TryQueuePartFailure(v, parts);
        }

        // Any round still chasing a corpse loses its lock rather than flying at a dangling ref.
        _pendingKills.Clear();
        _pendingPartKills.Clear();
    }
```

7. Recognise your own debris. The engine records no lineage, but split ids are `{parent}_{digits}`, so walk
   suffixes back to a craft you broke:

```csharp
// src/KSArmory/Sim/Wreckage.cs L38-69
    public bool IsPiece(string? id)
    {
        if (string.IsNullOrEmpty(id) || _broken.Count == 0) return false;

        ReadOnlySpan<char> name = id;
        while (TryParent(name, out ReadOnlySpan<char> parent))
        {
            if (_lookup.Contains(parent)) return true;
            name = parent;
        }

        return false;
    }

    public void Clear() => _broken.Clear();

    // "{parent}_{digits}" to its parent, or false for a name the engine did not give a piece.
    internal static bool TryParent(ReadOnlySpan<char> id, out ReadOnlySpan<char> parent)
    {
        parent = default;

        int underscore = id.LastIndexOf('_');
        if (underscore <= 0 || underscore == id.Length - 1) return false;

        foreach (char c in id[(underscore + 1)..])
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        parent = id[..underscore];
        return true;
    }
```

   Call `Wreckage.Broke(v.Id)` before queueing the failure (the mod does, inside `TryQueuePartFailure`) so
   pieces are known from their first frame. Clear the set on save load.

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2615-L2689)[^ksarmory-ksaworld] and [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3295-L3408)
- [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L4388-L4421)[^ksarmory-weaponsystem] - batching
- [src/KSArmory/Sim/Wreckage.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Wreckage.cs)[^ksarmory-wreckage] - lineage

## Variations and gotchas

- **`ShedDebris` can leave up to twelve vehicles.** KSArmory recorded seven extra vehicles in worlds where
  spent stages were destroyed with `DestroyVehicleFromEvent`; use the removal recipe for disposal.
- **`PartFailureEvent` is applied only when the vehicle has an update state** (a bubble). A craft with no
  state returns false from `TryQueuePartFailure`; destroy it instead.
- **Private field.** `Vehicle._threadWorkerUpdateState` is reached by reflection and verified to be a
  `VehicleUpdateState`; if the field moves the mod logs once and falls back to whole-craft kills.
- **A queued failure is skipped when the engine also has a destruction event for that vehicle.**
  `ApplyRenderEventsToVehicles` applies `PartFailureEvent` only `if (!flag)`, where `flag` is
  `DestructionEvent != null`, and applies the destruction afterwards [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L918-L972).
- **Not a Harmony patch.** Everything here is called, not patched, so it is stable across updates unless
  a signature changes.

## See also

- [Overview](overview.md), [Article 2](change-vehicles-without-racing-the-solver-2.md), [Article 4](change-vehicles-without-racing-the-solver-4.md)
- [ksarmory/safe-vehicle-destruction-and-part-failure](../../../../mods/ksarmory/features/safe-vehicle-destruction-and-part-failure.md)
- [Make a vehicle survive crashes and g-loads, or damage parts against crash tolerance](../make-a-vehicle-indestructible/overview.md), [write-worker-thread-safe-solver-patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)

[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-partfailureevent]: current/decomp/KSA/PartFailureEvent.cs (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-weaponsystem]: KSArmory: src/KSArmory/Ksa/WeaponSystem.cs
[^ksa-partfailure]: current/decomp/KSA/PartFailure.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-wreckage]: KSArmory: src/KSArmory/Sim/Wreckage.cs
[^ksarmory-ksaworld]: KSArmory: src/KSArmory/Ksa/KsaWorld.cs
