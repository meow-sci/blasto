---
type: How-To
title: Write mole (tank) state where the worker will not overwrite it
description: Owe tank mass in a queue, pay it from the PrepareWorker prefix through the live MoleState, and repeat the bookkeeping the engine does after emptying tanks
category: vehicles-and-parts
group: vehicles
topic: change-vehicles-without-racing-the-solver
approach: 6
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/holed-tank-leaks]
techniques: [module-state-write, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui, physics:vehicle-solver, frame:apply-solvers]
game_types:
  - KSA.Mole
  - KSA.MoleState
  - KSA.Tank
  - KSA.ModuleStateful
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.ResourceManager
tags: [tanks, mole-state, copy-on-write, leaks, mass]
---
Outcome: you can remove (or add) tank contents from a mod and have them survive the worker's write-back,
with mass, fill fraction, staging readouts and the flight computer all following. KSArmory uses it to make
holed tanks leak: each pass it works out how much has run out, records the debt, and pays it from the
`PrepareWorker` prefix.

Prerequisites: StarMap, Harmony, and the `PrepareWorker` prefix of article 5. Demonstrated by
[ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md). Reading tank state is [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../../modules-and-resources/read-live-part-module-state/overview.md); the refill and
drain calls as a topic are [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md).

## Background: how the game does it

Tank contents are `MoleState` structs in `ModuleStateful<Mole, MoleState, MoleGlobalState, EmptyStruct>`.

- The live array and global state belong to the main thread. At the snapshot `StateUpdater.Prepare()`
  resets the worker's "new states" marker and copies the global state; the worker's first write calls
  `GetNewStates()`, which copies the live array into its own buffer [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L685-L697)[^ksa-modulestateful]
  [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L771-L784).
- The next `ApplyVehicleSolvers` runs `UpdateBulkStates`: the global state is copied back
  unconditionally, and the new state array over the live one whenever the worker wrote any
  [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L383-L413). An engine drawing propellant counts, so a main-thread tank edit made
  while the worker runs is lost whenever the craft is burning; the `ValuesUpdated` flag is always lost.
- `GetModuleAndAllMutableStatesForInitialization(module)` returns a ref to the live state
  [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L479-L492), and `Mole.ConsumeStored(ref MoleState, kg)` subtracts and clamps
  at zero [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L89-L105)[^ksa-mole].
- After emptying tanks the engine runs `ResourceManager.OnContentsReplaced` (fill fraction, `ValuesUpdated`,
  performance sequences dirty) [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L482-L487)[^ksa-resourcemanager], then `Vehicle.DepleteConsumables`
  recomputes properties and the flight computer's configuration [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3208-L3213)[^ksa-vehicle].
- `Rocket`'s module update returns early unless a rocket is firing or `ValuesUpdated` is set
  [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L84-L95)[^ksa-rocket], so skipping the flag leaves the engines unaware of the change.

## Lifecycle and timing

| Moment | Hook | Work |
|---|---|---|
| frame N, `frame:draw-ui` or `frame:celestials` | the mod's step, on the simulation clock, every 0.1 s | measure flow, add to the per-craft debt dictionary |
| frame N+1, `frame:dispatch-solvers` | `Vehicle.PrepareWorker` prefix | pay the debt into the live `MoleState`, run the bookkeeping |
| frame N+1 worker | copy-on-write of the paid states | the worker starts from the reduced mass |
| frame N+2 `frame:apply-solvers` | `UpdateBulkStates` | writes the worker's states (which include the payment) back |

Measure at 10 Hz on the simulation clock, not per frame: each payment costs a mass-properties recompute
and a flight-computer rebuild. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) for the frame order.

## Steps

1. Keep a per-craft debt dictionary of `Mole` -> kilograms. Share a draw between liquids by mass and never
   owe more than a mole holds, so a craft that misses passes cannot go negative:

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

2. Pay in the `PrepareWorker` prefix (call `Leaks.Apply(__instance)` from the prefix of article 5). Take the
   live state by ref, consume, then repeat the engine's own bookkeeping:

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

3. `Vehicle.Props` is a `ref readonly`; the mod recomputes it in place with `Unsafe.AsRef(in craft.Props)`
   where `DepleteConsumables` writes `_props` directly. That is the one place an `Unsafe` write is needed;
   see article 7 for the same trick on mass.
4. Clear the debt dictionary on save load or when the feature is turned off (`Leaks.Clear()`).
5. Self-check once: compare the tank mass after the first payment with `mass - taken` and log whether
   the write held. A "did not hold" result means the payment ran on the wrong side of the snapshot.

## Complete example

- [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L126-L180)[^ksarmory-leaks] - cadence, debt, payment
- [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L341-L362)[^ksarmory-attitudehook] - the prefix that calls `Leaks.Apply`

## Variations and gotchas

- **Adding contents** is the same with a negative debt and a cap at capacity; use `Mole` capacity checks of
  your own, because `ConsumeStored` only handles removal.
- **A craft the worker is not stepping** (far away, on rails) misses passes; the cap on the debt is what
  keeps it correct when it is stepped again.
- **Vehicle splits.** Debts are keyed by `Vehicle`; a split between queue and apply moves the tank to a new
  vehicle whose debt is empty, and `Apply` simply finds nothing. Re-measure after splits.
- **Staging readouts** (delta-v): `PerformanceSequences.SetDirty()` (part of the bookkeeping) marks the staging simulation stale, which is what `IsDirty` and `RecomputeIfDirty` readers (the editor, a private list) act on and also sets `PartTree.HasUnsavedChanges`. In flight the controlled vehicle's list is recomputed on the vehicle-solver worker every step while the `Sequence` or `EngineControl` gauge canvas is enabled (the default), so the stock readout follows the drain either way.

## See also

- [Overview](overview.md), [Article 5](change-vehicles-without-racing-the-solver-5.md), [Article 7](change-vehicles-without-racing-the-solver-7.md)
- [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md)
- [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md), [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../../modules-and-resources/read-live-part-module-state/overview.md)

[^ksa-rocket]: current/decomp/KSA/Rocket.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-attitudehook]: KSArmory: src/KSArmory/Ksa/AttitudeHook.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-mole]: current/decomp/KSA/Mole.cs (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanager]: current/decomp/KSA/ResourceManager.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-leaks]: KSArmory: src/KSArmory/Ksa/Leaks.cs
[^ksa-modulestateful]: current/decomp/KSA/ModuleStateful.cs (decompiled, build 2026.10.10.5554)
