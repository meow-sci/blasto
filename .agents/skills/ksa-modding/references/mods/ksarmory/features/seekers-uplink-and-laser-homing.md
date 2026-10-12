---
type: Mod Feature
title: Seekers, command uplink and laser designation
description: Guided rounds are broken by what they can see, so the mod models a seeker gimbal, decoy seduction judged once per decoy, a command uplink that dies with the launcher's track, anti-radiation homing with emission memory, and laser spots cast on meshes and terrain (not yet homed on)
mod: ksarmory
category: flight-and-orbits
techniques: [custom-physics, sim-boundary, engine-raycast, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.ActiveEnginePerformance
  - KSA.Part
  - KSA.PartTree
  - KSA.Ray
  - KSA.Celestial
  - KSA.Universe
  - KSA.Program
tags:
  - seeker
  - command-link
  - uplink
  - anti-radiation
  - countermeasures
  - decoys
  - chaff
  - flares
  - laser-designator
  - raycast
---

A guided round in KSArmory is only as good as what it can see, and each guidance mode has its own
way of losing. A heat seeker can be pulled onto a flare; a pulse-Doppler radar seeker ignores chaff
unless the target beams; an anti-radiation round homes on a transmitter and keeps flying at where
it last heard it; a command-linked round (the Pantsir's 57E6) carries no seeker at all and dies
with the launcher's track. All of it is decided per frame by plain C# around the mod's own rounds,
from samples of KSA vehicles.[^ksarmory-readme][^ksarmory-munition]

Laser designation is half-built at this ref: an optical head with a laser casts a beam against
craft meshes and terrain and publishes a coded spot every frame, but **no round homes on a laser
spot yet**. There is no laser `GuidanceMode`, and nothing reads `LaserDesignators.Active`.

## How it works

**Guidance modes and what defeats them** (`GuidanceMode`, `MunitionProfile`):[^ksarmory-munition][^ksarmory-arsenal]

| Mode | Shipped on | Sees | Defeated by |
|---|---|---|---|
| `Seeker` + `SeekerBand.Infrared` | AIM-9J (resistance 0.1, FOV 40) | Heat in its gimbal | Flares; leaving the gimbal (recoverable) |
| `Seeker` + `SeekerBand.Radar` | AIM-120C (resistance 0.3, Doppler gate 40 m/s, FOV 60) | Radar return in its gimbal | Chaff inside 150 m of the target while beaming |
| `AntiRadiation` | AGM-88 (FOV 70) | Only a craft whose sensor emits | Going silent and moving away from the remembered spot |
| `CommandLink` | 57E6 (default) | Whatever the launcher's sensor sees | Leaving the launcher's sensor volume; chaff breaking the set's track; the launcher dying |
| `Inertial` | B61 tail kit | A designated place | Nothing; it cannot follow anything that moves |

`Seducible` is true only for `Seeker` with a band other than `SeekerBand.None`, so anti-radiation and
command-link rounds are never decoyed directly.

**Per frame, per round** (`WeaponSystem.UpdateRounds`): `Seek(round, SampleTarget(round), dt)`
produces the `TargetState?` handed to `RoundDriver.Fly`. A null target means "nothing to steer or
fuse on".[^ksarmory-weaponsystem]

**`SampleTarget`** (WeaponSystem.cs L3585-L3678):

1. Ground aimpoint: re-resolved from its body-fixed anchor every frame; a fixed `Point` is returned as is.
2. A round aimed at another round reads it from the airborne census (`_incomingByHandle`), with
   `Emitting = false` (a round carries no transmitter).
3. A craft target: `KsaWorld.PositionEcl/VelocityEcl/MeanRadius`, and `Emitting` read live through
   `WeaponSystems.IsEmitting(craft)` (any system on that craft whose `Sensor.Emits` and is not set
   `RadarSilent`).[^ksarmory-weaponsystems]
4. **Uplink** (`Munition.NeedsUplink`, i.e. `CommandLink`): null when the platform is gone, when
   `ThreatModel.InSensorVolume` fails (range is the sensor's RCS-scaled detection range, plus the
   cone), or when `Radar.ChaffBroke(handle)` says chaff has the set's track.[^ksarmory-threatmodel][^ksarmory-radar]

**Losing the uplink is permanent for that round.** `Interceptor.Update` sets `TargetRef = null`
when handed a null target, and `SampleTarget` finds craft targets only through `TargetRef`
(nothing but `Retarget` restores it, and only tail-kit stores are retargeted). The proximity fuse
also sits inside `if (target is { } t)`, so a round with no target cannot burst on proximity; it
flies on until its age limit or the ground. A seeker that merely leaves its gimbal is different:
`SampleTarget` still returns the target, and `SeekerInView` is recomputed every sub-step.[^ksarmory-interceptor][^ksarmory-weaponsystem]

**Anti-radiation memory** (Interceptor.cs L491-L507). While `t.Emitting`, the round stores the
back-dated target position, its velocity and the round's own `Age`. When the set goes silent it
steers at `emissionPos + emissionVel * (Age - emissionAge)`: a position replayed with the velocity
it was seen with, so the remembered spot rides the planet rather than being left behind at
~30 km/s. A round that has never heard its target is `blind`: no steering, but the fuse still runs.[^ksarmory-interceptor]

**The seeker's side of decoys** (`SeekerLock`, one per seducible round, created the first frame
any decoy is live):[^ksarmory-seekerlock][^ksarmory-signature]

| Rule | Code |
|---|---|
| Strength | `Signature.LockStrength = signature / km^2` (IR) or `/ km^4` (radar), times `1 - (offBoresight/fov)^2`, zero outside the FOV |
| Epoch | Target and every decoy put back by `frameSeconds` of their own velocity before any line of sight is drawn |
| Candidates | Only decoys whose `Profile.Fools == Band`; radar only within `ChaffNotch.CellMetres` (150 m) of the target[^ksarmory-chaffnotch] |
| Threshold | Must beat `targetStrength * Margin` (1.0) and any decoy already taken this frame |
| Doppler gate | With `DopplerGateMps > 0`, the decoy's closing speed must be within the gate of the target's |
| Judged once | Each decoy id goes into a `HashSet` the first frame it qualifies; the roll is `random < 0.8 * (1 - CountermeasureResistance)` |
| Held | A taken seeker steers on the decoy until `Spent`; then back to the target if `ReacquiresAfterDecoy`, else `SeekerPick.Lost` (null target) |

`Steer` hands the round `TargetState(decoy position, decoy velocity, radius 0, decoy)`, so the round
both steers and fuses on the decoy, and `Detonate` marks it `seduced` so no hit is scored on the
intended target (the splash still judges whatever it burst beside). Judging once per decoy rather
than rolling every frame keeps a flare's success independent of frame rate.[^ksarmory-weaponsystem]

**Target heat.** `KsaWorld.HeatOf(vehicle)` = 1 kW/sr airframe + 0.04 kW/sr per kN of
`FlightComputer.ActiveEnginePerformanceMax.Thrust * GetManualThrottle()`, counted only while
`IsAnyEnginePropellantAvailable()`; a round reads 5 kW/sr burning, 0.3 coasting. Radar targets use
`RadarSignature.CrossSectionFor(radius)`. Decoys (`Decoy.cs`) rise linearly to a peak signature and
decay to nothing over their life, and fall through the air with semi-implicit drag.[^ksarmory-ksaworld][^ksarmory-signature][^ksarmory-decoy]

**Laser designation** (`OpticalHead.UpdateLaser`):[^ksarmory-opticalhead][^ksarmory-lasercode]

1. Only when the head has a laser (`OpticProfile.HasLaser`), the operator is lasing, the head is not
   `Masked` by its own craft (`LaserInhibited`), and the code passes `LaserCode.IsValid` (NATO
   1111 to 1788, digits 2 to 4 bounded).
2. Cast from the optic's eye along its view: `KsaWorld.TryRayCraftHit` (sphere broad phase, then
   `Part.RayCastEgo` per part with `Vehicle.GetMatrixAsmb2Ego(centre - eye)`, own craft excluded)
   and `KsaWorld.TryRayGround` (the mod's `TerrainRay` walk against each celestial). The nearer hit
   within `LaserRangeMetres` (20 km default) wins.[^ksa-part][^ksa-vehicle][^ksarmory-ksaworld]
3. Publish `LaserSpot(designator, code, position, velocity, range)`, where velocity is the struck
   craft's or the ground's there (`GroundVelocityAt(body, at)`), so a future consumer can replay it
   without losing it to the planet's motion; then `LaserDesignators.Add`.

`LaserDesignators.BeginFrame()` clears the list at the top of `OpticalHeads.Update`. The only
readers of a spot today are the gunner sight overlay (`Sight.cs`, "LASER" range and the spot), the
optic panel and the bridge telemetry.[^ksarmory-laserdesignators][^ksarmory-opticalheads][^ksarmory-sight]

## Lifecycle and timing

Everything runs inside `KSArmoryMod.StepSimulation`, which `StepOnce` runs from the first hook to
claim the frame: `[StarMapAfterGui]` (postfix on `OnDrawUiViewports`, inside `if (DrawUI)` at
Program.cs L2265-L2268), the `OnFrameCelestials` prefix (L2320) when the UI is hidden, or
`[StarMapAfterOnFrame]` (after L2332).[^ksarmory-mod][^ksa-program]

Order inside the clamped step (KSArmoryMod.cs L759-L795):

| Step | Code | Why there |
|---|---|---|
| 1 | `CollectAirborne(step)` | Every round in the world as a contact, carried to end-of-step |
| 2 | `_countermeasures.Update(step, _airborne)` | Decoys advanced to the end of the step before any seeker judges them |
| 3 | `WeaponSystem.Update`: `Radar.Scan` (chaff notch decided) then `UpdateRounds` (`SampleTarget`, `Seek`, `Fly`) | The uplink check sees this frame's notch verdict |
| 4 | `_roster.UpdateLoose(step, ...)` | Rounds of dead launchers; command-link rounds there get `Platform is null`, so no uplink |
| 5 | `_heads.Update(dtSim, ...)`: `LaserDesignators.BeginFrame`, then each head's `UpdateLaser` | Unclamped step; spots are published after the rounds have flown |

- **Epoch.** All craft samples are end-of-step: `ApplyVehicleSolvers` (Program.cs L2167) sets
  `_lastSimStep` and refreshes `_positionEcl` (Universe.cs L1948-L1950), and
  `GetElapsedTime() => _lastSimStep.NextTime` (L2399). `SeekerLock.Choose` and `Interceptor.Step`
  both back-date by the frame step for that reason.[^ksa-universe]
- **Concurrency.** The next vehicle step was queued at Program.cs L2220 and is running on workers.
  The seeker reads `FlightComputer.ActiveEnginePerformanceMax`, `GetManualThrottle()` and
  `IsAnyEnginePropellantAvailable()` as frame samples inside `try/catch` (falling back to airframe
  heat), and the laser casts `Part.RayCastEgo` on the main thread; nothing here writes KSA state.[^ksarmory-ksaworld]
- **A laser consumer would lag a frame.** Because heads update after the fire loop, anything that
  read `LaserDesignators.Active` from `UpdateRounds` would see the previous frame's spots, which
  are end-of-step positions of that earlier step.[^ksarmory-mod]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3585-L3729) | `SampleTarget` (uplink, emission), `Seek`, `SignatureOf` |
| [src/KSArmory/Sim/SeekerLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SeekerLock.cs) | Decoy judging, hold, reacquire or lost |
| [src/KSArmory/Sim/Signature.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Signature.cs) | Heat model, `LockStrength`, `ClosingSpeed` |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L482-L540) | Anti-radiation memory, blind state, seeker gimbal gate |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs#L1-L73) | `GuidanceMode`, `SeekerBand` |
| [src/KSArmory/Sim/MunitionProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MunitionProfile.cs#L340-L389) | `NeedsUplink`, `SeekerFovDeg`, `Band`, `CountermeasureResistance`, `ReacquiresAfterDecoy`, `DopplerGateMps`, `Seducible` |
| [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L139-L160) | `InSensorVolume` (range and cone only, no policy) |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L203-L221) | `IsEmitting` |
| [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L344-L386) | `UpdateLaser` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4307-L4353) | `TryRayCraftHit` |
| [src/KSArmory/Ksa/LaserDesignators.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LaserDesignators.cs) | The per-frame spot list |
| [src/KSArmory/Sim/LaserCode.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LaserCode.cs) | Code validation, `LaserSpot` record |

## Snippets

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L3657-L3675
        if (round.Munition.NeedsUplink)
        {
            // No set left to command it, which is the end of the uplink rather than an exemption
            // from it. Testing `Platform is not null` the other way round is the trap: the guard
            // then skips on a destroyed launcher and the round steers on with nothing behind it.
            if (Platform is null) return null;

            double3 toTarget = positionEcl - PlatformEcl;
            var signature = new ThreatModel.ContactSignature(radius, double.PositiveInfinity);

            if (!ThreatModel.InSensorVolume(toTarget, Boresight, Sensor, signature)) return null;

            // Nor while chaff has the set's track: the set is steering on the cloud, not the target.
            if (Radar.ChaffBroke(handle))
            {
                Announce($"{RoundLabel.For(round.Tube)} lost its uplink: the set's track went to chaff");
                return null;
            }
        }
```

```csharp
// src/KSArmory/Sim/SeekerLock.cs L66-L101
        double3 targetAt = target.PositionEcl - (target.VelocityEcl * frameSeconds);
        double targetStrength = StrengthOf(munition, seekerPosEcl, seekerLocalVel, targetAt, targetSignature);
        double targetClosing = Signature.ClosingSpeed(seekerPosEcl, seekerVelEcl, targetAt, target.VelocityEcl);

        Decoy? taken = null;
        double best = targetStrength * Margin;

        for (int i = 0; i < decoys.Count; i++)
        {
            Decoy d = decoys[i];
            if (d.Profile.Fools != munition.Band || _judged.Contains(d.Id)) continue;

            double3 at = d.PositionEcl - (d.VelocityEcl * frameSeconds);
            if (munition.Band == SeekerBand.Radar && Vec.Len(at - targetAt) > ChaffNotch.CellMetres) continue;

            double strength = StrengthOf(munition, seekerPosEcl, seekerLocalVel, at, d.Signature);
            if (strength <= best) continue;

            if (munition.DopplerGateMps > 0f)
            {
                double closing = Signature.ClosingSpeed(seekerPosEcl, seekerVelEcl, at, d.VelocityEcl);
                if (Math.Abs(closing - targetClosing) > munition.DopplerGateMps) continue;
            }

            _judged.Add(d.Id);
            if (_random.NextDouble() < SeductionChance * (1.0 - munition.CountermeasureResistance))
            {
                taken = d;
                best = strength;
            }
        }

        if (taken is null) return SeekerPick.Target;

        OnDecoy = taken;
        return SeekerPick.Decoy;
```

```csharp
// src/KSArmory/Sim/Interceptor.cs L491-L507
            bool homingOnMemory = false;
            if (munition.Guidance == GuidanceMode.AntiRadiation && !OperatorHeld)
            {
                if (t.Emitting)
                {
                    _emissionPosEcl = targetPos;
                    _emissionVelEcl = targetVel;
                    _emissionAge = Age;
                    HasEmission = true;
                }
                else if (HasEmission)
                {
                    targetPos = _emissionPosEcl + _emissionVelEcl * (Age - _emissionAge);
                    targetVel = _emissionVelEcl;
                    homingOnMemory = true;
                }
            }
```

```csharp
// src/KSArmory/Ksa/OpticalHead.cs L357-L386
    private void UpdateLaser(Vehicle platform)
    {
        Spot = null;
        LaserInhibited = LaserWanted && Masked;

        if (!LaserWanted || LaserInhibited) return;
        if (!LaserCode.IsValid(_policy.LaserCode)) return;
        if (!TryOpticViewEcl(out double3 eye, out double3 forward)) return;

        double reach = Profile.LaserRangeMetres;
        bool onCraft = KsaWorld.TryRayCraftHit(eye, forward, platform, reach, out Vehicle? craft, out double range);

        if (KsaWorld.TryRayGround(eye, forward, onCraft ? range : reach, out Celestial? body, out double ground)
            && (!onCraft || ground < range))
        {
            onCraft = false;
            range = ground;
        }
        else if (!onCraft)
        {
            return;
        }

        double3 at = eye + (Vec.Unit(forward) * range);
        double3 velocity = onCraft ? KsaWorld.VelocityEcl(craft!) : KsaWorld.GroundVelocityAt(body!, at);

        Spot = new LaserSpot(platform, _policy.LaserCode, at, velocity, range);
        SpotOn = onCraft ? KsaWorld.DisplayName(craft!) : "the ground";
        LaserDesignators.Add(Spot.Value);
    }
```

## Game APIs touched

| API | Decompiled file | Used for |
|---|---|---|
| `Vehicle.GetPositionEcl()` / `GetVelocityEcl()` (L954, L960), `Vehicle.MeanRadius` (L535) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Target sample and size for the seeker, uplink volume and RCS |
| `Vehicle.IsAnyEnginePropellantAvailable()` (L6685), `Vehicle.GetManualThrottle()` (L1259), `Vehicle.FlightComputer` (L468) | same | Heat signature of a craft |
| `FlightComputer.ActiveEnginePerformanceMax` (L95), `.Thrust` | [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs) | Thrust behind the plume term |
| `Part.RayCastEgo(in double4x4, Ray, out ...)` (L2534) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs) | Laser beam stopped by the first part mesh |
| `Vehicle.GetMatrixAsmb2Ego(double3)` (L1276), `Vehicle.Parts` (L605), `PartTree.Parts` (L136) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs), [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs) | An eye-centred matrix for the cast, and the parts to cast against |
| `Ray` (struct, `Origin`, `Direction`) | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs) | The cast ray, origin at the eye |
| `Universe.GetElapsedTime()` (L2399) | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | Why targets and decoys are back-dated |

## Pitfalls and version notes

- **No laser homing at this ref.** `CLAUDE.md` describes `LaserDesignators` as "read by code,
  because a weapon homes on a code", and `OpticConfig.LaserCode`'s comment says "a weapon homes on
  the spot carrying its own"; neither is true in code yet: `GuidanceMode` has no laser mode and no
  file reads `LaserDesignators.Active`.[^ksarmory-claude][^ksarmory-opticconfig][^ksarmory-laserdesignators]
- **Uplink comment vs code.** The comment above the uplink check says "The fuse still works; see
  Interceptor.Step". `SampleTarget` returns null, `Interceptor.Update` then clears `TargetRef`, and
  the proximity fuse needs a non-null target, so a command-linked round that loses its uplink also
  loses its proximity fuse (WeaponSystem.cs L3647-L3650 against Interceptor.cs L352, L456, L545).[^ksarmory-weaponsystem][^ksarmory-interceptor]
- **One frame out of the volume is enough.** `InSensorVolume` is a pure range-and-cone test with no
  grace period, so a target that crosses the edge of the set's cone for one frame ends guidance for
  every command-linked round already flying at it.
- **Do not gate the uplink on policy.** The code deliberately uses the sensor volume, not the track
  list: the track list applies operator policy (e.g. protecting the controlled craft), and testing
  it would cut every uplink the moment the player takes the target's seat.[^ksarmory-weaponsystem]
- **Roll once per decoy.** A chance rolled every frame turns flare effectiveness into a function of
  frame rate; `SeekerLock` stores judged decoy ids for the whole flight.[^ksarmory-seekerlock]
- **Back-date decoys too.** Decoys are stepped to end-of-step before the seeker runs; comparing them
  against the pre-step round without the `- velocity * frameSeconds` correction would put every
  line of sight a frame of planetary motion off.

## See also

- [Countermeasure dispensers](./countermeasure-dispensers.md)
- [Radar search and lock](./radar-search-and-lock.md)
- [Proportional navigation guidance](./proportional-navigation-guidance.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Optical director heads](./optical-director-heads.md)
- [Gunner sight overlay](./gunner-sight-overlay.md)
- [Hull triangle hit test](./hull-triangle-hit-test.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Model a homing sensor's gimbal limit, raycast occlusion, decoys and uplink loss](../../../howto/flight-and-orbits/guidance/steer-by-zero-effort-miss/steer-by-zero-effort-miss-4.md) - how-to article demonstrating this feature

[^ksarmory-readme]: README.md, Countermeasures
[^ksarmory-munition]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-threatmodel]: src/KSArmory/Sim/ThreatModel.cs
[^ksarmory-radar]: src/KSArmory/Ksa/Radar.cs
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-seekerlock]: src/KSArmory/Sim/SeekerLock.cs
[^ksarmory-signature]: src/KSArmory/Sim/Signature.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-decoy]: src/KSArmory/Sim/Decoy.cs
[^ksarmory-opticalhead]: src/KSArmory/Ksa/OpticalHead.cs
[^ksarmory-lasercode]: src/KSArmory/Sim/LaserCode.cs
[^ksarmory-laserdesignators]: src/KSArmory/Ksa/LaserDesignators.cs
[^ksarmory-opticalheads]: src/KSArmory/Ksa/OpticalHeads.cs
[^ksarmory-sight]: src/KSArmory/Ksa/Sight.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-claude]: CLAUDE.md, file map
[^ksarmory-opticconfig]: src/KSArmory/Sim/OpticConfig.cs
[^ksarmory-chaffnotch]: src/KSArmory/Sim/ChaffNotch.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
