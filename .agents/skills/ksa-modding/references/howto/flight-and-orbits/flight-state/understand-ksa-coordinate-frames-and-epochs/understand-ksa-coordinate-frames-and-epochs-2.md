---
type: How-To
title: Use Ecl as the working frame, Ego for anything drawn, and cancel the 29.8 km/s carrier
description: Keep all simulation state in the ecliptic frame at one instant, move samples between instants only with an explicit carry, and use frame-invariant relative quantities such as closest approach so the planet's orbital motion cancels
category: flight-and-orbits
group: flight-state
topic: understand-ksa-coordinate-frames-and-epochs
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/ecliptic-frames-and-epochs, mods/ksarmory/features/cpa-threat-classification]
techniques: [custom-physics, draw-anchor, epoch-backdating, frame-conversion, relative-frame-math]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:apply-solvers, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.Camera, KSA.Universe, KSA.PhysicsBubble]
tags: [frames, epochs, ecl, ego, carrier, closest-approach, back-dating, sampling]
---

Outcome: a set of rules that keeps a mod's own simulation (projectiles, targets, gravity, blast
effects) correct in KSA's ecliptic frame, with the right instant attached to every sample. KSArmory
applies them throughout; [One ecliptic frame and one epoch for everything simulated and drawn](../../../../mods/ksarmory/features/ecliptic-frames-and-epochs.md) its threat model shows the second
half of the idea, quantities that are the same in every frame. [Closest-point-of-approach threat classification](../../../../mods/ksarmory/features/cpa-threat-classification.md)
Prerequisite: StarMap for the hooks; Harmony only if you patch `OnFrameCelestials` as KSArmory does.

## Background: how the game does it

Every vehicle's and celestial's ECL position is a cached field, rewritten once per frame in
`PrepareFrame` and never again until the next frame. The write order inside
`Universe.ApplyVehicleSolvers` is: vehicle results applied, `_lastSimStep = _nextSimStep`,
`CurrentSystem.UpdatePerFrameData()` (parent first). All samples are then at
`Universe.GetElapsedTime()`, the **end** of the step that just finished.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)
[current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L274-L285) [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L110-L124) The orbit worker evaluates celestial
orbits at the same `NextTime` (`CelestialUpdateTask`), so bodies and vehicles share the epoch.[^ksa-celestialupdatetask]
[current/decomp/KSA/CelestialUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialUpdateTask.cs#L52-L64)

Two consequences drive the rules below:

- Near Earth an ECL position carries the planet's orbital velocity, 29.8 km/s, which is about 500 m per
  frame at 60 fps. Two ECL positions sampled one step apart differ by that much even if both objects
  are at rest relative to each other.
- A mod that integrates its own objects inside a hook starts them at the *previous* `NextTime`, one step
  behind the engine samples it reads in the same hook. That one-step phase must be carried explicitly.

## Lifecycle and timing

Read engine ECL samples from any hook after `PrepareFrame`: `[StarMapAfterGui]` (`frame:draw-ui`) or a
prefix on `Program.OnFrameCelestials` (`frame:celestials`) both see the same epoch; nothing moves
until the next frame's `frame:apply-solvers`. A hook on `StarMapAfterOnFrame` sees the same epoch too,
but it runs after the render: gizmo instances it submits are wiped by `GizmosRenderer.ResetInstances` at the start of the
next `OnFrame` (before anything is drawn), and any state it computes is drawn a frame late against craft that have
moved on, so KSArmory both steps and draws in the pre-render hooks. The rule of the whole approach is that **one function decides which
instant each sample belongs to**, in the frame order of the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Simulate in ECL.** Use `Vehicle.GetPositionEcl()`, `GetVelocityEcl()` and
   `Celestial.GetPositionEcl()` as the common frame for every object (rounds, targets, gravity
   sources).
2. **Treat every engine sample as end-of-step.** Do not mix a sample taken this frame with one carried
   from the previous frame without saying so in the code.
3. **Move between instants with named carries.** KSArmory keeps one function per direction:

```csharp
// src/KSArmory/Sim/InFrame.cs L16-L21
public static double3 AtBurst(double3 atSample, double3 velocityEcl, double sinceSample)
    => atSample + (velocityEcl * sinceSample);

/// <summary>A point at <paramref name="sinceSample"/>, carried forward to the step's end.</summary>
public static double3 AtSample(double3 atBurst, double3 velocityEcl, double sinceSample)
    => atBurst - (velocityEcl * sinceSample);
```

4. **Back-date a target when you aim at it mid-step.** A round being sub-stepped is `elapsedInFrame`
   seconds into the step (zero at the end, negative before). Proportional navigation against the
   unadjusted sample flies at a ghost 450 to 680 m away; the fix is one expression:

```csharp
// src/KSArmory/Sim/Interceptor.cs L477-L479
// Subtracting frameSeconds puts the target back at the instant the round is actually
// at, which is what makes the common ecliptic motion cancel.
double3 targetPos = t.PositionEcl + t.VelocityEcl * (elapsedInFrame - frameSeconds);
```

5. **Sample moving targets once.** `CollectAirborne` publishes every round before any system steps, carried
   forward by `VelocityEcl * step` so each sits at the same end-of-step phase as a vehicle sample:

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

6. **Draw against the position KSA renders, not the analytic one.** `Camera.GetPositionEgo` returns
   `-PositionCce` for the followed craft, the bubble physics position for a craft in the same physics
   bubble, and `GetPositionEcl() - PositionEcl` otherwise; `Camera.EclToEgo` is only the
   subtraction:

```csharp
// current/decomp/KSA/Camera.cs L236-L250
public double3 GetPositionEgo(IPosition astronomical)
{
	if (astronomical == _following)
	{
		return -PositionCce;
	}
	if (_following is Vehicle { BubbleLeader: not null } vehicle && astronomical is Vehicle vehicle2 && vehicle2.BubbleLeader == vehicle.BubbleLeader)
	{
		double3 double5 = vehicle2.KinematicStates.PositionPhys.Transform(vehicle2.BubbleOrigin.GetBub2Cce());
		double3 double6 = -vehicle.KinematicStates.PositionPhys.Transform(vehicle.BubbleOrigin.GetBub2Cce());
		double3 double7 = -PositionCce;
		return double7 + double6 + double5;
	}
	return astronomical.GetPositionEcl() - PositionEcl;
}
```

   KSArmory then anchors every overlay with a two-instant struct (the render-frame position of the
   platform, plus the platform's ECL sample the geometry was measured against):

```csharp
// src/KSArmory/Sim/DrawAnchor.cs L29-L40, L53-L54
internal readonly struct DrawAnchor(double3 ego, double3 ecl)
{
    /// <summary>The platform's position in the render frame, sampled this frame.</summary>
    public double3 Ego { get; } = ego;

    /// <summary>The platform's ecliptic position at the instant the geometry was measured.</summary>
    public double3 Ecl { get; } = ecl;

    public bool IsValid => Vec.IsFinite(Ego) && Vec.IsFinite(Ecl);

    /// <summary>Maps an ecliptic position from the geometry's epoch into the render frame.</summary>
    public double3 ToEgo(double3 posEcl) => Ego + (posEcl - Ecl);
// ...
    public static double3 OffsetAtBurst(double3 offsetFromPlatform, double3 platformVelocityEcl, double elapsedInFrame)
        => offsetFromPlatform - (platformVelocityEcl * elapsedInFrame);
```

   The same idea is developed on its own in [the draw-anchor article](understand-ksa-coordinate-frames-and-epochs-5.md).
7. **Convert drawn points back to ECL with the same camera.** `Camera.EgoToEcl(ego)` is
   `ego + PositionEcl`; KSArmory's `DrawnBurstEcl` takes the platform's drawn Ego position plus the
   round's offset at the burst instant and converts that:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L4002-L4023
private double3 DrawnBurstEcl(IProjectile round, double3 analyticEcl)
{
    if (Platform is not { } platform) return analyticEcl;
    if (!KsaWorld.TryVehicleEgo(platform, out double3 platformEgo)) return analyticEcl;

    double3 offset = DrawAnchor.OffsetAtBurst(round.OffsetFromPlatform, KsaWorld.VelocityEcl(platform),
                                              round.DetonationElapsedInFrame);
    if (!KsaWorld.TryEgoToEcl(platformEgo + offset, out double3 drawn))
    {
        return analyticEcl;
    }

    double3 carried = InFrame.AtSample(analyticEcl, KsaWorld.VelocityEcl(platform), round.DetonationElapsedInFrame);
    double residual = Vec.Len(drawn - carried);
    if (residual > 1.0)
    {
        Log.Debug(() => $"  burst drawn {residual:F1} m from where the round struck, after carrying it "
                        + $"{Vec.Len(carried - analyticEcl):F1} m to the step's end");
    }

    return drawn;
}
```

8. **Prefer frame-invariant quantities.** A closest-approach classification depends only on the
   relative position `r` and relative velocity `v`, so the 1.5e11 m offset and the 29.8 km/s common
   motion cancel before any threshold is tested:

```csharp
// src/KSArmory/Sim/ThreatModel.cs L94-L95
double tCa = Vec.TimeOfClosestApproach(r, v, sensor.ThreatHorizonSeconds);
double cpa = Vec.Len(r + v * tCa);
```

   with `TimeOfClosestApproach` = `-dot(r, v) / |v|^2` clamped to `[0, horizon]`.[src/KSArmory/Sim/Vec.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Vec.cs#L85-L91)
   A mod whose logic can be written this way needs far fewer epoch rules.

## Complete example

KSArmory files, all pure arithmetic over `double3` apart from the thin KSA wrapper:

| File | Role |
|---|---|
| [src/KSArmory/Sim/InFrame.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/InFrame.cs) | `AtBurst` / `AtSample` carries |
| [src/KSArmory/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DrawAnchor.cs) | two-instant anchor, `OffsetAtBurst` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3409-L3540) | `BeginDraw`, `TryEclToEgo`, `TryVehicleEgo` |
| [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L62-L127) | `TryAssess` with the CPA test |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L465-L480) | target back-date |

## Variations and gotchas

- **`GetPositionEcl` is not where a craft is drawn** when the camera follows a craft in its physics
  bubble. Lines drawn to the analytic position visibly miss; anchor to `GetPositionEgo`.
- **Do not collapse the anchor's two samples into one.** Re-reading the platform's ECL at draw time, or
  deriving Ego from ECL, both look tidier and both move the overlay off the craft.
- **A carry belongs to its consumer.** The `VelocityEcl * step` carry is right for contacts and was flown
  and lost when applied to the rounds' own force sampling (the mod's notes record the miss growing from
  782 m to 2,667 m).
- **`KinematicStates` has no timestamp**; its epoch is `GetElapsedTime()` by construction. The body
  orbit's own `StateVectors.StateTime` is public and can be compared with it.
- `Celestial.PrepareWorker` no longer exists in build 5554: orbit solvers are prepared directly from
  `ExecuteNextOrbitSolvers`. Write-ups older than that build name it.

## See also

- [Overview of frames and epochs](overview.md)
- [Vessel state in CCI/CCF/ECL](understand-ksa-coordinate-frames-and-epochs-1.md), [owner-frame anchors](understand-ksa-coordinate-frames-and-epochs-3.md), [draw anchor](understand-ksa-coordinate-frames-and-epochs-5.md)
- [One ecliptic frame and one epoch for everything simulated and drawn](../../../../mods/ksarmory/features/ecliptic-frames-and-epochs.md), [Closest-point-of-approach threat classification](../../../../mods/ksarmory/features/cpa-threat-classification.md)
- Related topics: [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md)

[^ksa-celestialupdatetask]: KSA.CelestialUpdateTask (decompiled, build 2026.10.10.5554)
