---
type: Mod Feature
title: Analytic sub-frame fuses by closest approach
description: Proximity, contact, timed, height and ground fuses are solved in closed form inside each integration sub-step, so a round at kilometres per second cannot step over its trigger radius and the burst instant is known to a fraction of the step
mod: ksarmory
category: physics
techniques: [custom-physics, analytic-closest-approach, sim-boundary]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.Program]
tags: [fuse, proximity-fuse, closest-approach, tunnelling, sub-stepping, projectiles, warhead]
---

A KSArmory round never asks "is the target within my fuse radius now?" at the ends of a step.
Inside every integration sub-step it solves for the moment of closest approach to each candidate
body analytically, `t = -r.v / v.v` clamped to the sub-step, and fires if the separation at that
instant is inside the trigger. A round at 2 km/s that crosses 10 m per 5 ms sub-step therefore
still sees a 2.5 m fuse, and the burst position and its time within the frame come out of the same
solve.[^ksarmory-readme][^ksarmory-vec]

The pattern is reusable for anything fast that must detect a near pass against something it
samples only once per frame: hit detection, docking triggers, sensor proximity alarms.

## How it works

**The primitive.** `Vec.TimeOfClosestApproach(r, v, horizon)` treats the separation as linear
over the step, `r(t) = r + v t`, and returns the minimising `t` clamped to `[0, horizon]`
(0 for a stationary pair). Miss distance is `|r + v t|`.[^ksarmory-vec]

**Every input is a difference at the round's own epoch.** World samples are end-of-step
(see [mod-simulated rounds](./mod-simulated-rounds.md)), so each body is back-dated before
differencing: `r = body.PositionEcl + body.VelocityEcl * (elapsedInFrame - frameSeconds) - round.PositionEcl`
and `v = body.VelocityEcl - round.VelocityEcl`. Both terms carry the ~29.8 km/s ecliptic carrier,
which cancels in the subtraction.[^ksarmory-contactsweep][^ksarmory-frames]

**Fuses by round type.**

| Fuse | Round | Trigger | Where it bursts | `DetonationElapsedInFrame` |
|---|---|---|---|---|
| Proximity | `Interceptor` (after `FuseArmSeconds`) | `FuseRadius + target.Radius` vs miss at TCA over one sub-step | Advanced by `VelocityEcl * tCa` | `elapsed + tCa - frameSeconds` |
| Contact / proximity sweep | `Slug` | `ContactSweep.TryStrike`: sphere of `trigger + body.Radius`, then the hull test | Earliest contact over the designated target and every contact | `elapsed + soonest - frameSeconds` |
| Timed (flak) | `Slug` with `FuseSeconds > 0` | `Age >= FuseSeconds`, exact within the sub-step | `VelocityEcl * tBurst` into the step | `elapsed + tBurst - frameSeconds` |
| Height (air burst) | `Slug` with `BurstHeightMetres > 0` | Altitude crosses the height on the way down, after arming | Linear fraction of the step | `elapsed + h g - frameSeconds` |
| Ground | `Slug` with `HitsTerrain`, every `Interceptor` | Altitude crosses zero from above | Linear fraction (optionally refined on the terrain) | `elapsed + h f - frameSeconds` |

(Sources: `Interceptor.Step`, `Slug.Step`.)[^ksarmory-interceptor][^ksarmory-slug]

**Proximity on a missile.** The fuse is evaluated after the guidance command is computed and before
the sub-step is integrated, using the same back-dated `r` and `v` the guidance uses, and it "does not ask the seeker's permission": a
round that has lost its seeker can still fuse. A seduced round fuses on the decoy it is chasing.

**Contact on a shell.** `Slug` considers the designated target and every contact (every vehicle in the
world except its own platform) and keeps the **earliest** contact time, not the first found,
because enumeration order is arbitrary. Before arming the trigger radius is 0, so a shell still
strikes what it physically touches; arming only widens the reach.[^ksarmory-slug]

**Two-phase strike** (`ContactSweep.TryStrike`):

1. Broad phase: `TryContact` with `fuseRadius + bodyRadius`. A sphere containing the mesh cannot
   give a false negative and rejects almost everything.
2. Narrow phase: the [hull test](./hull-triangle-hit-test.md) is handed the separation and the
   round's travel relative to the body over the whole sub-step. `Missed` rejects, `Unknown` keeps
   the sphere's verdict, `Struck` replaces the contact time with `fraction * step` and the miss
   distance with 0.[^ksarmory-contactsweep]

**Ground and height crossings** are linear in altitude across the sub-step: `f = was / (was - now)`,
with the ground centre itself back-dated per sub-step (see
[terrain test](./terrain-ground-impact-test.md)). Only a downward crossing counts, so a round
launched from a tube slightly below the coarse height field does not burst on its rail.

**What the burst instant feeds.** `Detonate` uses `DetonationElapsedInFrame` (negative) to move
every world sample back to the burst instant before judging it, for example
`BlastSweep.SurfaceGap(position, velocity, elapsed, burst, meanRadius)`. The sign matters: reversing
it doubles the frame-sized error instead of cancelling it.[^ksarmory-blastsweep][^ksarmory-interceptor]

**Fuse radius is not lethality.** Lethality is judged afterwards: a hit kills when
`MissDistance <= LethalRadius + target MeanRadius` (`Detonate`), and both blast radii come from the
charge by cube-root scaling (`LethalRadius = 7.368 * cbrt(ChargeKg)`, `BlastRadius = 22.104 * cbrt(ChargeKg)`).[^ksarmory-weaponsystem][^ksarmory-warhead]

| Munition | `FuseRadius` (m) | `ChargeKg` | Lethal radius (m) |
|---|---|---|---|
| 57E6 (profile defaults) | 15 | 20 | 20.0 |
| AIM-9J | 12 | 9.4 | 15.5 |
| AIM-120C-7 | 14 | 20 | 20.0 |
| AGM-88 | 16 | 66 | 29.8 |
| M61A2 20 mm | 2.5 | 0.05 | 2.7 |
| 2A38M 30 mm | 3 | 0.16 | 4.0 |
| 5"/54 HEDP (timed) | 12 | 3.3 | 11.0 |

(Values from `Arsenal.cs`, `Weapons.xml` and the `MunitionProfile` defaults; radii computed from
`Warhead`.)[^ksarmory-arsenal][^ksarmory-weapons-xml][^ksarmory-munition]

**`ProximityWatch` is not a fuse.** Despite the name it is a measurement instrument for the MIRV
bus: the closest the bus came to the stage it dropped, armed only once the pair first opens past a
keep-out distance, never read by any decision.[^ksarmory-proximitywatch]

## Lifecycle and timing

- Fuses run inside `Interceptor.Step` / `Slug.Step`, inside `RoundDriver.Fly`, inside
  `WeaponSystem.UpdateRounds`, inside the mod's once-per-frame `StepSimulation`. That runs from
  `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`, Program.cs L2268), or from the
  `Program.OnFrameCelestials` prefix (call at L2320) when the UI is hidden, or from
  `[StarMapAfterOnFrame]` as the fallback.[^ksa-program]
- At that point the targets' `GetPositionEcl()`/`GetVelocityEcl()` are the values
  `Vehicle.UpdatePerFrameData` (L2645-L2651) wrote during `Universe.ApplyVehicleSolvers`
  (Universe.cs L1950): the end of the step about to be integrated. The fuse back-dates them by
  `frameSeconds - elapsedInFrame` and extrapolates linearly across the sub-step.[^ksa-vehicle][^ksa-universe]
- The next vehicle step is already running on workers (Program.cs L2220), so the fuse only reads.
  A detonation's consequences (damage, destruction) are applied later in the same `UpdateRounds`
  call through `ApplyPendingKills`.
- Because `UpdateRounds` runs before fire control in `WeaponSystem.Update`, a round fused in a
  frame never sees a target position written by the same frame's fire control.[^ksarmory-weaponsystem]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Vec.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Vec.cs#L81-L91) | `TimeOfClosestApproach` |
| [src/KSArmory/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ContactSweep.cs) | `TryContact`, `TryStrike` (sphere then hull), `TryReachSphere` |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L542-L598) | Missile proximity fuse and ground crossing |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L617-L843) | Timed, contact, height and ground fuses for unguided rounds |
| [src/KSArmory/Sim/Warhead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Warhead.cs) | Lethal and blast radii from the charge |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3758-L3935) | `Detonate`: lethality from `MissDistance`, world moved to the burst instant |
| [tests/KSArmory.Tests/HullStrikeTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/HullStrikeTests.cs) | Kinetic rounds must touch; sphere rejects before hull is asked |

## Snippets

```csharp
// src/KSArmory/Sim/Vec.cs L85-L91
    public static double TimeOfClosestApproach(double3 r, double3 v, double horizon)
    {
        double vv = Len2(v);
        if (vv < 1e-12) return 0.0;
        double t = -Dot(r, v) / vv;
        return Math.Clamp(t, 0.0, horizon);
    }
```

```csharp
// src/KSArmory/Sim/Interceptor.cs L545-L565
                if (Age >= munition.FuseArmSeconds)
                {
                    double trigger = munition.FuseRadius + t.Radius;
                    double tCa = Vec.TimeOfClosestApproach(r, v, h);
                    double miss = Vec.Len(r + v * tCa);
                    if (miss <= trigger)
                    {
                        // Detonate where the round actually is at closest approach.
                        PositionEcl += VelocityEcl * tCa;
                        MissDistance = miss;
                        // ...
                        DetonationElapsedInFrame = elapsedInFrame + tCa - frameSeconds;
                        State = RoundState.Detonated;
                        return;
                    }
                }
```

```csharp
// src/KSArmory/Sim/ContactSweep.cs L52-L76
    public static bool TryStrike(double3 separation, double3 closingVelocity, double stepSeconds,
                                 double fuseRadius, double bodyRadius,
                                 IHullTest? hull, object? body,
                                 out double timeOfContact, out double missDistance)
    {
        if (!TryContact(separation, closingVelocity, stepSeconds, fuseRadius + bodyRadius,
                        out timeOfContact, out missDistance))
        {
            return false;
        }

        if (hull is null) return true;

        // Relative travel: in a frame riding with the body it stands still for the step, which is
        // what makes a static segment the right query against a mesh sampled once.
        HullVerdict verdict = hull.Judge(body, separation, -closingVelocity * stepSeconds,
                                         out double fraction);

        if (verdict == HullVerdict.Missed) return false;
        if (verdict == HullVerdict.Unknown) return true;

        timeOfContact = Math.Clamp(fraction, 0.0, 1.0) * stepSeconds;
        missDistance = 0.0;
        return true;
    }
```

## Game APIs touched

| API | Decompiled file | Used for |
|---|---|---|
| `Vehicle.GetPositionEcl()` / `GetVelocityEcl()` (L954, L960) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Target and contact state, end of the applied step |
| `Vehicle.MeanRadius` (L535, `BoundingSphereRadiusBody`) | same | `TargetState.Radius` added to the trigger and to the lethal range |
| `Universe.GetLastSimStep()` (L2381) | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | `frameSeconds`, the interval the back-dating spans |

## Pitfalls and version notes

- **`MissDistance` is not a miss distance.** It is the range at a threshold crossing whose
  look-ahead is one sub-step, so it is bounded by the trigger whatever the round really did, and it
  converges upward toward the fuse radius as the sub-step shrinks. The author measured it reading
  10-15 m while rounds missed by 450-680 m (before target back-dating was added). The honest number
  is the `tgt` range in the `SyncRoundBodies` debug trace, or `MissFromAimpoint` for a ground
  aimpoint.[^ksarmory-frames][^ksarmory-weaponsystem]
- **A shell's proximity reach is contact-only against meshed craft.** For a `Slug` the hull test
  runs after the fuse sphere, and a `Missed` verdict vetoes it, so a shell passing a few metres
  beside a craft with pickable meshes does not burst; `FuseRadius` only widens the broad phase. It
  still acts as a proximity radius against rounds-as-contacts and craft the hull test cannot answer
  (`Unknown`). This follows from the code as written and from `HullStrikeTests`
  ("A kinetic round has to touch what it kills"); the 5"/54 gets its flak effect from the timed
  fuse instead.[^ksarmory-contactsweep][^ksarmory-hullstriketests]
- **Fuse below lethal, mostly.** For every shipped missile `FuseRadius < LethalRadius`, so every
  proximity trigger is lethal and the log says "destroyed" regardless of how good the shot was.
  The 5"/54 is the exception (12 m fuse, 11.0 m lethal radius).[^ksarmory-frames]
- **Linear separation is an approximation.** Curvature within one sub-step is dropped; the error is
  about half the lateral acceleration times the step squared, which is why
  `MaxFaithfulStepSeconds` defaults to 0.32 s (a 35 g endgame loses about its own fuse radius
  there).[^ksarmory-munition]
- **Ground crossings use the chord.** Two height samples a sub-step apart can disagree with the
  terrain between them; `Slug.StopOnTheTerrain` adds one false-position refinement against the
  real terrain on the landing frame.[^ksarmory-slug]

## See also

- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Hull triangle hit test](./hull-triangle-hit-test.md)
- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Terrain ground impact test](./terrain-ground-impact-test.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Countermeasure dispensers](./countermeasure-dispensers.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [Analytic closest approach t = -r.v / v.v clamped to the step](../../../howto/physics/mod-simulation/prevent-tunnelling-of-fast-bodies/prevent-tunnelling-of-fast-bodies-1.md) - how-to article demonstrating this feature
- [IProjectile objects with fixed 5 ms sub-steps and a faithful-step clamp](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-1.md) - how-to article demonstrating this feature

[^ksarmory-readme]: README.md, How it works
[^ksarmory-vec]: src/KSArmory/Sim/Vec.cs
[^ksarmory-contactsweep]: src/KSArmory/Sim/ContactSweep.cs
[^ksarmory-frames]: docs/FRAMES-AND-EPOCHS.md (author notes)
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-slug]: src/KSArmory/Sim/Slug.cs
[^ksarmory-blastsweep]: src/KSArmory/Sim/BlastSweep.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-warhead]: src/KSArmory/Sim/Warhead.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-weapons-xml]: src/KSArmory/KSArmory/Weapons.xml
[^ksarmory-munition]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-proximitywatch]: src/KSArmory/Sim/ProximityWatch.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksarmory-hullstriketests]: tests/KSArmory.Tests/HullStrikeTests.cs
