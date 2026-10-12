---
type: Mod Feature
title: Launcher parts get lighter as their stores leave
description: Each frame the mod works out how much a launcher or dispenser part has shed in fired rounds, then rescales that part's inert mass through KSA's per-part InertMass slot and recomputes the craft's mass properties inside a Harmony prefix on Vehicle.PrepareWorker, before the physics worker copies them
mod: ksarmory
category: vehicles-and-parts
techniques: [harmony-prefix, prepareworker-window, unsafe-ref-write, sim-ksa-split]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.Part
  - KSA.InertMass
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.VehicleProperties
  - KSA.VehicleUpdateState
  - KSA.FlightComputer
  - KSA.OffsetMassProperties
tags: [mass, inertia, centre-of-mass, stores, magazine, physics-worker, double-buffer]
---

A Sidewinder rail that has fired its AIM-9 is 78 kg lighter; a MIRV bus that has released six
reentry vehicles has lost 1.5 t; a dispenser that has thrown its flares weighs less. KSArmory's
rounds are not KSA parts, so nothing in the engine knows they left. The mod closes that gap: each
launcher part's declared mass includes its full load, and as rounds go the mod scales that part's
mass and inertia down by the fraction that has gone, then makes the craft's mass properties and
flight computer pick it up.[^ksarmory-storemass][^ksarmory-launcherprofile]

The write is done in the one window where the physics worker will not overwrite it: a Harmony
prefix on `Vehicle.PrepareWorker`, immediately before the next physics step is snapshotted.

## How it works

**Desired mass, from what is aboard (pure C#).** `StoreLoad.ShedKg(perRoundKg, full, remaining)`
is `perRoundKg * (full - remaining)`; `StoreLoad.Factor(fullKg, shedKg)` is
`(full - shed) / full`, clamped to `[0.01, 1]`. Computed fresh every frame from the magazine, never
subtracted per shot, so a reload, a refused shot that hands its round back and a save reload are
all correct with nothing to undo.[^ksarmory-storeload]

**Per-round weights are profile data.** `LauncherProfile.StoreMassKg` (tube rounds) and
`GunRoundMassKg` (belt rounds); a dispenser uses its decoy's `MassKg`. Weapon packs set the same
keys (`StoreMassKg` defaults to 0, which sheds nothing).[^ksarmory-weapon-packs]

**Queue in the frame step** (`StoreMass.Want`, every frame for every launcher and dispenser):

1. Find the part's own `InertMass` module (`Module<InertMass>.TryGetFrom(part.Modules, ...)`,
   matched on `m.Parent == part`).
2. Remember the full-load `OffsetMassProperties` per part in a `ConditionalWeakTable`. If the
   current mass is not what the mod last wrote, the engine has restored the template's (a load or
   a rescale), so the current value becomes "full" again.
3. If the wanted mass differs from the written one by more than 0.1% of the part (min 1 g), add
   the part to a per-craft "owed" list. Belt-fed guns therefore cause a write every few dozen rounds,
   not every frame.

**Apply in the worker window** (`StoreMass.Apply(craft)`, from the `PrepareWorker` prefix):

1. For each owed part still on this craft's tree: scale the full properties by the factor
   (`MassProperties.Scale`, so mass and inertia scale together and the offset stays where the part
   declares it) and call `Part.SetInertMassPropertiesAsmb`.[^ksa-part]
2. `PartTree.RefreshStaticMass()` re-sums every `InertMass` in the tree at its part's position and
   rotation (PartTree.cs L1019-L1037).[^ksa-parttree]
3. Write the craft's `VehicleProperties.InertMassPropsAsmb` from `tree.ComputeInertMassPropertiesAsmb()`
   and call `RecomputeMassProperties(stores, moles)`, which rebuilds total mass, inverse mass and the
   bounding sphere (VehicleProperties.cs L99-L107). `Vehicle.Props` is `ref readonly`, so the mod
   writes through `Unsafe.AsRef(in craft.Props)`.[^ksa-vehicleproperties][^ksa-vehicle]
4. `tree.PerformanceSequences.SetDirty()` and `craft.FlightComputer.ReadUpdatedVehicleConfiguration(craft)`,
   so delta-v readouts and the autopilot see the new mass.[^ksa-flightcomputer]

Scaling in place rather than moving mass to the tube is deliberate: the MIRV bus's mass sits on its
thruster ring, and taking it off at a tube would put every attitude correction off-axis.

## Lifecycle and timing

| Step | Hook | Where in the frame (build 5554) |
|---|---|---|
| `WantStoreMass` (queue) | `StepSimulation` after the fire loop, inside the simulated-step gate (KSArmoryMod.cs L779) | `[StarMapAfterGui]` (Program.cs L2268) or the `OnFrameCelestials` prefix (L2320): frame N, *after* frame N's physics step was already dispatched[^ksarmory-mod] |
| `StoreMass.Apply` (write) | `AttitudeHook.BeforePrepareWorker`, Harmony prefix on `Vehicle.PrepareWorker(SimStep)` (AttitudeHook.cs L341-L360) | Frame N+1: `PrepareFrame` -> `ExecuteNextVehicleSolvers` (Program.cs L2220) -> `PrepareVehicleWorkers` -> `PrepareWorker` per vehicle (Universe.cs L2148), before the job is queued (L2133)[^ksarmory-attitudehook][^ksa-universe][^ksa-program] |
| Worker reads it | The vehicle step copies `ReadOnlyVehicle.Props` into its result on first write (`GetNewProps`, VehicleUpdateState.cs L183-L193) | Physics worker, frame N+1 |
| Main thread receives it | `UpdateFromTaskResultsUnsynchronized` replaces `_props` with the worker's copy (Vehicle.cs L2445) | `ApplyVehicleSolvers` (Program.cs L2167), frame N+2[^ksa-vehicleupdatestate] |

Why the prefix: `Vehicle._props` is double-buffered with the worker. A main-thread write from a
StarMap hook lands while the worker for that frame is already running from the old properties,
and the next `ApplyVehicleSolvers` copies the worker's result over it. A write made in the
`PrepareWorker` prefix is in place before the worker starts, so the worker's own copy carries it
forward. The same window is used for attitude commands and tank leaks
([PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)).

The queue is cleared when a save is loaded (`StoreMass.Clear()` in `ForgetTheWorld`, KSArmoryMod.cs
L380), and `Apply` skips parts whose `Tree` is no longer the craft's, so a split or dock between
queue and apply does not write onto the wrong craft.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/StoreMass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/StoreMass.cs) | Full-load memory, owed queue, apply and craft recompute |
| [src/KSArmory/Sim/StoreLoad.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StoreLoad.cs) | Shed mass and scale factor |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1228-L1250) | `WantStoreMass` for launchers, belts and dispensers |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L339-L360) | The `PrepareWorker` prefix that calls `StoreMass.Apply` |
| [src/KSArmory/Ksa/Countermeasures.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Countermeasures.cs#L288-L297) | Dispensers shed decoy mass the same way |
| [tests/KSArmory.Tests/StoreLoadTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/StoreLoadTests.cs) | Shed and factor tested without the game |

## Snippets

```csharp
// src/KSArmory/Ksa/StoreMass.cs L68-L99
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
            // ...
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

```csharp
// src/KSArmory/Sim/StoreLoad.cs L12-L30
    public static double ShedKg(double perRoundKg, int full, int remaining)
    {
        if (!(perRoundKg > 0.0) || full <= 0) return 0.0;

        return perRoundKg * Math.Clamp(full - remaining, 0, full);
    }

    // ...
    public static double Factor(double fullKg, double shedKg)
    {
        if (!(fullKg > 0.0) || !(shedKg > 0.0)) return 1.0;

        return Math.Clamp((fullKg - shedKg) / fullKg, 0.01, 1.0);
    }
```

```csharp
// current/decomp/KSA/VehicleUpdateState.cs L183-L193
	public ref VehicleProperties GetNewProps()
	{
		VehicleUpdateData updateData = UpdateData;
		VehicleProperties valueOrDefault = updateData.NewVehicleProperties.GetValueOrDefault();
		if (!updateData.NewVehicleProperties.HasValue)
		{
			valueOrDefault = ReadOnlyVehicle.Props;
			updateData.NewVehicleProperties = valueOrDefault;
		}
		return ref NullableExtensions.DangerousGetValueOrNullReference(ref UpdateData.NewVehicleProperties);
	}
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Part.SetInertMassPropertiesAsmb(in OffsetMassProperties)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1298-L1313) | The per-part mass slot (sets the part's `InertMass.MassPropertiesAsmb`) |
| `InertMass.MassPropertiesAsmb`, `Module<InertMass>.TryGetFrom` | [current/decomp/KSA/InertMass.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InertMass.cs#L7-L14) | Read the current and full mass |
| `PartTree.RefreshStaticMass`, `ComputeInertMassPropertiesAsmb`, `PerformanceSequences` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1019-L1037) | Re-sum the tree; mark performance stale |
| `Vehicle.Props` (`ref readonly`) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L541) | Written through `Unsafe.AsRef` |
| `VehicleProperties.InertMassPropsAsmb`, `RecomputeMassProperties` | [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L99-L107) | Total mass, inverse mass, bounding sphere |
| `FlightComputer.ReadUpdatedVehicleConfiguration` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L311) | Flight computer re-reads the configuration |
| `Vehicle.PrepareWorker(SimStep)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Patched (prefix) for the write window |
| `Vehicle.UpdateFromTaskResultsUnsynchronized` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2445) | Engine side: where an out-of-window write would be overwritten |

## Pitfalls and version notes

- **Write in the worker window, not the GUI hook.** `_props` is replaced wholesale from the
  worker's result each frame (Vehicle.cs L2445), and the worker copies `ReadOnlyVehicle.Props`
  lazily while it runs; a write from `frame:draw-ui` races that copy and is lost on apply.[^ksa-vehicle][^ksa-vehicleupdatestate]
- **A part's new mass alone reaches nothing that flies.** Without `RefreshStaticMass` and the
  `VehicleProperties` recompute, the craft keeps flying its old mass; the mod's comment says the
  engine propagates part mass to the craft only on a structural change.[^ksarmory-storemass]
- **The engine can put the template's mass back.** `InertMass.SetScale` recomputes from the
  template's mass elements and `IInertMass` modules (InertMass.cs L11-L14), reached through
  `Part.RecomputeInertMass` (Part.cs L1315-L1324). `StoreMass.Want` detects this by comparing with
  what it last wrote and re-adopts the current value as "full".[^ksa-inertmass][^ksa-part]
- **Doc vs build 5554.** `StoreMass.cs` (L12-L13) calls `SetInertMassPropertiesAsmb` "what a parachute
  does when it loses its canopy". In 5554 the parachute changes mass through `Part.RecomputeInertMass`
  plus `PartTree.RefreshStaticMass` (Parachute.cs L529-L535); the engine's own callers of
  `SetInertMassPropertiesAsmb` are `InertMass.CreateComponents` (InertMass.cs L81) and
  `Vehicle.SpawnSubPartDebris` (Vehicle.cs L1710). Same slot, different entry point.[^ksa-parachute]
- **Not saved.** The written mass is not serialized; a save reload rebuilds parts from the template
  (full) and refills the magazine, so the two agree.
- **Floor at 1%.** A part declared lighter than its stores keeps 1% of its mass rather than none.
- **ICBM accuracy switch.** A MIRV bus sheds warhead mass only when its computer's
  `ShedWarheadMass` is on (KSArmoryMod.cs L1239-L1241).[^ksarmory-mod]

## See also

- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md): the hook and why the window exists
- [Holed tank leaks](./holed-tank-leaks.md): the other state written in the same prefix
- [Weapon registry and profiles](./weapon-registry-and-profiles.md): `StoreMassKg` and `GunRoundMassKg`
- [Countermeasure dispensers](./countermeasure-dispensers.md): dispensers shed decoy mass
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md): the bus whose mass sits on its thruster ring
- [Write flight-computer and kinematic fields in a Vehicle.PrepareWorker prefix](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-5.md) - how-to article demonstrating this feature
- [Change a part's mass at runtime in the PrepareWorker window](../../../howto/vehicles-and-parts/vehicles/change-vehicles-without-racing-the-solver/change-vehicles-without-racing-the-solver-7.md) - how-to article demonstrating this feature

[^ksarmory-storemass]: src/KSArmory/Ksa/StoreMass.cs
[^ksarmory-storeload]: src/KSArmory/Sim/StoreLoad.cs
[^ksarmory-attitudehook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-launcherprofile]: src/KSArmory/Sim/LauncherProfile.cs
[^ksarmory-weapon-packs]: docs/WEAPON-PACKS.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-inertmass]: KSA.InertMass (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: KSA.Parachute (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
