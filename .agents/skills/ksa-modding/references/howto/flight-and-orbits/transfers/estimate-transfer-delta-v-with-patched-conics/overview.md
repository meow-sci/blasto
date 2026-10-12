---
type: Game Feature
title: Estimate transfer delta-v in closed form with patched conics
description: Compute Hohmann-style departure and arrival delta-v between circular reference orbits around one body and across spheres of influence with the patched-conic approximation and the game's own orbit and Mu data
category: flight-and-orbits
group: transfers
topic: estimate-transfer-delta-v-with-patched-conics
demonstrated_by:
  - mods/kitten-engineer-redux/features/orbit-readouts-and-hohmann-estimate
  - mods/ksa-deltavmap/features/patched-conic-transfer-model
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.IParentBody, KSA.OrbitalTransfers, KSA.Astronomical]
tags: [hohmann, patched-conic, delta-v, oberth, vis-viva, transfer, estimate]
---

A planning screen needs a delta-v number without running a flight-plan search. Two closed-form estimates cover most needs: a two-impulse Hohmann transfer around one body from the vehicle's current orbit to a circular altitude, and the patched-conic version for bodies that share a hub, where the departure and arrival burns are the hub Hohmann legs wrapped in Oberth
ejections and captures from each end body's low orbit.

## Engine background

- **The game's own Hohmann timing.** `OrbitalTransfers.HohmannFlight(origin, destination)` uses an effective radius per orbit: the semi-major axis when `Eccentricity < 0.01`, else `(Apoapsis + Periapsis) / 2`, then `pi * sqrt(a_t^3 / mu)` with `mu = origin.Parent.Mu`. The estimates below copy that rule.[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L756-L763)
- **Hyperbolic departure speed.** `OrbitalTransfers.DepartureSpeedForExcess(mu, radius, vInf)` is `sqrt(vInf^2 + 2 mu / radius)`, the same Oberth identity the patched-conic estimate uses.[current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L399-L402)
- **Mu.** `IParentBody.Mu = Mass * 6.6743E-11`; `Orbit.Mu` is the parent's `Mu` unless the orbit has its own central mass (see [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)). `KSA.Constants.GRAVITATIONAL_CONSTANT` is `6.6743E-11`.[^ksa-iparentbody] [^ksa-constants] [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L25)
- Everything reads orbit element getters of bodies and the vehicle's CCI state; it does not call the transfer planner (see [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md) for the exact Lambert solve).

All estimates run on the main thread inside a draw hook (`frame:draw-ui`) and only read state; see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Coplanar Hohmann estimate to a circular altitude](estimate-transfer-delta-v-with-patched-conics-1.md) | Kitten-Engineer-Redux | flight math | `frame:draw-ui` | Ten lines; assumes a circular coplanar start |
| [Hub legs wrapped in Oberth burns, cached per body pair](estimate-transfer-delta-v-with-patched-conics-2.md) | KSA-DeltaVMap | patched-conic math | `frame:draw-ui` | Whole-system pricing, comets, multiple stars, ascent and landing; many approximations to flag |

## See also

- [Compute phase angles, synodic periods and the time to the next transfer window](../compute-transfer-windows/overview.md)
- [Plan and total multi-leg and interstellar delta-v routes](../plan-multi-leg-and-interstellar-delta-v-routes/overview.md)
- [Aim a transfer at a flyby periapsis and predict its encounter](../aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Read orbit elements, apsides and event countdowns safely](../../flight-state/read-orbit-elements-and-timing/overview.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
