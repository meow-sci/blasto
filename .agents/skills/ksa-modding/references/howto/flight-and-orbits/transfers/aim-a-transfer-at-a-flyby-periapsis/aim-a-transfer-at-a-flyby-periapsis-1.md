---
type: How-To
title: Offset the Lambert aim point by an impact parameter and hand the result to the planner's Create path
description: Solve a transfer to a chosen periapsis by offsetting the target by the impact parameter for that periapsis, iterating Lambert three times, and creating the retargeted burn through the stock Create button
category: flight-and-orbits
group: transfers
topic: aim-a-transfer-at-a-flyby-periapsis
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting
  - mods/ksa-advancedflightcomputer/features/deorbit-landing-planner
techniques: [harmony-transpiler, lambert]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui]
game_types: [KSA.OrbitalTransfers, KSA.Orbit, KSA.IParentBody, KSA.TransferPlanner, KSA.Burn]
tags: [flyby, impact-parameter, lambert, periapsis, transfer-planner, side, create]
---

Outcome: with a "Target flyby periapsis" option ticked in the Transfer Planning window, the departure is re-solved so the transfer arrives at a periapsis the player chooses, on the side they choose, and Create fires that burn. KSA-AdvancedFlightComputer implements it.[Aim a transfer at a flyby periapsis instead of the body center](../../../../mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting.md)
The same idea (offset the target, or search a time-sliced set of approach points) is used for its deorbit planner.[Plan a deorbit to a landing site and let stock Auto fly the node](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md) Prerequisites: StarMap and Harmony (the planner UI is patched; see [Add plan types to the stock Transfer Planner](../add-plan-type-to-the-transfer-planner/overview.md)).

## Background: how the game does it

See the [overview](overview.md) for the impact parameter and Lambert members. The stock planner builds its transfer with `OrbitalTransfers.SolveLambert` (a ThreadPool task fills the porkchop) and aims at the target's centre; AFC reads the selected entry once stock reports `_transferCalculated` and re-solves the departure on its own.
For a cross-parent transfer (LEO to Mars) the Lambert result is a hyperbolic excess in the Lambert parent's frame, which AFC rotates into the parking parent's frame and passes to `OrbitalTransfers.SingleImpulseHyperbolicEscape`, shifting the burn time by the escape's true anomaly, as stock's `FinalizeLambert` does.[^ksa-transferplanner] [^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L992-L1030)

## Lifecycle and timing

Everything runs on the main thread inside `TransferPlanner.DrawPlanWindow` (`frame:draw-ui`): the section is drawn by code the transpiler injected and the Lambert iterations run there on demand, with the cache keeping the per-frame cost to one key comparison. The stock porkchop result it starts from was computed by stock's ThreadPool task. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Impact parameter from the requested periapsis** (`ImpactParameterForPeriapsis(vSoi, rpRadius, muTarget, soiTarget)` in the mod wraps the stock formula with a numerically stable inverse for the readout).
2. **Solve the offset transfer in three iterations:** seed with `OrbitalTransfers.SuperiorLambert(muParent, departurePos, arrivalCenter, transit, out vDeparture, out _)` (`arrivalCenter` is the target's CCI position at `start + transit`); build the transfer orbit with `Orbit.CreateFromStateCci` and find the closest approach to the target (48 coarse samples, then `MathEx.BrentMin`, skipping the first second so it never latches onto the departure point);
   take the relative velocity at closest approach (it sets the offset plane) and at the SOI crossing (it sets the energy), compute `b`, resolve the side offset direction, aim at `targetPosition + b * offsetDir` and solve Lambert again with the closest-approach time as the new transit:

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

3. **Define sides in the target's own frame.** Inner/Outer (along the target's radius from its parent) or North/South (along its orbit normal). The offset must be perpendicular to the approach, so project the wanted axis into that plane; below an alignment of about 0.15 (roughly 9 degrees) refuse the side because the direction would be noise. A Hohmann arrival has no leading/trailing option.
4. **Convert the periapsis reference** (Surface, Center, or Atmosphere above `IParentBody.GetAtmosphereRadius()`) to a centre radius; the floor is `GetNearSurfaceRadius()`; below it block Create.
5. **Build the departure burn:** same parent, the Lambert departure velocity minus the parking velocity; cross parent, the escape impulse above.
6. **Cache the solve** by a key of everything it depends on (source, target, start, transit, radius, side, `mu`, SOI, both orbit geometries, frame rotations, and the parking state bucketed to 1 km and 0.1 m/s) and freeze it while the burn thrusts.
7. **Hand it to Create.** The stock CREATE call site is swapped by the plan window transpiler: the interceptor returns a `Burn.Create` of the flyby departure (or the multi-pass first pass) and falls back to stock's impact-aimed burn with an alert when the retarget fails.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.cs) | impact parameter, offset Lambert iteration, sides, departure burns |
| [AdvancedFlightComputer/Features/Flyby/FlybyTargeting.DepartureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyTargeting.DepartureCache.cs) | departure cache key and freeze during thrust |
| [AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs) | section UI and readouts |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | Create interception |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyTargetingTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyTargetingTest.cs) | headless test of the retarget |

## Variations and gotchas

- **Two `mu` values:** the Lambert solve uses the shared parent's; the impact parameter uses the target's.
- **The offset plane comes from the closest-approach relative velocity,** not from the target's orbit plane; a near-parallel approach leaves one side undefined.
- **For a landing deorbit** the mod searches a Lambert transfer to a point uprange of the rotating site over a 16 x 24 grid of departures and flight times, fully checks at most 8 candidates and refines with golden section (see [Execute planned burns hands-free with the stock Auto burn](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md)).
- Stock's planner state changes (`_transferCalculated`) gate the whole feature; re-check on game updates.

## See also

- [Overview](overview.md); [encounter prediction](aim-a-transfer-at-a-flyby-periapsis-2.md); [own Lambert](aim-a-transfer-at-a-flyby-periapsis-3.md)
- [Aim a transfer at a flyby periapsis instead of the body center](../../../../mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting.md)
- [Add plan types to the stock Transfer Planner](../add-plan-type-to-the-transfer-planner/overview.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
