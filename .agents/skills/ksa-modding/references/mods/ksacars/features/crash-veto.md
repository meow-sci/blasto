---
type: Mod Feature
title: Veto structural destruction for one kind of craft
description: Spare a car from KSA's crash destruction with a Harmony prefix on Universe.DestroyVehicleFromEvent that returns false for it, and close the separate part-failure path by setting CrashTolerance far past any contact pressure on the car's Part
mod: ksacars
category: vehicles-and-parts
techniques: [harmony-prefix, xml-asset]
hooks: [starmap StarMapAllModsLoaded, harmony-prefix KSA.Universe.DestroyVehicleFromEvent]
lifecycle:
  - startup:asset-load
  - startup:all-mods-loaded
  - frame:apply-solvers
  - frame:input
  - physics:vehicle-solver
  - event:vehicle-destroyed
game_types:
  - KSA.Universe
  - KSA.VehicleDestructionEvent
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateTask
  - KSA.PartFailure
  - KSA.PartFailureEvent
  - KSA.Part
  - KSA.PartTemplate
  - KSA.PartStructuralLimits
  - KSA.InputEvents
tags: [vehicles, destruction, crash, harmony, prefix, part-xml, crash-tolerance, structural-failure]
---

A KSACars car can be dropped off a cliff, driven into the sea or rammed into a rock and come out
driveable. KSA breaks a craft up past a g-load set by its size, which a car falling a few metres
onto its bump stops exceeds, and past a dynamic pressure a car reaches driving into the sea. The
mod closes both of the engine's destruction paths for its cars: a Harmony prefix on
`Universe.DestroyVehicleFromEvent` vetoes whole-craft destruction, and a huge `CrashTolerance` on
each car's `<Part>` keeps the separate per-part failure from ever tripping.[^ksacars-crashhook][^ksacars-notes]

## How it works

**Path 1: whole-craft destruction.** On the physics worker, `PhysicsBubble.DetectStructuralFailure`
compares the step's peak g-load with `VehicleStructuralLimits.EffectiveMaxGLoad(bounding radius)`
and the peak dynamic pressure with 200 kPa, and on failure stores a `VehicleDestructionEvent` with
a cause: `GroundImpact`, `OceanImpact`, `HydrodynamicForces`, `Collision`, `ExcessiveGForce` or
`AerodynamicForces`.[^ksa-physicsbubble] Back on the main thread
`PhysicsBubble.ApplyRenderEventsToVehicles` applies pending destruction events, and
`VehicleDestructionEvent.Apply` calls `Universe.DestroyVehicleFromEvent`, which spawns the
explosion, sheds debris, retargets cameras and finally calls `DestroyVehicle(vehicle, Kill)`.[^ksa-vehicledestructionevent][^ksa-universe]
The console's `explode` command and the explosion editor queue an `InputEvents.VehicleExplodeData`,
which is applied with the frame's input events and goes through the same method.[^ksa-inputevents]

`CrashHook` patches that one public static method with a prefix
`BeforeDestroy(Vehicle vehicle)` that returns `Buggies.Of(vehicle) is null`: false (skip the
original) for a car, true for everything else. On an exception it returns true, so a fault never
makes other craft indestructible.[^ksacars-crashhook]

**Path 2: part failure.** In the same worker step `PartFailure.Detect` takes the contact pressure
accumulated on each part and raises a `PartFailureEvent` for every part where
`PartStructuralLimits.HasFailed(peakPressure, part.CrashTolerancePascals)`.[^ksa-partfailure]
`PartFailureEvent.Apply` spawns an explosion and calls `PartFailure.IsolateAndDestroy`, which splits
the failed part off and calls `Universe.DestroyVehicle` on the piece holding it, never
`DestroyVehicleFromEvent`.[^ksa-partfailureevent] A car is one part, so that piece is the car and
the prefix above does not see it.[^ksacars-notes]

The tolerance is `Part.CrashTolerancePascals`, which resolves the `<Part CrashTolerance>`
attribute (`PartTemplate.CrashTolerance`, an `[XmlAttribute]` double defaulting to `NaN`) and, when
it is missing or not positive, derives one from mass over collider volume, clamped to
0.1..100 MPa.[^ksa-parttemplate][^ksa-part][^ksa-structurallimits] An authored value is used as
given, so the mod sets `CrashTolerance="1e12"` on both cars' `<Part>`.[^ksacars-assets]

| Path | Raised by | Applied by | Covered by |
|---|---|---|---|
| g-load, dynamic pressure, ground, sea, collision | `PhysicsBubble.DetectStructuralFailure` | `ApplyRenderEventsToVehicles` -> `DestroyVehicleFromEvent` | prefix veto |
| console `explode`, explosion editor | `InputEvents.VehicleExplodeData` | `DestroyVehicleFromEvent` | prefix veto |
| contact pressure on a part | `PartFailure.Detect` | `PartFailureEvent.Apply` -> `IsolateAndDestroy` -> `DestroyVehicle` | `CrashTolerance="1e12"` |

## Lifecycle and timing

| Moment | Where | Thread |
|---|---|---|
| `CrashTolerance` read | `<Part>` XML deserialised into `PartTemplate` (`startup:asset-load`) | main |
| Prefix installed | `[StarMapAllModsLoaded]` -> `CrashHook.Install` | main |
| Failures detected | `PhysicsBubble.FullPhysicsEndFrame` -> `PartFailure.Detect`, `DetectStructuralFailure` (L1681-L1682) | vehicle-solver worker |
| **Prefix runs** | `Program.PrepareFrame` -> `Universe.ApplyVehicleSolvers` -> `VehicleUpdateTask.ApplyResultsToMainThread` -> `PhysicsBubble.ApplyRenderEventsToVehicles` -> `VehicleDestructionEvent.Apply` -> `DestroyVehicleFromEvent` | main |

`ApplyResultsToMainThread` runs the per-bubble apply jobs in a parallel batch, then on the main
thread calls `ApplySynchronizedResultsToVehicles` and finally `ApplyRenderEventsToVehicles` for
every bubble.[^ksa-vehicleupdatetask] That is inside `Universe.ApplyVehicleSolvers`, which
`Program.PrepareFrame` calls at L2167, before input and before the next step is
dispatched.[^ksa-universe][^ksa-program] The prefix therefore sees fully applied vehicle state, and
reads only the mod's own car dictionary (written on the same thread from the GUI-pass hook).
After `Apply`, KSA clears the stored event; if the car is still over its limits next step a new
one is raised, and the prefix refuses it again.[^ksa-physicsbubble]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/CrashHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CrashHook.cs) | Install, the veto prefix, signature pin |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L162-L167) | `CrashTolerance="1e12"` on the buggy's `<Part>` (the Eldorado's at L422) |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L154-L155) | `Buggies.Of`: is this vehicle a car |

## Snippets

```csharp
// src/KSACars/Ksa/CrashHook.cs L31-L45
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent),
                                                    [typeof(Vehicle), typeof(VehicleDestructionEvent)]);
            if (target is null)
            {
                Log.Warn("KSA has no Universe.DestroyVehicleFromEvent to hook; a car breaks up in a hard landing");
                return;
            }

            MethodInfo prefix = typeof(CrashHook).GetMethod(nameof(BeforeDestroy),
                                                            BindingFlags.NonPublic | BindingFlags.Static)!;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Installed = true;
```

```csharp
// src/KSACars/Ksa/CrashHook.cs L68-L84
    // False spares the craft. Inside the engine's own loop: nothing here may throw.
    private static bool BeforeDestroy(Vehicle vehicle)
    {
        try
        {
            return Buggies.Of(vehicle) is null;
        }
        catch (Exception e)
        {
            if (!_complained)
            {
                _complained = true;
                Log.Error("sparing a car from a crash failed", e);
            }
            return true;
        }
    }
```

```xml
<!-- src/KSACars/KSACarsAssets.xml L162-L167 -->
  <!--
    CrashTolerance is the contact pressure, in pascals, past which KSA breaks a part. A car is one
    part, so its breaking is the car's destruction, and a wheel pad clipping a rock at speed reaches
    what KSA derives from its mass. Set far past anything a contact gives.
  -->
  <Part Id="KSACars_Prefab_Buggy" CrashTolerance="1e12">
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Universe.DestroyVehicleFromEvent(Vehicle, VehicleDestructionEvent)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971-L2016) | Patch target: explosion, debris, cameras, `DestroyVehicle` |
| `Universe.ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | Main-thread apply that leads to the prefix |
| `VehicleUpdateTask.ApplyResultsToMainThread` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L258) | Calls `ApplyRenderEventsToVehicles` per bubble |
| `PhysicsBubble.ApplyRenderEventsToVehicles` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L919-L970) | Applies docking, impact, splash, part-failure and then destruction events |
| `PhysicsBubble.DetectStructuralFailure` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1039) | Raises `VehicleDestructionEvent` with a cause |
| `VehicleDestructionEvent.Apply` | [current/decomp/KSA/VehicleDestructionEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleDestructionEvent.cs) | Forwards to `DestroyVehicleFromEvent` |
| `InputEvents.VehicleExplodeData.Apply` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L690-L704) | Second caller of `DestroyVehicleFromEvent`, applied in `ApplyInputEvents` (L1137) |
| `PartFailure.Detect`, `IsolateAndDestroy` | [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L45-L75) | Contact-pressure failure; `IsolateAndDestroy` at L441-L502 ends in `DestroyVehicle` |
| `PartFailureEvent.Apply` | [current/decomp/KSA/PartFailureEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailureEvent.cs#L16-L94) | Explosion, debris, isolate and destroy |
| `PartTemplate.CrashTolerance` | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L17-L18) | `[XmlAttribute] double`, default `NaN` |
| `Part.CrashTolerancePascals` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L858) | Resolves authored or derived tolerance |
| `PartStructuralLimits.ResolveCrashTolerance`, `DeriveCrashTolerance`, `HasFailed` | [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L30-L44) | Derivation clamps to 0.1..100 MPa; `HasFailed` at L192-L195 |

## Pitfalls and version notes

- **One call does not cover both paths.** The author saw an Eldorado clipping a rock with a wheel
  pad at 46 m/s destroyed by part failure with the veto in place and nothing in either log, which
  is what led to the `CrashTolerance` attribute.[^ksacars-notes]
- **A one-part craft dies whole by part failure.** `PartFailure.TrippedTheFragmentGuard` needs at
  least two failed parts to flag `DestroyWholeVehicle`, but `IsolateAndDestroy` of the only part has
  nothing to split off and destroys the craft anyway.[^ksa-partfailure]
- **The event repeats.** The veto is per event, and a car resting past its g-limit raises one every
  step; the prefix is a dictionary lookup, so this is cheap.[^ksacars-notes]
- **Explosions are inside the vetoed method**, so a spared car makes no explosion either. A part
  failure still spawns one if the tolerance is ever reached.[^ksa-universe][^ksa-partfailureevent]
- **Public and pinned.** `PinTheSignature` references the target so a signature change is a build
  error; if the patch fails, a car breaks up like any craft.[^ksacars-crashhook]
- **Prefix return semantics.** Returning false skips the original method for that call, so
  another mod that patches `DestroyVehicleFromEvent` and expects a car to be destroyed will see the
  original skipped for cars.

## See also

- [Car part game data](./car-part-gamedata.md)
- [Scoop colliders and loose clutter](./scoop-colliders-and-loose-clutter.md) (clipping rocks)
- [API surface pinning and diff](./api-surface-pinning-and-diff.md)
- [KSACars overview](../overview.md)
- [Make one kind of craft indestructible with CrashTolerance and a destruction veto](../../../howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-1.md) - how-to article demonstrating this feature

[^ksacars-crashhook]: src/KSACars/Ksa/CrashHook.cs
[^ksacars-assets]: src/KSACars/KSACarsAssets.xml
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md, "The engine destroys a craft in two ways, and one call covers only the first"
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled)
[^ksa-vehicledestructionevent]: KSA.VehicleDestructionEvent (decompiled)
[^ksa-inputevents]: KSA.InputEvents (decompiled)
[^ksa-partfailure]: KSA.PartFailure (decompiled)
[^ksa-partfailureevent]: KSA.PartFailureEvent (decompiled)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled)
[^ksa-part]: KSA.Part (decompiled)
[^ksa-structurallimits]: KSA.PartStructuralLimits (decompiled)
[^ksa-program]: KSA.Program (decompiled)
