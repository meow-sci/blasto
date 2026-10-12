---
type: Mod Feature
title: One ecliptic frame and one epoch for everything simulated and drawn
description: All mod state lives in KSA's ecliptic frame at Universe.GetElapsedTime(), targets are back-dated to the round's sub-step instant, and drawing goes through the engine's own Camera.GetPositionEgo so overlays and bursts land where the craft is rendered
mod: ksarmory
category: physics
techniques: [custom-physics, draw-anchor, epoch-backdating, frame-conversion]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.CelestialSystem
  - KSA.IParentBody
  - KSA.Camera
  - KSA.StateVectors
  - KSA.Orbit
  - KSA.CelestialUpdateTask
  - KSA.PhysicsBubble
  - KSA.KinematicStates
  - KSA.Universe
tags: [coordinate-frames, ecliptic, ego, epoch, sampling, drawing, camera, physics-bubble]
---

Near Earth every ecliptic position carries about 29.8 km/s of the planet's orbital motion, which is
roughly 500 m per frame at 60 fps. Any two positions differenced across even part of a step leak a
slice of that, and the leak shows up as jitter, a constant offset, a guidance error or a drift.
KSArmory's answer is a small set of rules applied everywhere: simulate in KSA's ecliptic frame
(Ecl), treat every engine sample as belonging to one instant (the end of the step just applied),
move a sample to another instant only with an explicit carry, and draw against the position KSA
renders a craft at rather than its analytic position.[^ksarmory-frames-doc]

## How it works

Frames in play:

| Frame | Where it comes from | Mod use |
|---|---|---|
| Ecl | `Vehicle.GetPositionEcl()` / `Celestial.GetPositionEcl()`: cached `_positionEcl`, composed parent-first from orbit state vectors | All simulation: rounds, targets, gravity, blasts |
| Ego | Camera-relative render frame; `Camera.EclToEgo(p) = p - camera.PositionEcl`, `EgoToEcl` the inverse | Drawing, cursor rays, handing drawn points back to engine effects |
| drawn Ego of a craft | `Camera.GetPositionEgo(v)`: `-PositionCce` for the followed craft, bubble physics positions for craft in its physics bubble, otherwise `GetPositionEcl() - camera.PositionEcl` | Anchoring overlays and bursts |
| Cci / Cce / Asmb | Body-centred inertial / ecliptic-aligned / craft assembly | Attitude commands and shoves (see [PrepareWorker window](./prepareworker-prefix-command-window.md)) |

Rules, as code:

1. **Draw anchor.** `KsaWorld.BeginDraw(platform, system.PlatformEcl)` builds
   `DrawAnchor(camera.GetPositionEgo(platform), platformEcl)`, and every overlay point goes through
   `ToEgo(posEcl) = Ego + (posEcl - Ecl)`. The Ecl term is the platform sample the geometry was
   measured against (`WeaponSystem.SampleWorld`), so the ecliptic carrier cancels; the Ego term is
   where KSA actually draws the platform, so a physics-bubble offset does not. `KsaWorld.BeginFrame`
   clears the anchor each step, so a pass that forgets `BeginDraw` draws nothing instead of drawing
   500 m off.[^ksarmory-drawanchor][^ksarmory-ksaworld]
2. **Drawn position of a craft**: `TryVehicleEgo` asks `camera.GetPositionEgo(v)` on the render
   camera (`Program.GetRenderCamera() ?? GetMainCamera()`), falling back to the anchored Ecl conversion.
3. **Drawn point to Ecl**: `TryEgoToEcl` is `camera.EgoToEcl(ego)` on the main camera, for systems
   that take Ecl and place themselves (KSA explosions, particle emitters). `WeaponSystem.DrawnBurstEcl`
   takes the platform's drawn Ego plus the round's offset at the burst instant
   (`DrawAnchor.OffsetAtBurst`) and converts back; it logs when that differs by more than 1 m from the
   analytic burst carried to the step's end with `InFrame.AtSample`.[^ksarmory-weaponsystem][^ksarmory-inframe]
4. **Cursor ray**: `TryCursorRayEcl` builds the ray in Ego with `Camera.ScreenToEgoRay` and converts
   only the origin (`camera.EgoToEcl(0)`); a direction is the same in both frames because Ego is a pure
   translation of Ecl.
5. **Back-date targets.** Engine samples are end-of-step; a round being sub-stepped is at
   `elapsedInFrame` seconds into the step (`-frameSeconds` at its start). `Interceptor` and `Slug`
   aim at `t.PositionEcl + t.VelocityEcl * (elapsedInFrame - frameSeconds)`; without it proportional
   navigation flies a clean intercept on a ghost one frame of ecliptic motion away (450-680 m).[^ksarmory-interceptor][^ksarmory-slug]
6. **Rounds as targets are sampled once.** `CollectAirborne` publishes every round before any
   system steps, carried forward by `VelocityEcl * step` so it sits at the same end-of-step phase as
   a vehicle sample, whichever system reads it first.[^ksarmory-mod]

## Lifecycle and timing

Every Ecl sample the mod reads is written in `Program.PrepareFrame` and nowhere else, before any
mod hook:[^ksa-universe]

| Step (build 5554) | Where | Effect |
|---|---|---|
| Orbit results applied | `Universe.ApplyOrbitSolvers` (Universe.cs L1905-L1920), Program.cs L2164 -> `Celestial.UpdateFromTaskResults` (Celestial.cs L1529-L1535) | Each celestial's `Orbit` takes state vectors the worker evaluated at `_simStep.NextTime` (CelestialUpdateTask.cs L52-L64) |
| Vehicle results applied | `ApplyVehicleSolvers` L1947 -> `BubbleApplyResultsJob` -> `PhysicsBubble.ApplyResultsToVehicles` (PhysicsBubble.cs L881-L909) -> `Vehicle.UpdateFromTaskResultsUnsynchronized` | `_kinematicStates` (Vehicle.cs L2441) and `Orbit.UpdatePosition` (L2468), integrated to `SimStep.NextTime` (PhysicsBubble.cs L1068, L1305-L1349) |
| Clock advanced | `_lastSimStep = _nextSimStep` (Universe.cs L1948) | `GetElapsedTime()` becomes that `NextTime` |
| Ecl caches rebuilt | `CurrentSystem.UpdatePerFrameData()` (L1950) -> `CelestialSystem.UpdatePerFrameData` (L274-L285) -> `IParentBody.UpdatePerFrameDataTree` (L110-L124), parent first | `Celestial.UpdatePerFrameData` (L591-L606), `Vehicle.UpdatePerFrameData` (L2645-L2651) |

So in `[StarMapAfterGui]` (Program.cs L2268) or the `OnFrameCelestials` prefix (L2320) every
vehicle and celestial position is at `Universe.GetElapsedTime()`, the end of the step just applied,
and nothing moves until next frame's `PrepareFrame`.[^ksa-celestialsystem][^ksa-iparentbody][^ksa-celestial][^ksa-vehicle]
The mod's rounds, integrated across that same step inside the hook, start at the previous
`NextTime`; that one-step phase is what rules 5 and 6 handle. The camera's drawn positions
(`GetPositionEgo`, Camera.cs L236-L250) are computed live from stored positions and the camera's
current follow offset, so they agree with what `Render` will draw this frame provided nothing
moves the camera between the read and the render.[^ksa-camera]
Hooks that run after the render (`[StarMapAfterOnFrame]`) see the same epoch, but anything they
place is drawn next frame against craft that have moved on, which is why the mod both steps and
draws in the pre-render hooks ([dual-hook step](./dual-hook-simulation-step.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DrawAnchor.cs) | `ToEgo`, `OffsetAtBurst` |
| [src/KSArmory/Sim/InFrame.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/InFrame.cs) | `AtBurst` / `AtSample`: one function per direction of carry |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3409-L3540) | `BeginDraw`, `TryEclToEgo`, `TryVehicleEgo` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1791-L1815) | `TryEgoToEcl` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4113-L4152) | `TryCursorRayEcl` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L4002-L4022) | `DrawnBurstEcl` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1285-L1338) | `CollectAirborne` / `AddAirborne` carry |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L465-L480) | Target back-date |

## Snippets

```csharp
// src/KSArmory/Sim/DrawAnchor.cs L29-L54
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

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Vehicle.GetPositionEcl`, `GetVelocityEcl`, `UpdatePerFrameData`, `Orbit` (`=> Patch.Orbit`) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2679) | Analytic Ecl samples and their composition |
| `Celestial.GetPositionEcl`, `UpdatePerFrameData`, `UpdateFromTaskResults` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L606) | Body positions at the same epoch |
| `Camera.GetPositionEgo`, `EclToEgo`, `EgoToEcl`, `ScreenToEgoRay`, `PositionEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L250) | Drawn positions and frame conversion |
| `Universe.GetElapsedTime`, `ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) | The epoch and where it is set |
| `CelestialUpdateTask` | [current/decomp/KSA/CelestialUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialUpdateTask.cs#L52-L64) | Orbits evaluated at `NextTime`; skipped for a zero step[^ksa-celestialupdatetask] |
| `StateVectors.StateTime`, `Orbit.StateVectors` | [current/decomp/KSA/StateVectors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StateVectors.cs#L8) | Per-body sample timestamp, checkable against `GetElapsedTime()`[^ksa-statevectors][^ksa-orbit] |
| `PhysicsBubble.ApplyResultsToVehicles` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L881-L909) | Physics results copied onto vehicles[^ksa-physicsbubble] |

## Pitfalls and version notes

- **`GetPositionEcl` is not where a craft is drawn** when the camera follows a craft in its physics
  bubble: KSA draws those from `KinematicStates.PositionPhys` (Camera.cs L242-L247). Lines drawn to
  the analytic position visibly miss; anchor to `GetPositionEgo`.
- **Do not collapse the anchor's two samples into one.** Re-reading the platform Ecl at draw time,
  or deriving Ego from Ecl, both look tidier and both move the overlay off the craft.
- **A carry belongs to its consumer.** `AddAirborne`'s `VelocityEcl * step` carry is right for
  contacts and was flown and lost when applied to the rounds' own force sampling (mod docs: miss
  782 m -> 2,667 m).[^ksarmory-frames-doc]
- **`KinematicStates` has no timestamp** (KinematicStates.cs L8-L16); its epoch is the same
  `GetElapsedTime()` by construction.[^ksa-kinematicstates]

`docs/KSA-FRAME-ORDER.md` (build 2026.8.22.5348) against build 2026.10.10.5554, epoch claims:[^ksarmory-frame-order]

| Doc claim | 5554 verdict |
|---|---|
| §2 `GetLastSimStep` / `GetElapsedTime` are one struct; `_lastSimStep` written only in `ApplyVehicleSolvers` and `DeserializeSave` | Holds (Universe.cs L1948, L2456; getters L2381-L2402) |
| §2 steps contiguous, `NextTime` rounded to ns, two-frame delay for a speed change | Holds (GetJobSimStep L2615-L2627; UniverseTime.cs L71-L88, L162-L165) |
| §3 celestials evaluated at `NextTime`, zero step skipped | Holds (CelestialUpdateTask.cs L52-L64); but `Celestial.PrepareWorker` is gone: `ExecuteNextOrbitSolvers` calls `IOrbitSolved.UpdateTask.Prepare(simStep)` directly (Universe.cs L2098-L2112) |
| §4 vehicles and celestials share one epoch via one parent-first walk | Holds (L1948-L1950; CelestialSystem.cs L274-L285; IParentBody.cs L110-L124; Vehicle.cs L2645-L2651) |
| §4 worker integrates to `NextTime`, results via `ApplyResultsToVehicles` -> `UpdateFromTaskResultsUnsynchronized` | Holds (PhysicsBubble.cs L1068, L881-L909; Vehicle.cs L2435-L2468) |
| §6 `DeserializeSave` rewrites the clock to a zero step | Holds (L2455-L2462); "this mod does not notice" is out of date: `ForgetTheWorld` resets `StepGate` |
| §8 `StateVectors.StateTime` public and honest | Holds (StateVectors.cs L8; Orbit.cs L1185; full-physics orbit rebuilt at the physics state's time, PhysicsBubble.cs L2902-L2918) |
| §8 `Camera.GetPositionEgo` blends nothing; no interpolation alpha | Holds (Camera.cs L236-L250) |
| §9 the three assumptions | All hold |

## See also

- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Proportional navigation guidance](./proportional-navigation-guidance.md)
- [Gizmo world overlays](./gizmo-world-overlays.md)
- [KSA explosions from a point](./ksa-explosions-from-a-point.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Use Ecl as the working frame, Ego for anything drawn, and cancel the 29.8 km/s carrier](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-2.md) - how-to article demonstrating this feature
- [Ecl state at GetElapsedTime with epoch back-dating](../../../howto/physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-4.md) - how-to article demonstrating this feature

[^ksarmory-drawanchor]: src/KSArmory/Sim/DrawAnchor.cs
[^ksarmory-inframe]: src/KSArmory/Sim/InFrame.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-slug]: src/KSArmory/Sim/Slug.cs
[^ksarmory-frames-doc]: docs/FRAMES-AND-EPOCHS.md
[^ksarmory-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled)
[^ksa-iparentbody]: KSA.IParentBody (decompiled)
[^ksa-celestialupdatetask]: KSA.CelestialUpdateTask (decompiled)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-statevectors]: KSA.StateVectors (decompiled)
[^ksa-orbit]: KSA.Orbit (decompiled)
[^ksa-kinematicstates]: KSA.KinematicStates (decompiled)
