---
type: Game Feature
title: Aim a transfer at a flyby periapsis and predict its encounter
description: Target a chosen periapsis around a destination body instead of its center by solving the transfer with Lambert, predict the encounter periapsis of a planned burn, and iterate the departure burn until the miss is closed
category: flight-and-orbits
group: transfers
topic: aim-a-transfer-at-a-flyby-periapsis
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/flyby-periapsis-targeting
  - mods/ksa-advancedflightcomputer/features/deorbit-landing-planner
  - mods/ksa-advancedflightcomputer/features/flyby-encounter-prediction
  - mods/ksarmory/features/icbm-ballistic-computer
lifecycle: [frame:draw-ui, frame:pre-render, frame:dispatch-solvers]
game_types:
  - KSA.OrbitalTransfers
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.TransferPlanner
  - KSA.Burn
  - KSA.CorrectionBurnTask
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Encounter
  - KSA.Celestial
tags: [lambert, flyby, periapsis, impact-parameter, encounter, patched-conic, soi, transfer]
---

The stock Transfer Planner aims every transfer at the target's centre, so a well-timed Hohmann transfer arrives as an impact and a flyby needs a later correction burn. Three techniques cover the problem: offset the Lambert aim point by an impact parameter derived from the wanted periapsis, check the result by propagating the burn through the game's own patched-conic code,
and solve an Earth-fixed target with your own universal-variable Lambert in the parent body's inertial frame.

## Engine background

- **Impact parameter.** For a hyperbolic approach at speed `v_soi` relative to the target at the SOI radius `r_soi`, `b = r_p v_p / v_soi` with `v_p^2 = v_soi^2 - 2 mu / r_soi + 2 mu / r_p`. That is `OrbitalTransfers.ImpactParameterAtSoiEntry(mu, soiRadius, relativeSpeed, periapsis)`; there is also `ImpactParameterForPeriapsis(mu, hyperbolicExcess, periapsis)`. Stock's own course correction (`CorrectionBurnTask`) uses these with the target's `mu`.[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L686-L696) [^ksa-correctionburntask]
- **Lambert and escape.** `OrbitalTransfers.SuperiorLambert(mu, positionOne, positionTwo, timeOfFlight, out velocityDeparture, out velocityCapture)` solves the transfer; `SingleImpulseHyperbolicEscape(...)` turns a hyperbolic excess into a single impulse from a parking orbit.[current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L765-L846) [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L992-L1030)
- **Propagation.** `FlightPlan.CalculateBurnPatch(prePatch, timeSincePe, dvVlf, burnTime)` turns an impulsive VLF delta-v into the next `PatchedConic`; `ComputeCompleteTrajectory(out errors, patchLimit, polynomialOrderN, encounterFilter, resolveImpactsCompletely)` finds SOI transitions; `Encounter` carries `Body`, `GameTime`, `ClosestDistance`.[^ksa-flightplan]
  [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L214-L222) [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L399-L410) [^ksa-encounter]
- **Two `mu` values** are in play: the Lambert solve uses the shared parent's (Earth for LEO to Luna, the Sun for LEO to Mars), the impact parameter uses the target's own.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Impact-parameter offset with transpiled planner hooks](aim-a-transfer-at-a-flyby-periapsis-1.md) | KSA-AdvancedFlightComputer | harmony transpiler | `frame:draw-ui` | Exact Create button in the stock planner; planner-coupled |
| [CalculateBurnPatch plus ComputeCompleteTrajectory filtered to the target](aim-a-transfer-at-a-flyby-periapsis-2.md) | KSA-AdvancedFlightComputer | harmony postfix | `frame:draw-ui`, `frame:pre-render` | Verifies against the game's own propagation; typed failure reasons |
| [Own universal-variable Lambert against a spinning-planet target](aim-a-transfer-at-a-flyby-periapsis-3.md) | KSArmory | Lambert, universal variables | `frame:draw-ui`, `frame:dispatch-solvers` | Self-contained, bisection-robust; a moving surface target in CCI |

## See also

- [Add plan types to the stock Transfer Planner](../add-plan-type-to-the-transfer-planner/overview.md)
- [Compute phase angles, synodic periods and the time to the next transfer window](../compute-transfer-windows/overview.md)
- [Estimate transfer delta-v in closed form with patched conics](../estimate-transfer-delta-v-with-patched-conics/overview.md)
- Related topics: [Draw a predicted trajectory and markers in the map view, and control orbit lines](../../map-view/draw-a-predicted-trajectory-and-map-markers/overview.md), [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md), [Plan and fly a powered landing](../../guidance/plan-and-fly-a-powered-landing/overview.md), [Predict impact points with drag and build a ballistic computer](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-correctionburntask]: KSA.CorrectionBurnTask (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-encounter]: KSA.Encounter (decompiled, build 2026.10.10.5554)
