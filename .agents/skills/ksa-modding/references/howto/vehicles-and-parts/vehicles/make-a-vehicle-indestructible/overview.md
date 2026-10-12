---
type: Game Feature
title: Making a vehicle survive crashes and g-loads, and damaging parts against crash tolerance
description: KSA destroys vehicles through two paths, a per-part crash-tolerance comparison and a whole-craft structural check, and four recipes to veto, neutralise, filter or imitate them
category: vehicles-and-parts
group: vehicles
topic: make-a-vehicle-indestructible
demonstrated_by:
  - mods/ksacars/features/crash-veto
  - mods/unscience/features/the-tick-vessel-indestructibility
  - mods/unscience/features/kitchen-sink-g-load-protection
  - mods/ksarmory/features/blast-damage-and-part-health
lifecycle:
  - physics:vehicle-solver
  - frame:apply-solvers
  - event:vehicle-destroyed
  - frame:after-on-frame
  - physics:bepu
  - event:save
  - event:load
  - frame:draw-ui
game_types:
  - KSA.Part
  - KSA.PhysicsBubble
  - KSA.PartFailureEvent
  - KSA.PartFailure
  - KSA.FxDeformation
  - KSA.PartStructuralLimits
  - KSA.Universe
  - KSA.StructuralLoad
  - KSA.Vehicle
  - KSA.VehicleDestructionEvent
  - KSA.PartTemplate
tags: [crash, destruction, crash-tolerance, g-load, harmony, dents, part-failure]
---
A craft can die in KSA in two independent ways, decided on the physics worker and applied on the main thread
one frame later. Knowing which path killed it is the whole problem: vetoing one leaves the other open.

## Engine background

**Path 1: whole-craft structural failure.** At the end of each bubble step the worker runs
`PartFailure.Detect` and then `PhysicsBubble.DetectStructuralFailure(VehicleUpdateState)`
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1655-L1684)[^ksa-physicsbubble] [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1038). The detector writes the step's
`StructuralLoad` telemetry (peak g, size-dependent g limit from
`VehicleStructuralLimits.EffectiveMaxGLoad`, peak dynamic pressure vs 200 kPa), compares
`GLoadFraction >= 1.0` and `DynamicPressureFraction >= 1.0`, and stores a `VehicleDestructionEvent` with a
cause. The main thread applies it in `ApplyRenderEventsToVehicles`, and `VehicleDestructionEvent.Apply` calls
`Universe.DestroyVehicleFromEvent` [current/decomp/KSA/VehicleDestructionEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleDestructionEvent.cs#L1-L15)[^ksa-vehicledestructionevent] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971-L2016)[^ksa-universe].

**Path 2: part failure from contact pressure.** `PartFailure.Detect` takes each contacted part's accumulated
pressure and raises a `PartFailureEvent` where `PartStructuralLimits.HasFailed(peak,
part.CrashTolerancePascals)` is true [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L45-L75)[^ksa-partfailure] [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L192-L195)[^ksa-partstructurallimits].
The event is applied with `PartFailure.IsolateAndDestroy`, which ends in `Universe.DestroyVehicle`, never
`DestroyVehicleFromEvent` [current/decomp/KSA/PartFailureEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailureEvent.cs#L16-L100)[^ksa-partfailureevent].

**The tolerance.** `Part.CrashTolerancePascals` is
`PartStructuralLimits.ResolveCrashTolerance(Template.CrashTolerance, mass, volume)` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L858)[^ksa-part]. An
authored `CrashTolerance` XML attribute on `<Part>` (a double, default NaN) is used as given; NaN or <= 0
derives `9 MPa * clamp(density / 330, 0.1, 8)` clamped to 0.1 to 100 MPa [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L30-L44)
[current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L12-L20)[^ksa-parttemplate]. The same getter feeds dents: `FxDeformation.ReportContact` divides pressure by it
and ignores anything under `PressureThresholdFraction` (0.5) [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L318-L370)[^ksa-fxdeformation].

## Approaches

| Article | Mod | Technique | Scope |
|---|---|---|---|
| [1: XML `CrashTolerance` plus a `DestroyVehicleFromEvent` veto](make-a-vehicle-indestructible-1.md) | KSACars | `harmony-prefix` | One kind of craft, by template |
| [2: per-vessel registry, tolerance getter and detector pair](make-a-vehicle-indestructible-2.md) | unscience (The Tick) | `harmony-prefix` | Chosen vessels at runtime, no template mutation |
| [3: filter only the g-load read](make-a-vehicle-indestructible-3.md) | unscience (Kitchen Sink) | `harmony-transpiler` | g-load only, with persistence by vessel identity |
| [4: blast damage against engine-derived strength](make-a-vehicle-indestructible-4.md) | KSArmory | `weak-keyed-ledger` | The opposite problem: damage parts against crash tolerance |

## See also

- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md) (destruction from a frame hook), [write-worker-thread-safe-solver-patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)
- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) for the rules of worker-thread patches
- Mod features: [ksacars/crash-veto](../../../../mods/ksacars/features/crash-veto.md), [unscience/the-tick-vessel-indestructibility](../../../../mods/unscience/features/the-tick-vessel-indestructibility.md), [unscience/kitchen-sink-g-load-protection](../../../../mods/unscience/features/kitchen-sink-g-load-protection.md), [ksarmory/blast-damage-and-part-health](../../../../mods/ksarmory/features/blast-damage-and-part-health.md)

[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicledestructionevent]: current/decomp/KSA/VehicleDestructionEvent.cs (decompiled, build 2026.10.10.5554)
[^ksa-fxdeformation]: current/decomp/KSA/FxDeformation.cs (decompiled, build 2026.10.10.5554)
[^ksa-partfailureevent]: current/decomp/KSA/PartFailureEvent.cs (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-partfailure]: current/decomp/KSA/PartFailure.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-partstructurallimits]: current/decomp/KSA/PartStructuralLimits.cs (decompiled, build 2026.10.10.5554)
