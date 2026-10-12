---
type: How-To
title: Change a part's mass at runtime in the PrepareWorker window
description: Derive the wanted mass from the roster every frame and write it through Part.SetInertMassPropertiesAsmb and an Unsafe ref to Vehicle.Props from the PrepareWorker prefix
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 7
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/per-part-store-mass]
techniques: [unsafe-ref-write, prepareworker-window, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, physics:vehicle-solver, frame:apply-solvers]
game_types:
  - KSA.Part
  - KSA.InertMass
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.VehicleProperties
  - KSA.FlightComputer
  - KSA.MassProperties
tags: [mass, inertia, weapons, double-buffer]
---
Outcome: a part gets lighter (or heavier) while the vehicle flies, and total mass, inertia, bounding sphere,
delta-v readouts and the autopilot all agree. KSArmory does this so a rail that has fired its missile is
78 kg lighter; rounds are not KSA parts, so the engine never learns they left.

Prerequisites: StarMap, Harmony, the prefix of article 5. Demonstrated by
[ksarmory/per-part-store-mass](../../../../mods/ksarmory/features/per-part-store-mass.md).

## Background: how the game does it

- A part's mass lives in its `InertMass` module as `OffsetMassProperties`; `Part.SetInertMassPropertiesAsmb`
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1298)[^ksa-part] is the slot (KSArmory's comment says a parachute uses it when it loses its canopy).
- `PartTree.RefreshStaticMass()` re-sums every `InertMass` at its part's position and rotation, and
  `ComputeInertMassPropertiesAsmb()` returns the tree's total [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1019-L1037)[^ksa-parttree] [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1200). The vehicle copies
  that total into `VehicleProperties.InertMassPropsAsmb` only on structural changes
  (`UpdateAfterPartTreeModification`) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1908)[^ksa-vehicle], so a part's new mass alone reaches
  nothing that flies.
- `VehicleProperties.RecomputeMassProperties(stores, moles)` rebuilds total mass, inverse mass and the
  bounding sphere [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L99-L107)[^ksa-vehicleproperties].
- `Vehicle._props` is double-buffered. The worker copies `ReadOnlyVehicle.Props` into its result on first
  write (`GetNewProps`) [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L183-L193)[^ksa-vehicleupdatestate], and `UpdateFromTaskResultsUnsynchronized`
  replaces `_props` with the worker's copy [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2450). A main-thread mass write while the
  worker runs is overwritten the next frame.
- `Vehicle.Props` is `ref readonly` (`ref _props`) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L541).

## Lifecycle and timing

| Step | Hook | Frame |
|---|---|---|
| queue (`StoreMass.Want`) | simulation step after the fire loop, from `StarMapAfterGui` | frame N, after N's step was dispatched |
| write (`StoreMass.Apply`) | `Vehicle.PrepareWorker` prefix | frame N+1, before the snapshot |
| worker reads it | first `GetNewProps()` | frame N+1 worker |
| main thread receives it | `ApplyVehicleSolvers` | frame N+2 |

The queue is cleared on save load, and `Apply` skips parts whose `Tree` is no longer the craft's, so a
split or dock between queue and apply cannot write onto the wrong craft. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Compute the wanted mass fresh every frame from what is aboard, never by subtracting per event. KSArmory's
   `StoreLoad.Factor(fullKg, shedKg)` is `(full - shed) / full` clamped to `[0.01, 1]`, so reloads, refused
   shots and save loads need no undo.
2. Remember the full-load properties per part in a `ConditionalWeakTable`, and re-capture them whenever the
   current mass is not what you last wrote (the engine restored the template's after a load or rescale):

```csharp
// src/KSArmory/Ksa/StoreMass.cs L20-62
{
    // The part's mass with its full load, read the first time it is seen and again whenever the
    // engine puts the template's back, as a load or a rescale does.
    private sealed class Held
    {
        public OffsetMassProperties Full;
        public float WrittenKg = float.NaN;
        public double WantedFactor = 1.0;
    }

    private static readonly ConditionalWeakTable<Part, Held> _held = new();
    private static readonly Dictionary<Vehicle, List<Part>> _owed = new(ReferenceEqualityComparer.Instance);

    /// <summary>Wants a part lighter by <paramref name="shedKg"/>. Called each frame for every launcher and dispenser.</summary>
    public static void Want(Vehicle craft, Part part, double shedKg)
    {
        try
        {
            if (!TryInertMass(part, out InertMass module)) return;

            Held held = _held.GetValue(part, static _ => new Held());
            float now = module.MassPropertiesAsmb.Props.Mass;

            // Not what was last written: the engine has put the template's back, so that is full.
            if (float.IsNaN(held.WrittenKg) || Math.Abs(now - held.WrittenKg) > 1e-3f)
            {
                held.Full = module.MassPropertiesAsmb;
                held.WrittenKg = now;
            }

            held.WantedFactor = StoreLoad.Factor(held.Full.Props.Mass, shedKg);
            float wantedKg = (float)(held.Full.Props.Mass * held.WantedFactor);

            // A tenth of a percent of the part, so a gun firing is a write every few dozen rounds
            // rather than a whole recompute of the craft every frame; a store is always far more.
            float band = Math.Max(1e-3f, 0.001f * held.Full.Props.Mass);
            if (Math.Abs(wantedKg - held.WrittenKg) <= band) return;

            if (!_owed.TryGetValue(craft, out List<Part>? parts)) _owed[craft] = parts = [];
            if (!parts.Contains(part)) parts.Add(part);
        }
        catch
        {
```

3. Pay only when the difference exceeds 0.1% of the part (minimum 1 g), so a belt-fed gun causes one write
   every few dozen rounds, not every frame (the `band` test above).
4. In the `PrepareWorker` prefix, scale the full properties (mass and inertia together, offset unchanged),
   write the part, then propagate through the tree, the vehicle and the flight computer:

```csharp
// src/KSArmory/Ksa/StoreMass.cs L67-99
    /// <summary>Writes what is owed on one craft. Only from <see cref="AttitudeHook"/>'s prefix.</summary>
    public static void Apply(Vehicle craft)
    {
        if (_owed.Count == 0 || !_owed.Remove(craft, out List<Part>? parts)) return;

        PartTree tree = craft.Parts;
        bool wrote = false;

        foreach (Part part in parts)
        {
            if (!_held.TryGetValue(part, out Held? held) || !ReferenceEquals(part.Tree, tree)) continue;

            OffsetMassProperties props = held.Full;
            props.Props = held.Full.Props.Scale((float)held.WantedFactor);

            part.SetInertMassPropertiesAsmb(in props);
            Log.Info($"store mass: {part.Id} on {KsaWorld.DisplayName(craft)} now {props.Props.Mass:F2} kg "
                     + $"of {held.Full.Props.Mass:F2} (was {held.WrittenKg:F2})");
            held.WrittenKg = props.Props.Mass;
            wrote = true;
        }

        if (!wrote) return;

        // The tree's sum, then the craft's: the engine copies one into the other only on a structural
        // change, so a part's new mass alone reaches nothing that flies.
        tree.RefreshStaticMass();
        ref VehicleProperties props2 = ref Unsafe.AsRef(in craft.Props);
        props2.InertMassPropsAsmb = tree.ComputeInertMassPropertiesAsmb();
        props2.RecomputeMassProperties(tree.SubstanceStores, tree.Moles.States);
        tree.PerformanceSequences.SetDirty();
        craft.FlightComputer.ReadUpdatedVehicleConfiguration(craft);
    }
```

5. Call `StoreMass.Apply(__instance)` from the prefix and `StoreMass.Clear()` when the world is forgotten.
6. Verify: the log shows `store mass: <part> on <craft> now <kg> of <full> (was <kg>)` and the staging
   readouts and `Vehicle.Props.TotalMass` agree.

## Complete example

- [src/KSArmory/Ksa/StoreMass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/StoreMass.cs)[^ksarmory-storemass] - queue and apply
- [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L341-L362)[^ksarmory-attitudehook] - the prefix that calls it

## Variations and gotchas

- **Scale where mass is declared, never move it to another point.** A MIRV bus's mass sits on its thruster
  ring; taking it off at a tube would put every attitude correction off-axis.
- **Derive, do not subtract.** Idempotence is the point: the same wanted mass written twice is harmless.
- **Not saved.** The part's inert mass is the template's on load, which matches a magazine that reloads
  with the save.
- **Unsafe is local.** `Unsafe.AsRef(in craft.Props)` exists only because `Props` is `ref readonly`; it
  writes the live `_props` in the one window where that is safe. Do not use it elsewhere.
- **Performance sequences.** `tree.PerformanceSequences.SetDirty()` marks the staging simulation stale, which is what `IsDirty` and `RecomputeIfDirty` readers (the editor, a private list) act on and also sets `PartTree.HasUnsavedChanges`. In flight the controlled vehicle's list is recomputed on the vehicle-solver worker every step while the `Sequence` or `EngineControl` gauge canvas is enabled (the default), so the stock readout follows the drain either way.

## See also

- [Overview](overview.md), [Article 5](change-vehicles-without-racing-the-solver-5.md), [Article 6](change-vehicles-without-racing-the-solver-6.md)
- [ksarmory/per-part-store-mass](../../../../mods/ksarmory/features/per-part-store-mass.md)
- [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md)

[^ksa-vehicleupdatestate]: current/decomp/KSA/VehicleUpdateState.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-storemass]: KSArmory: src/KSArmory/Ksa/StoreMass.cs
[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleproperties]: current/decomp/KSA/VehicleProperties.cs (decompiled, build 2026.10.10.5554)
