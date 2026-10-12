---
type: Mod Feature
title: Guided bomb tail kit and a flown bomb sight
description: A falling store steers by predicting its own landing and pushing square to its airflow to move that landing onto a ground designation, while the bomb sight flies a throwaway copy of the same round in a ground-carried frame and draws the arc and impact ring
mod: ksarmory
category: flight-and-orbits
techniques:
  - predicted-impact-guidance
  - flown-prediction
  - ground-carried-frame
  - imgui-drawlist
  - rate-limited-solve
  - platform-relative-cache
hooks:
  - "starmap StarMapAfterGui (via StepOnce: store steering and sight solve; then the sight draw)"
  - harmony-prefix KSA.Program.OnFrameCelestials (via StepOnce, HUD hidden)
  - harmony-prefix KSA.SunbloomRenderer.Render (impact ring painted by the ring pass)
lifecycle: [frame:draw-ui, frame:celestials, render:bloom, render:imgui]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.Program
  - KSA.Camera
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
tags: [guidance, tail-kit, bomb-sight, pipper, ballistics, rotating-frame, imgui-drawlist, ground-ring]
---

A bomb rack in KSArmory gets two things: a **pipper** (an orange ring on the ground where a store
released now would land, with the arc down to it) and, for a store fitted with a guided tail kit,
**steering onto a designated place on the ground** after release.[^ksarmory-bombsightoverlay][^ksarmory-tailkit]
The shipped example is the B61-pattern store on its rack, with `Guidance = GuidanceMode.Inertial`,
`NavConstant = 3` and 0.4 g of fin authority, and a "Mk 9 bombsight" sensor profile that gives the
rack something to name what is ahead.[^ksarmory-arsenal]

Both answers are **flown, not solved**. The tail kit predicts the store's own landing under
gravity and current drag and pushes to move that landing onto the aim point; the sight steps a
throwaway copy of the same round, through the same gravity, air density and terrain, until the
ground stops it. A closed form exists only without drag, and the mod's position is that a sight
derived from a tidier model than the round flies is a sight that is wrong when it matters.[^ksarmory-bombsight][^ksarmory-claude-md]

## How it works

### The tail kit law (`TailKit.Command`)

Called once per round sub-step from `Slug.Update` when `munition.SteersItsFall` (true only for
`GuidanceMode.Inertial`) and the round has a target.[^ksarmory-slug][^ksarmory-munitionprofile]

1. Work in the round's frame: `p = -toAim`, `u = -(aim velocity - round velocity)`, and a constant
   acceleration `a0 = gravity + drag now`, all projected on local up (gravity's opposite).[^ksarmory-tailkit]
2. Solve the vertical fall `h + vz t + az t^2 / 2 = 0` for the time `t` to the aim's height; give
   up (zero command) if the store is below it, cannot reach it, or is not descending at arrival.
3. Predicted miss at that time: `miss = p + u t + a0 t^2 / 2`; wanted correction
   `-NavConstant * miss / t^2`.
4. Build two directions square to the airflow (`localVelocity`); for each, `Moves(push)` is how far
   that push moves the landing: sideways by itself, plus along the arrival for however much it
   lengthens or shortens the fall.
5. Solve the 2x2 least-squares system (with a tiny regulariser) for the pushes that best produce
   the wanted correction, and clamp the result to `MaxLateralAccel` (`MaxLateralG * 9.80665`).[^ksarmory-tailkit][^ksarmory-munitionprofile]

The mod chose this over proportional navigation because a store released from a climb flies away
from where it lands, and a line-of-sight law reads that as a target it is losing; its comment
records 125 m of miss from that.[^ksarmory-slug] The aim's position is back-dated to the sub-step's
instant and both terms are differenced (aim minus round), so the planet's ~29.8 km/s of ecliptic
motion never reaches the steering.[^ksarmory-slug] The command is also exposed as
`SteeringCommandEcl`, which `WeaponSystem` rotates into the round body's frame through
`Vehicle.Asmb2Ego` and splits into per-blade deflections (`FinMixer`) so the four tail blades are
drawn deflecting; the fins are presentation only.[^ksarmory-weaponsystem][^ksarmory-finmixer][^ksa-vehicle]

### The sight (`BombSight.TryPredict`, `BombSightOverlay`)

| Piece | Behaviour |
|---|---|
| Release state | `WeaponSystem.TryNextReleaseEcl` (the next tube's launch position and velocity); velocity is taken **over the ground**: minus `KsaWorld.GroundVelocityAt`, which is the body's `GetVelocityEcl()` plus `GetAngularVelocityCce() x r`[^ksarmory-bombsightoverlay][^ksarmory-weaponsystem][^ksarmory-ksaworld] |
| Carried frame | Flown in a frame riding with the ground under the release: `Drift(t) = spin t + A t^2 / 2`, where `A = w x (w x r)` (`GroundAccelerationAt`) is taken off gravity; gravity and density are sampled where the round is relative to the drifted centre[^ksarmory-bombsight][^ksarmory-ksaworld] |
| Terrain | `CarriedGround` shifts only the centre by the drift, so the height comes from the ground under the release rather than kilometres upwind; `CoarseGroundTest` skips lookups until within 600 m of the cached surface or 250 m from the last sample[^ksarmory-bombsight][^ksarmory-coarsegroundtest] |
| Integration | A `Slug` with the real `MunitionProfile`, `MaxSteps = 2048` steps of 0.05 s (0.30 s under a parachute), at age zero for the pipper (the reach solve passes a falling store's age); only a round the ground stopped (or burst at fuse height) has an impact[^ksarmory-bombsight][^ksarmory-bombsightoverlay] |
| Landing | The impact is carried back by the ground-velocity difference times the fall time, so the ring marks the ground that will be under the store[^ksarmory-bombsight] |
| Rate | Re-solved every 0.2 s of wall clock, 2 s after an unreachable answer; a failed solve keeps the last good one[^ksarmory-bombsightoverlay] |
| Storage | Arc and impact are held as offsets from the platform sample they were solved against, not as ecliptic points, so a quarter-second-old answer translates with the aircraft instead of drifting by the planet's motion[^ksarmory-bombsightoverlay] |
| Drawing | `KsaWorld.BeginDraw(platform, PlatformEcl)` anchors to the camera's view of the craft; the arc is projected point by point and drawn twice on `ImGui.GetBackgroundDrawList()` (4 px dark, 2 px amber), breaking where a point is behind the camera; the impact goes to `GroundRings.Add` with the store's lethal radius[^ksarmory-bombsightoverlay][^ksarmory-groundrings] |

Powered rounds get no pipper, a craft whose ballistic computer is armed gets none, and only the
craft the panel is focused on solves and draws one; a tail-kit store does get one, because
undesignated it falls ballistically and the ring is its release cue.[^ksarmory-bombsightoverlay][^ksarmory-mod]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (Program.cs L2268) -> `StepOnce` -> `StepSimulation` | Rounds stepped (tail kit per sub-step); then `SightFor(e.Weapon).Update` after the effects loop (KSArmoryMod.cs L884-L900)[^ksarmory-mod][^ksa-program] |
| `frame:draw-ui` | Same hook, after `StepOnce` and the panel: `GroundRings.BeginFrame()` then `BombSightOverlay.Draw` (KSArmoryMod.cs L537-L553) | Arc onto the background draw list; ring queued |
| `frame:celestials` | HUD hidden: `StepOnce` from the prefix on `Program.OnFrameCelestials` (L2320) | Steering and sight solve continue; no draw |
| `render:bloom` | The ring pass recorded from the prefix on `SunbloomRenderer.Render` | Impact ring painted on the depth buffer |
| `render:imgui` | ImGui's draw data rendered at the end of `RenderGame` | The arc |

The steering runs on the main thread inside the mod's own round step, not in KSA's vehicle
solver, because rounds are not KSA vehicles; the sample of the aim it reads is resampled each frame
for the same reason.[^ksarmory-slug] The sight is rate-limited on wall clock because a solve is a
computation budget: under timewarp a simulated interval passes every frame and a limiter built on
it would stop limiting.[^ksarmory-bombsightoverlay] Solving and drawing in the same hook pairs the
offsets with the platform sample `BeginDraw` anchors to, which is what keeps the arc on the
aircraft.[^ksarmory-bombsightoverlay][^ksarmory-ksaworld] The scripted `drop` scenario measures
the whole chain in game: it flies the sight off the rack and off the state the store actually left
with, and for a guided store designates the ring at release, so its miss from the ring is the tail
kit's.[^ksarmory-dropscenario]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/TailKit.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TailKit.cs) | Predicted-impact steering law |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L593-L615) | Where the command is applied per sub-step |
| [src/KSArmory/Sim/BombSight.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BombSight.cs#L61-L161) | `TryPredict`, `CarriedFrame`, `CarriedGround` |
| [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L87-L215) | Rate-limited solve, release state over the ground, platform-relative cache |
| [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L221-L286) | Arc on the background draw list, ring |
| [src/KSArmory/Sim/CoarseGroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CoarseGroundTest.cs) | Terrain lookups only near the ground |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1995-L2087) | Ground velocity and acceleration, body velocity |
| [src/KSArmory/Sim/Arsenal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Arsenal.cs#L327-L418) | The B61-pattern store's tail-kit settings |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L870-L900) | Per-weapon solve after the round step |
| [src/KSArmory/Ksa/DropScenario.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/DropScenario.cs#L823-L843) | Scripted drop scored against the sight; a guided drop designates the ring |
| [tests/KSArmory.Tests/TailKitGuidanceTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/TailKitGuidanceTests.cs) | Steering, differencing, authority and no-kit regression tests |
| [tests/KSArmory.Tests/BombSightSpinTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/BombSightSpinTests.cs) | The ring sits on the ground the store strikes on a spinning body |

## Snippets

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

```csharp
// src/KSArmory/Sim/BombSight.cs L84-L116
        var frame = new CarriedFrame(groundVelocityEcl - bodyVelocityEcl, groundAccelerationEcl);
        var carried = new CarriedGround(ground, frame);
        // ...
        Slug shot = new(releaseEcl, velocityOverGround, null, 0, releaseEcl, Vec.Zero)
        {
            Age = Math.Max(startAge, 0.0),
            Munition = munition,
            Ground = carried,
            GravityAt = (p, intoStep) => gravityAt(p + frame.Drift(carried.Seconds + intoStep)) - groundAccelerationEcl,
            AirDensityAt = (p, intoStep) => densityAt(p + frame.Drift(carried.Seconds + intoStep)),
        };

        pathEcl.Add(releaseEcl);
        // ...
        TargetState? aim = steerAtEcl is { } steer ? new TargetState(steer, Vec.Zero, 0.0) : null;

        for (int i = 0; i < MaxSteps && shot.State == RoundState.Flying; i++)
        {
            carried.Seconds += stepSeconds;

            double3 at = shot.PositionEcl + frame.Drift(carried.Seconds);
            shot.Update(stepSeconds, aim, gravityAt(at) - groundAccelerationEcl, Vec.Zero,
                        releaseEcl, munition, densityAt(at));

            pathEcl.Add(shot.PositionEcl);
        }
```

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L221-L245
    private void DrawArcOnScreen(double3 here, int stride)
    {
        _screen.Clear();

        for (int i = 0; i < _path.Count; i += stride) AddScreenPoint(here + _path[i]);
        AddScreenPoint(here + _path[^1]);

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        for (int pass = 0; pass < 2; pass++)
        {
            uint colour = pass == 0 ? ArcEdge : ArcInk;
            float width = pass == 0 ? 4f : 2f;

            for (int i = 1; i < _screen.Count; i++)
            {
                if (_screen[i - 1] is { } a && _screen[i] is { } b) draw.AddLine(a, b, colour, width);
            }
        }
    }

    // A point behind the camera breaks the line there rather than folding it across the screen.
    private void AddScreenPoint(double3 pointEcl)
        => _screen.Add(KsaWorld.TryEclToEgo(pointEcl, out double3 ego) && KsaWorld.TryProjectEgo(ego, out float2 at)
                           ? at
                           : null);
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `IParentBody.GetAngularVelocityCce` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L81) | Spin for the ground velocity and the carried frame's acceleration[^ksa-iparentbody] |
| `Celestial.GetVelocityEcl`, `GetPositionEcl` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L386-L409) | Body motion and centre[^ksa-celestial] |
| `Vehicle.Parent`, `Vehicle.Asmb2Ego` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L373) | The platform's body; world-to-part rotation for fin deflection (L502) |
| `Program.GetRenderCamera`, `GetMainCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L641) | The camera `BeginDraw` anchors to |
| `Camera.GetPositionEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236) | Where the engine draws the platform, the arc's anchor[^ksa-camera] |
| `ImGui.GetBackgroundDrawList`, `ImDrawListPtr.AddLine`, `ImGui.ColorConvertFloat4ToU32` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033) | The arc under all windows[^ksa-imgui] |

## Pitfalls and version notes

- **Fly the sight in the ground's frame.** Integrated in ecliptic velocities, the round moves
  ~1.5 km per step against a planet sampled where it is now, reads as underground after one step,
  and the pipper lands kilometres out, fixed to the ecliptic.[^ksarmory-bombsightoverlay]
- **But read terrain at the release ground, not the carried point.** The author's flown numbers
  over 150 m ridges: holding the planet still put the ring 36.5 m (east) and 16.4 m (north) from a
  5 km drop's impact, reading terrain upwind 26.7 m and 190.6 m, the carried frame 0.6 and 0.7 m;
  `BombSightSpinTests` pins it.[^ksarmory-claude-md][^ksarmory-bombsight-tests]
- **Step versus refresh.** The 0.05 s integration step and the 0.2 s refresh are separate numbers;
  at terminal velocity a coarse step quantises the impact to ~55 m and the ring hops.[^ksarmory-bombsightoverlay]
- **Horizon.** `MaxSteps x 0.05 s` is about 102 s of fall; a store released higher (the Mk 21 reentry
  vehicle) has no pipper, and the solve backs off to every 2 s rather than re-flying 2048 steps.[^ksarmory-bombsightoverlay]
- **The sight ignores the kit.** It flies an undesignated round on purpose ("where this lands if you
  designate nothing"); what a steered store can still reach is a separate flown solve.[^ksarmory-munitionprofile]
- **The kit cannot follow.** It flies at a point fixed when designated (a ground aim keeps the
  planet's motion through its velocity); a moving target is missed, and 0.4 g corrects a release
  error of a few hundred metres rather than gliding.[^ksarmory-munitionprofile][^ksarmory-arsenal]
- **Differencing is load-bearing.** Steering on `VelocityEcl` alone reads the planet's ~29.8 km/s as
  closing speed; `TailKitGuidanceTests.TheCommandIsDifferencedAgainstTheAimpointsOwnMotion` guards it.[^ksarmory-slug][^ksarmory-tailkit-tests]

## See also

- [Ground target designation and reach](./ground-target-designation-and-reach.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Terrain ground impact test](./terrain-ground-impact-test.md)
- [Proportional navigation guidance](./proportional-navigation-guidance.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Predict where a released store lands by flying a throwaway round in a ground-carried frame](../../../howto/flight-and-orbits/guidance/predict-impact-point-and-build-a-ballistic-computer/predict-impact-point-and-build-a-ballistic-computer-2.md) - how-to article demonstrating this feature
- [Rate-limit a solve on the wall clock and cache results per object with real and simulated time limits](../../../howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-5.md) - how-to article demonstrating this feature
- [Steer a falling store by its predicted impact and take a ground designation as its input](../../../howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-3.md) - how-to article demonstrating this feature
- [Draw a flown prediction as an ImGui polyline on the background draw list, anchored to the craft and broken behind the camera](../../../howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-3.md) - how-to article demonstrating this feature

[^ksarmory-tailkit]: src/KSArmory/Sim/TailKit.cs
[^ksarmory-bombsight]: src/KSArmory/Sim/BombSight.cs
[^ksarmory-bombsightoverlay]: src/KSArmory/Ksa/BombSightOverlay.cs
[^ksarmory-slug]: src/KSArmory/Sim/Slug.cs
[^ksarmory-munitionprofile]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-coarsegroundtest]: src/KSArmory/Sim/CoarseGroundTest.cs
[^ksarmory-finmixer]: src/KSArmory/Sim/FinMixer.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-groundrings]: src/KSArmory/Ksa/GroundRings.cs
[^ksarmory-dropscenario]: src/KSArmory/Ksa/DropScenario.cs
[^ksarmory-tailkit-tests]: tests/KSArmory.Tests/TailKitGuidanceTests.cs
[^ksarmory-bombsight-tests]: tests/KSArmory.Tests/BombSightSpinTests.cs
[^ksarmory-claude-md]: CLAUDE.md (author notes)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
