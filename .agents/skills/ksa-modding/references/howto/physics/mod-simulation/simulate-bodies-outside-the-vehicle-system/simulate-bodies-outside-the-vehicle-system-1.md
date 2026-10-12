---
type: How-To
title: IProjectile objects with fixed 5 ms sub-steps and a faithful-step clamp
description: Fly guided and unguided rounds as plain objects in sub-steps across each applied simulation step, with one driver shared by the game and a headless rig, and a clamp on how much time a frame may integrate
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/mod-simulated-rounds, mods/ksarmory/features/analytic-subframe-fuses]
techniques: [sub-stepping, custom-physics]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.SimStep, KSA.Vehicle, KSA.Celestial]
tags: [physics, custom-physics, sub-stepping, projectile, missile, simulated-time, headless-tests, frames]
---

Outcome: many short-lived, fast objects (a guided missile, a 20 mm shell, a reentry vehicle) that live only in the
mod, are integrated across each simulation step the engine applied, and are reaped when they detonate, expire or
are shot down. KSA never sees them as vehicles. Prerequisites: StarMap and Harmony. KSArmory demonstrates it; the
reasons are sub-frame accuracy (at 2 km/s of closing speed one frame is about 67 m, far more than any fuse radius)
and not touching the save.

## Background: how the game does it

KSA has no projectile system a mod can borrow: vehicles are heavy (part trees, Bepu bodies, flight plans in the
save). The engine does give you the clock and the state of everything else. At the start of `PrepareFrame` it applies
the finished step ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)), sets `_lastSimStep`, and refreshes every body's and vehicle's
ecliptic position; the next vehicle step then runs on workers while the frame's hooks execute. A mod hook is
therefore reading a settled world and may integrate its own objects across the interval that was just applied,
without locks, because the mod's objects touch no vehicle state.[^ksa-universe]

## Lifecycle and timing

The per-frame step is claimed once per frame by whichever of three hooks runs first (a `FrameLatch`):

| Hook | Lifecycle | When |
|---|---|---|
| `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`) | `frame:draw-ui` | Preferred: the step and the draw then share an epoch |
| Prefix on private `Program.OnFrameCelestials` | `frame:celestials` | Frames where F2 hid the UI, so the step still lands before the render |
| `[StarMapAfterOnFrame]` | `frame:after-on-frame` | Last resort: rounds are drawn one frame late |

What the step consumes is the applied step ([approach 3](simulate-bodies-outside-the-vehicle-system-3.md)), never
`dtPlayer * speed`. Every vehicle and celestial sample read in the step is the end of that step, while a round's
pre-step position is its start, which is why samples are back-dated ([approach 4](simulate-bodies-outside-the-vehicle-system-4.md)).
Within a frame the order that matters: fill the incoming list, radar scan, turret, `UpdateRounds(dt)`, then fire
control. Rounds go before fire control on purpose: a round fired this frame is first integrated next frame,
otherwise one frame of ecliptic motion is baked into its `TravelSinceLaunch` forever.

## Steps

1. Define the contract in a project that references no KSA types, so the same code runs in the game and in tests.
   KSArmory's `IProjectile` carries the absolute ecliptic state, the drawn offset from the launcher (measured after
   the step), the longest step the round can take now (`FaithfulStepSeconds`), the burst instant within the frame
   (`DetonationElapsedInFrame`, negative) and a `State` (`Flying`, `Detonated`, `Expired`, `ShotDown`).

2. Sub-step inside `Update`. The nominal sub-step is 5 ms for a missile (`clamp(ceil(dt / 0.005), 1, 64)`
   sub-steps) and per munition for a shell; a reentry vehicle can ask for 1 ms. Look up density, gravity and
   ground at the round's own position and sub-step time, back-dated, and measure the drawn offset after the step:

```csharp
// src/KSArmory/Sim/Interceptor.cs L363-L391
        int steps = Math.Clamp((int)Math.Ceiling(dt / SubStep), 1, MaxSubSteps);
        double h = dt / steps;
        double elapsed = 0.0;

        for (int i = 0; i < steps && State == RoundState.Flying; i++)
        {
            double density = AirDensityAt?.Invoke(PositionEcl, elapsed - dt) ?? mediumDensityRatio;
            if (!double.IsFinite(density) || density < 0.0) density = mediumDensityRatio;

            Step(h, elapsed, dt, target, gravity, frameVelocityEcl, munition, density);
            elapsed += h;
        }

        // Measured AFTER the step, against the platform sampled this same frame, with no
        // extrapolation whatsoever.
        //
        // ...
        OffsetFromPlatform = PositionEcl - platformEcl;
```

3. Advance every round through one function. `RoundDriver.Fly` assigns the per-sub-step lookups to the round and
   calls `Update`; the game passes the real lookups, the test rig passes its own, so neither can drift:

```csharp
// src/KSArmory/Sim/RoundDriver.cs L90-L119
    public static void Fly(IProjectile round, double dt, TargetState? target,
                           double3 gravity, double3 frameVelocityEcl, double3 platformEcl,
                           MunitionProfile munition, double mediumDensityRatio,
                           in RoundFields fields)
    {
        // Assigned rather than assigned-if-present: a caller that means "hold this field" says so
        // by passing nothing for it, and that has to be distinguishable from a caller that simply
        // did not reach this line.
        if (round is Slug slug)
        {
            slug.GravityAt = fields.GravityAt;
            slug.AirDensityAt = fields.AirDensityAt;
            slug.Ground = fields.Ground;
            slug.GroundCentreDriftAt = fields.GroundCentreDriftAt;
            slug.GroundQueryDriftAt = fields.GroundQueryDriftAt;
            slug.ApproachAt = fields.ApproachAt;
            slug.AirVelocityAt = fields.AirVelocityAt;
        }
        else if (round is Interceptor missile)
        {
            // The air, and the ground for one that stops on it. An interceptor's flight is dominated
            // by its own guidance rather than by gravity, but the medium decides whether it flies at
            // all: held for the frame, a round low over the sea reads as under it.
            missile.AirDensityAt = fields.AirDensityAt;
            missile.Ground = fields.Ground;
            missile.GroundCentreDriftAt = fields.GroundCentreDriftAt;
        }

        round.Update(dt, target, gravity, frameVelocityEcl, platformEcl, munition, mediumDensityRatio);
    }
```

4. Clamp the frame's step before any round sees it: `step = min(dtSim, faithful)`, where `faithful` is the smallest
   `MaxFaithfulStepSeconds` of any round in the air (default 0.32 s, shorter in air; `Medium.FaithfulStepInAir`
   is 0.05 s). Time beyond the clamp is discarded and logged, not deferred; a separate warp policy slows the
   world so the next frame fits.

5. Sample the world once. Before any system updates, snapshot every flying round into a contact carried forward
   by `VelocityEcl * step` so it sits at the end-of-step phase every KSA vehicle sample has; otherwise a round would
   read start- or end-of-step depending on which system asked first:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L1326-L1338
    private void AddAirborne(IReadOnlyList<IProjectile> rounds, string firedBy, string? team,
                             KSA.Vehicle? anchor, Celestial? body, double step)
    {
        for (int i = 0; i < rounds.Count; i++)
        {
            IProjectile r = rounds[i];
            if (r.State != RoundState.Flying) continue;

            double3 at = r.PositionEcl + r.VelocityEcl * step;
            _airborne.Add(new RoundContact(r, firedBy, team, anchor, at, r.VelocityEcl,
                                           CoastingAcceleration(r, body, at)));
        }
    }
```

6. Reap rounds that have no natural end: a time limit (`MaxFlightSeconds`, default 30 s), or for a round the
   ground stops, a trajectory classification (`RoundReach.Classify`: `Impossible` expires now, `Landing` and
   `Coasting` hold the clock, `Arriving` and `Unknown` run). When the launcher dies, re-anchor every offset onto the
   parent body so the rounds outlive it.

7. Draw outside the clock gate. `SyncRoundBodies` (the hidden launcher subpart the player sees) runs every rendered
   frame so bodies follow the platform through frames that advanced no simulated time. Expect an INFO log line on
   each detonation (`destroyed <craft>`, closest approach) and an overrun report when a frame exceeds the clamp.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/IProjectile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IProjectile.cs) | The round contract, KSA-free |
| [src/KSArmory/Sim/RoundDriver.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundDriver.cs#L45-L119) | `RoundFields` lookups and the single advance path |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L341-L400) | Guided round: sub-step loop |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L464-L565) | Unguided round: sub-step loop, reaper |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3380-L3500) | `UpdateRounds`: sampling, `Fly`, reaping, detonation |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L675-L800) | `StepSimulation`: consume step, clamp, airborne census |

## Variations and gotchas

- Never difference across instants: a round integrated to the end of the step and a platform sampled at the end of
  the same step cancel the ~29.8 km/s carrier; any other pairing leaks `v * dstep` (30 m per ms of frame wobble).
- A carry belongs to a consumer. The end-of-step carry on `AddAirborne` is right for contacts and cost 2 km of
  divergence when applied to the round's own gravity and density lookups.
- Time beyond the clamp is lost, not deferred.
- Rounds are invisible to the engine: collisions, the save and the map view do not know them; terrain is tested only
  where the profile sets `HitsTerrain`.
- Destroying a vehicle from the step needs the vehicle solver joined first (see
  [safe destruction](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-4.md)).

## See also

- [Overview and comparison](overview.md)
- [Approach 3: the applied step](simulate-bodies-outside-the-vehicle-system-3.md), [approach 4: epochs](simulate-bodies-outside-the-vehicle-system-4.md), [approach 6: headless rigs](simulate-bodies-outside-the-vehicle-system-6.md)
- Mod features: [mod-simulated rounds](../../../../mods/ksarmory/features/mod-simulated-rounds.md), [analytic sub-frame fuses](../../../../mods/ksarmory/features/analytic-subframe-fuses.md)
- [Detect hits for fast bodies without tunnelling](../../mod-simulation/prevent-tunnelling-of-fast-bodies/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
