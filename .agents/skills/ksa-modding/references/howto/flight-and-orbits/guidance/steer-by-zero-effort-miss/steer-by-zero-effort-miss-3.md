---
type: How-To
title: Steer a falling store by its predicted impact and take a ground designation as its input
description: Predict where a falling store lands under gravity and current drag, push square to its airflow to move that landing onto a designated place on the ground, and decide whether a new click corrects the store in the air or aims the next one
category: flight-and-orbits
group: guidance
topic: steer-by-zero-effort-miss
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/guided-bomb-tail-kit-and-sight
  - mods/ksarmory/features/ground-target-designation-and-reach
techniques: [predicted-impact-guidance, zero-effort-miss, body-fixed-anchor, imgui-window]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials]
game_types: [KSA.Celestial, KSA.Astronomical, KSA.IParentBody, KSA.Camera, KSA.Universe]
tags:
  - tail-kit
  - guided-bomb
  - predicted-impact
  - designation
  - ground-target
  - retarget
  - reach
  - zero-effort-miss
---
Outcome: a falling store (a bomb with a guided tail kit) lands on a place the player clicked on the ground. Its law is a closed-form zero-effort-miss: predict where the store lands if nothing steers it, ask for `-N * miss / t^2`, and solve for the two pushes square to the airflow that produce it. A click is then either a correction for the store already falling (if its fins can still reach the place) or the aim for the next release. KSArmory demonstrates both halves.[Guided bomb tail kit and sight](../../../../mods/ksarmory/features/guided-bomb-tail-kit-and-sight.md) [Ground target designation and reach](../../../../mods/ksarmory/features/ground-target-designation-and-reach.md)

Prerequisites: StarMap; a store you integrate yourself (position and velocity in the ecliptic, a gravity vector and its current drag acceleration); a cursor-to-ground pick and a body-fixed anchor, covered in [Anchor points to a rotating body](../../map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-1.md).

## Background: how the game does it

- **A place on the ground only stands still in the body's frame.** In the ecliptic it carries the planet's ~29.8 km/s orbital motion plus up to 465 m/s of spin. Store it as `conj(Celestial.GetBodyFixed2Ecl()) * (pointEcl - GetPositionEcl())` and re-read it every frame.[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174-L188)
- **Its velocity** is `Celestial.GetVelocityEcl()` plus the spin `GetAngularVelocity()` about the body-fixed +Z axis crossed with the offset. That axis is the parent pole, CCI +Z: `IParentBody.GetAngularVelocityCci()` is `(0, 0, GetAngularVelocity())`.[^ksa-iparentbody] [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L78-L90)
- **The clock.** Guidance integrates on `Universe.GetLastSimStep().DeltaTime`, and the samples are stamped at `Universe.GetElapsedTime()`, the end of that step.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2401)
- **Clicks.** `ImGui.GetIO().WantCaptureMouse` says the click landed on a window, not the world.[^brutal-imguiapi-imguiioptr] [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L140)

## Lifecycle and timing

| What | Lifecycle | Where |
|---|---|---|
| Click to designation | `frame:draw-ui` | `[StarMapAfterGui]`, after the panel draws, so a click on a window is not a click on the world |
| Resample the anchor, step the store, apply the tail-kit law per sub-step | `frame:draw-ui` (or `frame:celestials` when the HUD is hidden) | The mod's own round step, after `PrepareFrame` applied this frame's vehicle and celestial state |
| Reach solve | `frame:draw-ui` | Same pass, rate-limited on a stopwatch, only while a steerable store is falling |

Nothing writes KSA state; the store is not a KSA vehicle. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Turn a click on the ground into a body-anchored aimpoint.** The cursor ray is cast against terrain, and the hit is anchored to the nearest body:

```csharp
// src/KSArmory/Ksa/TargetLock.cs L67-L80
        // Anchored to the body rather than to the ecliptic, so it is still there a second later.
        // This is the half that lets a static target be watched at all -- a structure the engine
        // does not model is nothing any sensor reports, and an operator can still name the ground
        // it stands on.
        if (KsaWorld.TryCursorGroundPoint(out double3 groundEcl, out double lat, out double lon,
                                          out string body)
            && KsaWorld.TryAnchorToGround(groundEcl, out object? handle, out double3 anchor))
        {
            Aimpoint at = Aimpoint.OnGround(handle!, anchor, groundEcl, Vec.Zero);
            string what = $"{body} {lat:F2}, {lon:F2}";

            designate?.Invoke(at, what);
            head?.Designate(at, what);
        }
```

2. **Decide whether the click corrects the falling store or aims the next one.** Fly the store's reachable footprint first (a few flown predictions with the same law), and retarget only if the place is inside it; otherwise the designation stands for the next release and the falling store keeps its aim:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L263-L290
    public void SendStoresInTheAir(string what, IProjectile? steerable, bool asInstrument = false)
    {
        // Nothing to send them at. Designating nothing is not how a store is recalled -- see
        // ClearDesignation, which deliberately leaves one already steering alone.
        if (Designation.Kind == AimpointKind.None) return;

        foreach (IProjectile round in _rounds)
        {
            if (round.State != RoundState.Flying || !round.Munition.SteersItsFall) continue;
            if (!asInstrument && !ReferenceEquals(round, steerable)) continue;

            // Flown before the write, because the region is measured around where the store comes
            // down untouched and the aimpoint it is about to carry says nothing about that.
            TailKitReach reach = StoreReach.SolveNow(this, round);

            if (!asInstrument && !StoreRetarget.Reaches(reach, Designation.PositionEcl))
            {
                Announce($"{RoundLabel.For(round.Tube)} keeps its aim: {what} is "
                         + reach.Describe(Designation.PositionEcl));
                continue;
            }

            round.Retarget(Designation);
            Announce($"{RoundLabel.For(round.Tube)} now steering at {what} - "
                     + reach.Describe(Designation.PositionEcl));
        }
    }
```

3. **Resample the ground aimpoint every frame** before the store reads it, from the body-fixed anchor to a fresh ecliptic position and ground velocity:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L3585-L3599
    private TargetState? SampleTarget(IProjectile round)
    {
        // A place on a body has to be re-read every frame. Held as the coordinate it was when it
        // was designated, it is left behind by the planet at ~29.8 km/s - and the round then reads
        // that whole frame velocity as closing speed and turns hard across it.
        if (round.Aimpoint.Kind == AimpointKind.Ground)
        {
            if (KsaWorld.TryGroundAnchorEcl(round.Aimpoint.Handle, round.Aimpoint.Anchor,
                                            out double3 groundEcl, out double3 groundVel))
            {
                round.Aimpoint = round.Aimpoint.Resampled(groundEcl, groundVel);
            }

            return round.Aimpoint.ToTargetState();
        }
```

4. **Back-date the sample and difference both terms** inside each sub-step, so the planet's motion cancels and the aim is at the sub-step's own instant:

```csharp
// src/KSArmory/Sim/Slug.cs L607-L615
        SteeringCommandEcl = Vec.Zero;
        if (munition.SteersItsFall && target is { } aim)
        {
            double3 aimPos = aim.PositionEcl + aim.VelocityEcl * (elapsedInFrame - frameSeconds);

            SteeringCommandEcl = TailKit.Command(aimPos - PositionEcl, aim.VelocityEcl - VelocityEcl,
                                                 localVelocity, gravity, -drag, munition);
            accel += SteeringCommandEcl;
        }
```

5. **Predict the landing and the miss.** With constant acceleration `a0 = gravity + drag now`, solve the vertical fall to the aim's height for `t`, give up if the store is below the aim or not descending at arrival, and form the wanted correction `-NavConstant * miss / t^2`:

```csharp
// src/KSArmory/Sim/TailKit.cs L15-L43
    public static double3 Command(double3 toAim, double3 aimVelocityMinusRound, double3 localVelocity,
                                  double3 gravity, double3 dragAccel, MunitionProfile munition)
    {
        double g = Vec.Len(gravity);
        if (!(g > 1e-6) || !Vec.IsFinite(toAim) || !Vec.IsFinite(aimVelocityMinusRound)) return Vec.Zero;

        double3 up = gravity / -g;
        double3 p = -toAim;
        double3 u = -aimVelocityMinusRound;
        double3 a0 = gravity + (Vec.IsFinite(dragAccel) ? dragAccel : Vec.Zero);

        double h = Vec.Dot(p, up);
        if (h <= 0.0) return Vec.Zero;

        double vz = Vec.Dot(u, up);
        double az = Vec.Dot(a0, up);
        double disc = (vz * vz) - (2.0 * az * h);
        if (disc < 0.0) return Vec.Zero;

        double denom = -vz + Math.Sqrt(disc);
        if (denom <= 1e-9) return Vec.Zero;

        double t = 2.0 * h / denom;
        double3 miss = p + (u * t) + (a0 * (0.5 * t * t));
        double3 arriving = u + (a0 * t);
        double arrivingUp = Vec.Dot(arriving, up);
        if (arrivingUp >= -1e-6) return Vec.Zero;

        double3 wanted = miss * (-munition.NavConstant / (t * t));
```

6. **Solve for the pushes square to the airflow.** A push moves the landing sideways by itself and along the arrival direction by however much it lengthens or shortens the fall; a regularised 2x2 least squares picks the pushes, clamped to the fin authority:

```csharp
// src/KSArmory/Sim/TailKit.cs L45-L72
        double3 along = Vec.Unit(localVelocity);
        double3 e1 = Vec.AnyPerpendicular(along.Equals(Vec.Zero) ? up : along);
        double3 e2 = Vec.Unit(Vec.Cross(along.Equals(Vec.Zero) ? up : along, e1));

        double3 m1 = Moves(e1);
        double3 m2 = Moves(e2);

        double a11 = Vec.Dot(m1, m1);
        double a12 = Vec.Dot(m1, m2);
        double a22 = Vec.Dot(m2, m2);
        double reg = (1e-6 * (a11 + a22)) + 1e-12;
        a11 += reg;
        a22 += reg;

        double det = (a11 * a22) - (a12 * a12);
        if (!(det > 0.0)) return Vec.Zero;

        double b1 = Vec.Dot(m1, wanted);
        double b2 = Vec.Dot(m2, wanted);
        double x1 = ((b1 * a22) - (b2 * a12)) / det;
        double x2 = ((a11 * b2) - (a12 * b1)) / det;

        double3 command = (e1 * x1) + (e2 * x2);
        return Vec.IsFinite(command) ? Vec.ClampLength(command, munition.MaxLateralAccel) : Vec.Zero;

        // Where a push moves the landing: sideways by itself, and along the arrival for however
        // much it lengthens or shortens the fall.
        double3 Moves(double3 push) => push - (arriving * (Vec.Dot(push, up) / arrivingUp));
```

7. **Draw the remaining reach** so the player can see which clicks are corrections. The reach is flown, not closed-form: a lateral push settles against drag, so displacement grows roughly as `t` rather than `t^2`. Solve it only while a steerable store is in the air, at a fraction of the draw rate.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/TailKit.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TailKit.cs) | Predicted-impact law |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L593-L615) | Where it is applied per sub-step |
| [src/KSArmory/Ksa/TargetLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TargetLock.cs#L67-L80) | Click to body-anchored designation |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L263-L290) | Correction or next aim |
| [src/KSArmory/Sim/StoreRetarget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StoreRetarget.cs) | `Reaches`: an unknown reach makes no claim |
| [src/KSArmory/Ksa/StoreReach.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/StoreReach.cs) | Rate-limited flown reach |
| [src/KSArmory/Sim/TailKitReach.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TailKitReach.cs) | Flies the footprint with the same law |

## Variations and gotchas

- **Why not proportional navigation here.** A store released from a climb flies away from where it lands, and a line-of-sight law reads that as a target it is losing; the Slug comment records 125 m of miss from that.
- **Difference both terms in the law's frame.** Steering on `VelocityEcl` alone reads the planet's ~29.8 km/s as closing speed; KSArmory pins it with a test.
- **The kit cannot follow a mover.** The aim is a place fixed when designated; a moving craft is missed, and a few tenths of a g corrects a release error of a few hundred metres rather than gliding.
- **Arm a tool, do not press a button.** A button samples the cursor while it is over the button.
- **Hidden HUD.** `[StarMapAfterGui]` does not fire when `Program.DrawUI` is false; step from a prefix on `Program.OnFrameCelestials` and handle clicks there too.
- **For lift-steered vehicles**, swap the closed-form fall for a drag-integrated prediction and a measured Jacobian ([approach 2](steer-by-zero-effort-miss-2.md)).

## See also

- [Overview](overview.md); [ZEM on a predicted impact point](steer-by-zero-effort-miss-2.md)
- [Predict where a released store lands](../predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-2.md) (the sight that shows where an unsteered store lands)
- [Anchor points to a rotating body and pick bodies under the cursor](../../map-view/anchor-and-pick-points-on-bodies/overview.md)
- [Draw a predicted trajectory and markers in the map view](../../map-view/draw-a-predicted-trajectory-and-map-markers/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
