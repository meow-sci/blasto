---
type: How-To
title: Drain a tank through its mole states and recompute mass
description: Take mass off specific tanks with Mole.ConsumeStored on the live state, then repeat the bookkeeping DepleteConsumables does so mass, fill fraction and the flight computer follow
category: vehicles-and-parts
group: modules-and-resources
topic: refill-or-drain-resources
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/holed-tank-leaks]
techniques: [module-state-write, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Mole
  - KSA.MoleState
  - KSA.Tank
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.VehicleProperties
  - KSA.FlightComputer
  - KSA.ResourceManager
tags: [drain, leak, tanks, mass, bookkeeping]
---
Outcome: remove an exact amount of propellant from one tank (rather than emptying every tank) and have the craft's
mass, engines, staging readouts and flight computer all follow. KSArmory does this to make holed tanks leak. The
timing proof (why it must be paid from the `PrepareWorker` prefix) is in [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)
article 6; this article is the resource recipe.

Prerequisites: StarMap, Harmony. Demonstrated by [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md).

## Background: how the game does it

- `Vehicle.DepleteConsumables()` empties **all** tanks: `ResourceManager.DepleteAllTanks`, then
  `_props.RecomputeMassProperties(...)`, then `FlightComputer.ReadUpdatedVehicleConfiguration` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3208-L3213)[^ksa-vehicle]
  [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L468-L480)[^ksa-resourcemanager]. For a partial drain you do the same steps on selected moles.
- `Mole.ConsumeStored(ref MoleState state, float consumeMass)` subtracts and clamps at zero, returning what it took
  [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L89-L103)[^ksa-mole]; the live `MoleState` is reached with
  `moles.GetModuleAndAllMutableStatesForInitialization(mole)` [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L479-L492)[^ksa-modulestateful].
- After the change, `OnContentsReplaced` does three things: recompute the total fill fraction, set the Moles global
  `ValuesUpdated` flag, mark performance sequences dirty [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L482-L487).
- `Vehicle.Props` is a `ref readonly VehicleProperties` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L541), so recomputing mass in place needs
  `Unsafe.AsRef(in craft.Props)`.

## Lifecycle and timing

The mod measures flow in its step (`frame:draw-ui`, every 0.1 s of simulated time) and records the debt per craft and
per mole. The debt is paid in a prefix on `Vehicle.PrepareWorker` (`frame:dispatch-solvers`), the last point before
the worker snapshots the vehicle; a payment made from the GUI hook is overwritten whenever the worker wrote any mole
state that step, and the `ValuesUpdated` flag is always overwritten. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Record what is owed**, per craft, per mole, never more than the mole holds:

```csharp
// src/KSArmory/Ksa/Leaks.cs L430-449
    // Shares a draw between the liquids in a tank by mass, onto what the craft already owes.
    private static void Owe(Vehicle craft, Tank tank, ReadOnlySpan<MoleState> states, double mass, double taken)
    {
        if (!_owed.TryGetValue(craft, out Dictionary<Mole, float>? owed))
        {
            _owed[craft] = owed = new Dictionary<Mole, float>(ReferenceEqualityComparer.Instance);
        }

        foreach (Mole mole in tank.Moles)
        {
            if (mole.Liquid is null) continue;

            double held = states[mole.StatesIdx].Mass;
            float more = (float)(taken * (held / mass));

            // Never more than the tank holds, however many passes a craft that is not being stepped
            // has missed.
            owed[mole] = (float)Math.Min(owed.GetValueOrDefault(mole) + more, held);
        }
    }
```

2. **Pay it in the `PrepareWorker` prefix**: take the live state by ref, consume, then repeat `DepleteConsumables`'
   bookkeeping:

```csharp
// src/KSArmory/Ksa/Leaks.cs L162-180
    public static void Apply(Vehicle craft)
    {
        if (_owed.Count == 0 || !_owed.Remove(craft, out Dictionary<Mole, float>? owed)) return;

        PartTree parts = craft.Parts;
        var moles = parts.Moles;

        foreach ((Mole mole, float kg) in owed)
        {
            var held = moles.GetModuleAndAllMutableStatesForInitialization(mole);
            held.Module.ConsumeStored(ref held.State, kg);
        }

        parts.RecomputeTotalFillFraction();
        moles.GetMutableGlobalStateForInitialization().ValuesUpdated = true;
        parts.PerformanceSequences.SetDirty();
        Unsafe.AsRef(in craft.Props).RecomputeMassProperties(parts.SubstanceStores, parts.Moles.States);
        craft.FlightComputer.ReadUpdatedVehicleConfiguration(craft);
    }
```

3. Install the prefix once (see [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md) article 5); call
   `Leaks.Apply(__instance)` in it.
4. **Self-check** the first payment: compare the tank's mass with `mass - taken` and log whether the write held. A
   "did NOT hold" log means the payment ran on the wrong side of the snapshot.
5. To **add** mass instead of removing it, write `state.Mass` directly (capped at `Mole.GetStoredMass(ContainerVolume)`)
   and run the same bookkeeping; `ConsumeStored` only handles removal.

## Complete example

- [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L126-L180)[^ksarmory-leaks] and [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L430-L449)
- [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L341-L362)[^ksarmory-attitudehook] - the prefix that calls it

## Variations and gotchas

- **Mass first.** `RecomputeMassProperties` before `ReadUpdatedVehicleConfiguration`, so the flight computer reads the
  new mass.
- **Staging readouts.** `PerformanceSequences.SetDirty()` marks the staging simulation stale, which is what `IsDirty` and `RecomputeIfDirty` readers (the editor, a private list) act on and also sets `PartTree.HasUnsavedChanges`. In flight the controlled vehicle's list is recomputed on the vehicle-solver worker every step while the `Sequence` or `EngineControl` gauge canvas is enabled (the default), so the stock readout follows the drain either way.
- **Allocation.** Share a draw across a tank's liquid moles by mass share.
- **Solid grain** lives on `SolidGrainSegment` (`ISubstanceStore`), not a `Tank`; `Tank.Moles` does not include it.
- **Updates.** `ResourceManager.OnContentsReplaced` is private; the mod repeats its three calls by name, so a change
  there needs a matching change here.

## See also

- [Overview](overview.md), [Article 1](refill-or-drain-resources-1.md), [Article 2](refill-or-drain-resources-2.md)
- [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md), [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md)

[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-mole]: current/decomp/KSA/Mole.cs (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanager]: current/decomp/KSA/ResourceManager.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-leaks]: KSArmory: src/KSArmory/Ksa/Leaks.cs
[^ksa-modulestateful]: current/decomp/KSA/ModuleStateful.cs (decompiled, build 2026.10.10.5554)
