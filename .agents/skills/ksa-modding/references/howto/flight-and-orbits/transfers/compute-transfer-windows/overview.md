---
type: Game Feature
title: Compute phase angles, synodic periods and the time to the next transfer window
description: Find the phase angle and the time until the next transfer window between two bodies using the game's AlignmentTime branches and Orbit.GetPhaseAngle, and schedule a multi-pass transfer around it
category: flight-and-orbits
group: transfers
topic: compute-transfer-windows
demonstrated_by:
  - mods/ksa-deltavmap/features/transfer-windows-and-phase-clock
  - mods/ksa-advancedflightcomputer/features/multi-pass-hohmann-schedule
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types: [KSA.Orbit, KSA.OrbitalTransfers, KSA.MathEx, KSA.IOrbiter, KSA.Universe]
tags: [transfer-window, phase-angle, synodic-period, alignment-time, hohmann, lambert, porkchop]
---

A transfer window is the moment the phase angle between two bodies equals the lead angle a Hohmann transfer needs. The game computes it in `OrbitalTransfers.AlignmentTime`; a mod that wants the angle, the synodic period or a countdown without opening the planner can mirror the same three branches with the game's own `Orbit.GetPhaseAngle`.

## Engine background

- `Orbit.GetPhaseAngle(UniverseTime)` is `LongitudeOfAscendingNode + sign * ArgumentOfPeriapsis + sign * trueAnomaly`, wrapped, with `sign` negative for inclinations of at least pi: an angle in the parent's frame that is comparable between two orbits around the same parent.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1909-L1915)
- `OrbitalTransfers.AlignmentTime(transferInfo, startTime, offsetDegrees)` has three branches: same-SOI, prograde and retrograde (relative inclination above pi/2). The lead angle is `pi (1 - ((aS + aT) / (2 aT))^1.5)`; the current phase is `ToOrbitAngle(targetPhase - sourcePhase)`; the synodic rate is `2 pi/Tt - 2 pi/Ts` (prograde) or the sum (retrograde); the result is `|gap / rate|`.[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L708-L754)
  The retrograde branch adds the two rates and does not wrap the gap.
- `OrbitalTransfers.SolveLambert` (which finishes through the private `FinalizeLambert`) is what the stock planner uses to pick the departure for a chosen window (see [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md)).
- The window quantities depend only on analytic body orbits, so evaluating `GetPhaseAngle` at `Universe.GetElapsedTime()` is a pure read and safe while the vehicle solver runs on worker threads.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Mirror the branches of AlignmentTime with a phase clock](compute-transfer-windows-1.md) | KSA-DeltaVMap | closed-form orbital math | `frame:draw-ui` | Countdown, synodic period, ejection angle and a polar clock; reimplements the game's branches |
| [Shift a same-parent window and re-solve Lambert](compute-transfer-windows-2.md) | KSA-AdvancedFlightComputer | harmony transpiler | `frame:draw-ui`, `frame:apply-solvers` | Hits the stock window exactly with several periapsis kicks; same-parent only |

## See also

- [Estimate transfer delta-v in closed form with patched conics](../estimate-transfer-delta-v-with-patched-conics/overview.md)
- [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Execute planned burns hands-free with the stock Auto burn](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md)
- Related topics: [Draw a predicted trajectory and markers in the map view, and control orbit lines](../../map-view/draw-a-predicted-trajectory-and-map-markers/overview.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
