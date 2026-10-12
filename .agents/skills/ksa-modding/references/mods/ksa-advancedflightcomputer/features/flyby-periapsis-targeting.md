---
type: Mod Feature
title: Aim a transfer at a flyby periapsis instead of the body center
description: Retarget a stock porkchop transfer by solving Lambert to the target's center offset by the impact parameter that yields the requested periapsis, on a chosen side perpendicular to the approach, and hand the result to the stock Create button
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-transpiler, custom-physics]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.OrbitalTransfers
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.TransferPlanner
  - KSA.Burn
  - KSA.CorrectionBurnTask
tags: [flyby, lambert, b-plane, impact-parameter, transfer-planner, hohmann]
---

The stock planner aims every transfer at the target's center, so a well-timed Hohmann transfer
arrives as an impact and the flyby needs a later correction burn. With **Target flyby periapsis**
ticked in the Transfer Planning window, AFC re-solves the departure so it arrives at a periapsis the
player chooses, on the side the player chooses, and **Create** fires that burn directly. The section
reports approach speed, impact parameter, the departure delta-v next to the impact-aimed one, and the
periapsis the propagated trajectory actually reaches.

## How it works

### Impact parameter from the requested periapsis

For a hyperbolic approach at speed `v_soi` relative to the target at the SOI radius `r_soi`, the
periapsis `r_p` and impact parameter `b` are related by
`v_p^2 = v_soi^2 - 2 mu / r_soi + 2 mu / r_p` and `b = r_p v_p / v_soi`. That is exactly stock's
`OrbitalTransfers.ImpactParameterAtSoiEntry` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L691-L696)), which stock's
own course correction (`CorrectionBurnTask`) uses with the target's `mu`.[^ksa-orbitaltransfers]
AFC calls it for the forward direction and adds a numerically stable inverse for the readout
([AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs#L101-L135)). Two `mu` values are in
play: the Lambert solve uses the shared parent (Earth for LEO to Luna, the Sun for LEO to Mars), the
impact parameter uses the target's own `mu`.

### Solving the offset transfer

`SolveOffsetTransfer` iterates three times ([AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs#L266-L334)):

1. Seed with `OrbitalTransfers.SuperiorLambert(muParent, departurePos, targetCenterAtArrival, transit)`.
2. Build the transfer orbit with `Orbit.CreateFromStateCci`, find the closest approach to the target
   (48 coarse samples, then `MathEx.BrentMin`, skipping the first second so it never latches onto the
   departure point).
3. Take the relative velocity at closest approach (it sets the offset plane) and at the SOI crossing
   (it sets the energy), compute `b`, resolve the side offset direction, aim at
   `targetPosition + b * offsetDir` and solve Lambert again with the closest-approach time as the new
   transit.

### Sides in the target's own frame

`FlybySide` is Inner/Outer (along the target's radius from its parent) or North/South (along its orbit
normal). The offset must be perpendicular to the approach, so the wanted axis is projected into that
plane; the surviving length is the axis "alignment", and below about 0.15 (roughly 9 degrees) the
side is refused and greyed out in the UI because the direction would be noise
([AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs#L336-L364)). For a Hohmann arrival the
relative velocity runs along the target's track, which is why there is no leading/trailing option.

### Periapsis reference and floor

The entry is converted to a center radius by reference: Surface (above mean radius), Center, or
Atmosphere (above `IParentBody.GetAtmosphereRadius()`, offered only when that is positive). The floor
is `GetNearSurfaceRadius()` (terrain ceiling, or the top of the atmosphere for an atmospheric body);
below it Create is blocked.

### Departure burn

- Same parent (LEO to Luna): the Lambert departure velocity minus the parking velocity is the
  injection delta-v at the transfer start.
- Cross parent (LEO to Mars): the result is a hyperbolic excess in the Lambert parent's frame; AFC
  rotates it into the parking parent's frame and calls `OrbitalTransfers.SingleImpulseHyperbolicEscape`,
  shifting the burn time by the escape's true anomaly, as stock's `FinalizeLambert` does.

### Caching and Create

The solve is cached by a `DepartureKey` of everything it depends on (source, target, start, transit,
radius, side, `mu`, SOI, both orbit geometries, frame rotations, and the parking state bucketed to 1 km
and 0.1 m/s). While the departure burn is thrusting, parking drift does not invalidate the cache, so
the selected departure stays fixed mid-burn ([AdvancedFlightComputer/Features/Flyby/FlybyTargeting.DepartureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.DepartureCache.cs)).
The stock CREATE call site is swapped by the [plan window transpiler](plan-window-transpiler-injection.md):
`HohmannCreateInterceptor` returns a `Burn.Create` of the flyby departure (or the multi-pass first
pass), and falls back to stock's impact-aimed burn with an alert when the retarget fails
([AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs)).

## Lifecycle and timing

Everything runs on the main thread inside `TransferPlanner.DrawPlanWindow` (`frame:draw-ui`): the
section is drawn by `HohmannMultiPassUI.DrawInline`, which the transpiler injected, and the Lambert
iterations run there on demand; the cache keeps the per-frame cost to one key comparison. The stock
porkchop result it starts from was computed earlier by stock's `ThreadPool` task; AFC only reads the
selected entry once stock reports `_transferCalculated`.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs) | Impact parameter, offset Lambert iteration, sides, departure burns |
| [AdvancedFlightComputer/Features/Flyby/FlybyTargeting.DepartureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.DepartureCache.cs) | Departure cache key and freeze during thrust |
| [AdvancedFlightComputer/Features/Flyby/FlybySide.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybySide.cs) | Side definitions |
| [AdvancedFlightComputer/Features/Flyby/FlybyReference.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyReference.cs) | Periapsis references |
| [AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs) | Section UI and readouts |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | Create interception |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyTargetingTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyTargetingTest.cs) | Headless test of the retarget |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs L297-L321
            // The relative velocity at closest approach sets the offset plane.
            // A little earlier, at the SOI boundary, it sets the flyby energy.
            double3 vRelCa = transfer.GetStateVectorsAt(caTime).VelocityCci
                             - targetOrbit.GetStateVectorsAt(caTime).VelocityCci;
            double vRelCaLen = vRelCa.Length();
            if (!(vRelCaLen > 0.0)) return null;

            UniverseTime soiTime = caTime - soiTarget / vRelCaLen;
            double vSoi = (transfer.GetStateVectorsAt(soiTime).VelocityCci
                           - targetOrbit.GetStateVectorsAt(soiTime).VelocityCci).Length();
            double b = ImpactParameterForPeriapsis(vSoi, rpRadius, muTarget, soiTarget);
            if (!(b > 0.0)) return null;

            StateVectors targetAtCa = targetOrbit.GetStateVectorsAt(caTime);
            if (!TryResolveSideOffset(targetAtCa, vRelCa / vRelCaLen, side,
                    out double3 offsetDir, out radialAlign, out normalAlign))
                return null;

            double3 aim = targetAtCa.PositionCci + b * offsetDir;

            UniverseTime newTransit = caTime - start;
            if (!(newTransit > 0.0)) return null;

            OrbitalTransfers.SuperiorLambert(
                muParent, departurePos, aim, newTransit, out vDeparture, out _);
```

## Game APIs touched

| Member | Use |
|---|---|
| `OrbitalTransfers.SuperiorLambert` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L765)) | Lambert solver |
| `OrbitalTransfers.ImpactParameterAtSoiEntry` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L691)) | b from periapsis at the SOI |
| `OrbitalTransfers.SingleImpulseHyperbolicEscape` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L992)) | Cross-parent ejection burn |
| `Orbit.CreateFromStateCci` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611)) | Transfer orbit for the closest-approach search |
| `IParentBody.GetNearSurfaceRadius`, `GetAtmosphereRadius` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L55-L61)) | Floor and Atmosphere reference |
| `MathEx.BrentMin` ([current/decomp/KSA/MathEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MathEx.cs)) | Closest-approach refinement |
| `CorrectionBurnTask` ([current/decomp/KSA/CorrectionBurnTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CorrectionBurnTask.cs#L94)) | Stock use of the same relation |

## Pitfalls and version notes

- The plan is impulsive and the burn is not: on a near-escape departure the flown periapsis drifts by
  roughly the finite-burn loss (about 1 % of a multi-km/s injection). Split the departure into passes
  or trim with a correction.
- Use the speed at the SOI boundary, not the asymptotic excess, or the achieved patched-conic periapsis
  will not match the game's model.
- Stock rewrites the transfer source to the vehicle for transfers inside one SOI, so detect
  cross-parent transfers by comparing parent ids, not with `OrbitalTransfers.SameSoiTransfer`.

## See also

- [Flyby encounter prediction](flyby-encounter-prediction.md)
- [Multi-pass Hohmann schedule](multi-pass-hohmann-schedule.md)
- [Plan window transpiler injection](plan-window-transpiler-injection.md)
- [Planner preview trajectories](planner-preview-trajectories.md)
- [Hand a retargeted transfer to the stock Create path](../../../howto/flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/add-plan-type-to-the-transfer-planner-4.md) - how-to article demonstrating this feature
- [Offset the Lambert aim point by an impact parameter and hand the result to the planner's Create path](../../../howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-1.md) - how-to article demonstrating this feature

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
