---
type: How-To
title: Ecl state at GetElapsedTime with epoch back-dating
description: Treat every engine sample as belonging to one instant, the end of the step just applied, back-date targets to the sub-step a round is at, and draw against the position KSA renders a craft at
category: physics
group: mod-simulation
topic: simulate-bodies-outside-the-vehicle-system
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/ecliptic-frames-and-epochs]
techniques: [epoch-backdating, custom-physics]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.Camera, KSA.Vehicle, KSA.Celestial, KSA.Program]
tags: [physics, frames, epochs, ecliptic, ego, back-dating, 29.8-kms, camera, draw-anchor]
---

Outcome: a mod-simulated body that aims at, hits and is drawn against moving craft without a 450-680 m error, by
treating every position read from the engine as one instant and moving between instants explicitly. Prerequisites:
StarMap. Near Earth every ecliptic position carries about 29.8 km/s of the planet's orbital motion, roughly 500 m per
frame at 60 fps, so any two positions differenced across even part of a step leak a slice of it, and the leak shows
up as jitter, an offset, a guidance error or a drift. KSArmory follows four rules everywhere.

## Background: how the game does it

| Frame | Source | Use in a mod-simulated body |
|---|---|---|
| Ecl | `Vehicle.GetPositionEcl()` / `Celestial.GetPositionEcl()`: cached `_positionEcl`, composed parent-first | All simulation: rounds, targets, gravity |
| Ego | Camera-relative render frame: `Camera.EclToEgo(p)` and `EgoToEcl(p)` are a pure translation by `camera.PositionEcl` | Drawing, cursor rays |
| Drawn Ego of a craft | `Camera.GetPositionEgo(IPosition)` | Anchoring overlays and bursts |
| Cci / Cce | Body-centred inertial / ecliptic-aligned | Attitude and shoves, see [the PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md) |

Every Ecl sample is written inside `Program.PrepareFrame` and nowhere else, before any mod hook:

| Step in `PrepareFrame` | Effect |
|---|---|
| `Universe.ApplyOrbitSolvers` | Each celestial's `Orbit` takes state vectors the worker evaluated at `_simStep.NextTime` |
| `Universe.ApplyVehicleSolvers` | Vehicle kinematic states integrated to `SimStep.NextTime` are copied in; then `_lastSimStep = _nextSimStep` |
| `CurrentSystem.UpdatePerFrameData()` | One parent-first walk rebuilds every body's and vehicle's Ecl cache |

So in a hook that runs after `PrepareFrame` every vehicle and celestial position is at `Universe.GetElapsedTime()`,
the end of the step just applied, and nothing moves until the next frame's `PrepareFrame`.[^ksa-universe]
The camera's drawn position is computed live and branches on what the camera follows:

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

For the followed craft it is `-PositionCce`; for another craft in the same physics bubble it is built from physics
positions; otherwise it is `GetPositionEcl() - PositionEcl`. The branching means the drawn position of a craft
in flight differs from its analytic ecliptic position, which is the reason for rule 1 below.[^ksa-camera]

## Lifecycle and timing

The simulation and the drawing both happen in the pre-render hooks (`frame:draw-ui`, `frame:celestials`) so that
what is placed this frame is drawn against craft that have not moved on. A hook after the render
(`frame:after-on-frame`) sees the same epoch, but anything it places is drawn next frame against moved craft.
Read the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the
order. The mod's own rounds, integrated across the same step inside the hook, start at the previous `NextTime`;
that one-step phase between the engine samples and the mod's objects is what rules 2 and 3 deal with.

## Steps

1. Draw against the position KSA renders. Build an anchor from `Camera.GetPositionEgo(platform)` on the camera the
   frame will actually render with (`Program.GetRenderCamera()`, falling back to `GetMainCamera()`), paired with the
   platform's Ecl position at the instant the geometry was measured. Every overlay point then goes through
   `ToEgo`: the Ecl terms cancel the ecliptic carrier, and the Ego term supplies where KSA draws the platform:

```csharp
// src/KSArmory/Sim/DrawAnchor.cs L29-L40
internal readonly struct DrawAnchor(double3 ego, double3 ecl)
{
    /// <summary>The platform's position in the render frame, sampled this frame.</summary>
    public double3 Ego { get; } = ego;

    /// <summary>The platform's ecliptic position at the instant the geometry was measured.</summary>
    public double3 Ecl { get; } = ecl;

    public bool IsValid => Vec.IsFinite(Ego) && Vec.IsFinite(Ecl);

    /// <summary>Maps an ecliptic position from the geometry's epoch into the render frame.</summary>
    public double3 ToEgo(double3 posEcl) => Ego + (posEcl - Ecl);
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3438-L3470
    public static bool BeginDraw(Vehicle anchor, double3 anchorEcl)
    {
        _anchored = false;
        if (!IsAlive(anchor)) return false;

        try
        {
            // The camera the frame will actually be rendered with, not the main viewport's.
            Camera camera = Program.GetRenderCamera() ?? Program.GetMainCamera();
            if (camera is null) return false;

            // See DrawAnchor for why these are sampled at different instants, and why
            // collapsing them into one puts the whole overlay beside the craft.
            //
            // GetPositionEgo, not EclToEgo. Its branching on what the camera follows is the
            // engine answering correctly per case — exact for the followed craft, physics-based
            // for others in its bubble — and it is the same call KSA renders vehicles with.
            // EclToEgo instead measures against camera.PositionEcl, which only agrees with the
            // rendered scene when the followed craft's analytic and physics positions coincide.
            // That holds for a landed launcher and fails once the camera follows something in
            // flight, which is when anything drawn to a vehicle stops lining up.
            _anchor = new DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl);

            if (!_anchor.IsValid) return false;

            _anchored = true;
            return true;
        }
        catch
        {
            return false;
        }
    }
```

   Clear the anchor each frame so a pass that forgets `BeginDraw` draws nothing instead of drawing 500 m off.
2. Back-date targets to the round's own instant. Engine samples are end-of-step. A round being sub-stepped is
   `elapsedInFrame` seconds into the step (`-frameSeconds` at its start). Without the correction proportional
   navigation flies a clean intercept on a ghost one frame of ecliptic motion away:

```csharp
// src/KSArmory/Sim/Interceptor.cs L460-L480
            //
            // KSA writes every vehicle's Ecl state once per frame, at the top of OnFrame, to the
            // state at GetLastSimStep().NextTime - the END of the step this update is about to
            // integrate the round across. Differenced against the round's PRE-step position, that
            // leaves every line of sight carrying a constant
            //
            //     r = r_true + targetVelocityEcl * frameSeconds
            //
            // and proportional navigation, doing its job perfectly, flies a clean intercept on a
            // ghost displaced by one frame of the planet's ~29.8 km/s of ecliptic motion: 450-680 m,
            // against the 10-15 m MissDistance reports.
            //
            // MissDistance cannot show that. It is a threshold crossing with a one-sub-step
            // horizon, so it is bounded by the fuse radius whatever the round actually does. Nor
            // can a finer sub-step: a whole-frame bias only makes the round converge harder on the
            // same wrong point.
            //
            // Subtracting frameSeconds puts the target back at the instant the round is actually
            // at, which is what makes the common ecliptic motion cancel.
            double3 targetPos = t.PositionEcl + t.VelocityEcl * (elapsedInFrame - frameSeconds);
            double3 targetVel = t.VelocityEcl;
```

3. Carry a point between instants with one function per direction, never an expression retyped at each site.
   `AtBurst` moves an end-of-step sample back to the burst instant, `AtSample` moves a point forward to the sample
   instant:

```csharp
// src/KSArmory/Sim/InFrame.cs L13-L22
public static class InFrame
{
    /// <summary>A point sampled at the step's end, carried back to <paramref name="sinceSample"/>.</summary>
    public static double3 AtBurst(double3 atSample, double3 velocityEcl, double sinceSample)
        => atSample + (velocityEcl * sinceSample);

    /// <summary>A point at <paramref name="sinceSample"/>, carried forward to the step's end.</summary>
    public static double3 AtSample(double3 atBurst, double3 velocityEcl, double sinceSample)
        => atBurst - (velocityEcl * sinceSample);
}
```

4. Publish a mod's own rounds as targets at the same phase as the engine samples. `CollectAirborne` carries each
   round forward by `VelocityEcl * step` before any system reads it, so a round looks the same to every consumer
   whichever system steps first:

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

5. Hand drawn points back to engine effects as Ecl. A burst effect takes Ecl, so convert the drawn Ego point with
   `camera.EgoToEcl`. KSArmory's `DrawnBurstEcl` adds the round's offset at the burst instant to the platform's drawn
   Ego position, converts back, and logs when the result differs from the analytic burst by more than a metre:

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

6. For a cursor ray, build it in Ego and convert only the origin. A direction is identical in Ecl and Ego because
   Ego is a pure translation of Ecl. The pixel position must be in camera framebuffer pixels before
   `Camera.ScreenToEgoRay`:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4113-L4152
    public static bool TryCursorRayEcl(out double3 originEcl, out double3 directionEcl)
    {
        originEcl = default;
        directionEcl = default;
        try
        {
            float2 cursor = ImGui.GetMousePos();

            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                IGameViewport v = viewports[i];
                if (!v.Visible) continue;

                if (v.GetCamera() is not { } camera) continue;

                // Framebuffer pixels, not viewport pixels: ScreenToEgoRay divides by the camera's
                // own framebuffer, and a render or display scale makes those different sizes.
                if (!CursorAim.TryToFramebuffer(cursor, v.Position, v.Width, v.Height,
                                                camera.FramebufferSize.X, camera.FramebufferSize.Y,
                                                out float2 local))
                {
                    continue;
                }

                double3 direction = camera.ScreenToEgoRay(local).Direction;
                if (!CursorAim.IsUsableDirection(direction)) continue;

                originEcl = camera.EgoToEcl(Vec.Zero);
                directionEcl = Vec.Unit(direction);
                return Vec.IsFinite(originEcl);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DrawAnchor.cs) | `ToEgo`, `OffsetAtBurst` |
| [src/KSArmory/Sim/InFrame.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/InFrame.cs) | `AtBurst`, `AtSample` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3409-L3540) | `BeginDraw`, `TryEclToEgo`, `TryVehicleEgo` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L4002-L4022) | `DrawnBurstEcl` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1285-L1338) | `CollectAirborne` and `AddAirborne` |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L460-L480) | The target back-date |

## Variations and gotchas

- Use `GetPositionEgo`, not `EclToEgo`, for a craft: `EclToEgo` measures against `camera.PositionEcl`, which agrees
  with the rendered scene only when the followed craft's analytic and physics positions coincide (a landed
  launcher), and fails once the camera follows something in flight.
- `Camera.GetPositionEgo` is computed live from stored positions and the camera's follow offset, so it agrees with
  the render only if nothing moves the camera between the read and `Render`.
- A miss-distance report cannot expose a whole-frame bias: it is a threshold crossing with a one-sub-step horizon.
  A finer sub-step makes the round converge harder on the same wrong point.
- Differences are the cure for large coordinates: see [approach 6](simulate-bodies-outside-the-vehicle-system-6.md).
- For a mod that integrates in the planet-fixed frame instead, none of this applies: see
  [approach 2](simulate-bodies-outside-the-vehicle-system-2.md).

## See also

- [Overview and comparison](overview.md)
- [Approach 3: the applied step](simulate-bodies-outside-the-vehicle-system-3.md), [approach 5: Ccf from the physics state](simulate-bodies-outside-the-vehicle-system-5.md)
- Mod feature: [KSArmory ecliptic frames and epochs](../../../../mods/ksarmory/features/ecliptic-frames-and-epochs.md)
- The topic understand-ksa-coordinate-frames-and-epochs (other batch) covers the frames in full

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
