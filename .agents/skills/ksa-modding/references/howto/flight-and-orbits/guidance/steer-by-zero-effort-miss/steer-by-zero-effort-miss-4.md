---
type: How-To
title: Model a homing sensor's gimbal limit, raycast occlusion, decoys and uplink loss
description: Gate a homing round on a seeker field of view recomputed every sub-step, cast a sensor beam against craft meshes with Part.RayCastEgo and against terrain, judge each decoy once, remember an emitter that goes silent, and drop a command uplink when the launcher loses sight
category: flight-and-orbits
group: guidance
topic: steer-by-zero-effort-miss
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/seekers-uplink-and-laser-homing]
techniques: [engine-raycast, sensor-model, sim-boundary]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Part, KSA.PartTree, KSA.Ray, KSA.FlightComputer, KSA.Universe]
tags:
  - seeker
  - field-of-view
  - line-of-sight
  - raycast
  - occlusion
  - decoy
  - countermeasures
  - uplink
  - laser-designator
  - anti-radiation
---
Outcome: a homing round only steers on what its sensor could really see. The seeker has a gimbal cone it can lose and regain a target in; a beam stops at the first craft mesh or the ground; a flare or chaff cloud is judged once, when it first outshines the target; an anti-radiation round flies at where it last heard its emitter; and a command-linked round loses its uplink, permanently, the frame its launcher cannot see the target. KSArmory demonstrates each piece around the guidance law of [approach 1](steer-by-zero-effort-miss-1.md).[Seekers, uplink and laser homing](../../../../mods/ksarmory/features/seekers-uplink-and-laser-homing.md)

Prerequisites: StarMap; a mod-integrated round with a per-frame target sample; access to the target `Vehicle` for its parts.

## Background: how the game does it

- **Ray against a craft.** `Part.RayCastEgo(in double4x4 matrixVehicleAsmb2Ego, Ray ray, out double minDistance, ...)` tests a bounding sphere, then each sub-part, and returns the near and far hit distances along the ray.[^ksa-part] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572)
- **The matrix.** `Vehicle.GetMatrixAsmb2Ego(double3 bodyPositionEgo)` builds the assembly-to-ego transform for a vehicle whose centre of mass is at that ego position; pass `centre - eye` and the cast runs in a frame centred on the eye.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281)
- **The parts.** `Vehicle.Parts` is a `PartTree`, and `PartTree.Parts` is a `ReadOnlySpan<Part>`.[^ksa-parttree] [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136)
- **The ray.** `KSA.Ray` is a struct with `Origin` and `Direction` fields; its primary constructor normalises the direction, an object initializer does not.[^ksa-ray] [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L7-L11)
- **Heat.** A target's plume can be estimated from `FlightComputer.ActiveEnginePerformanceMax.Thrust`, `Vehicle.GetManualThrottle()` and `Vehicle.IsAnyEnginePropellantAvailable()`.[^ksa-flightcomputer]
- **Epoch.** Samples are end-of-step (`Universe.GetElapsedTime()`), so targets and decoys are back-dated by the frame step before any line of sight is drawn.[^ksa-universe]

## Lifecycle and timing

Everything runs in the mod's own simulation step, reached from `[StarMapAfterGui]`, the `Program.OnFrameCelestials` prefix when the HUD is hidden, or `[StarMapAfterOnFrame]`. Order inside the step matters: decoys are advanced to end-of-step before any seeker judges them; the radar scan decides chaff before the uplink check reads it; laser heads cast after rounds fly, so a consumer of laser spots would see last frame's. Casts call `Part.RayCastEgo` on the main thread while the next vehicle step runs on workers, which reads part state the worker does not write; nothing here writes KSA state. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Recompute the gimbal gate every sub-step.** `SeekerInView` is true for a command-linked round, for an operator-held aim, or when the angle between the line of sight and the round's air-relative velocity is within the seeker's field of view; a seeker that loses its target off-axis can regain it (see step 3 of [approach 1](steer-by-zero-effort-miss-1.md)).
2. **Cast the beam against craft meshes.** Broad phase on a bounding sphere, then `Part.RayCastEgo` per part with an eye-centred matrix; keep the nearest hit and exclude the craft carrying the sensor:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4312-L4353
    public static bool TryRayCraftHit(double3 eye, double3 direction, Vehicle? exclude, double maxRange,
                                      out Vehicle? hitCraft, out double range)
    {
        hitCraft = null;
        range = maxRange;

        try
        {
            Ray ray = new() { Origin = default, Direction = Vec.Unit(direction) };
            CollectVehicles(_pickScratch);

            foreach (Vehicle craft in _pickScratch)
            {
                if (ReferenceEquals(craft, exclude) || craft.Parts is not { } tree) continue;

                double radius = MeanRadius(craft);
                double3 centre = PositionEcl(craft);
                if (!(radius > 0.0) || Vec.Len(centre - eye) - radius > range) continue;
                if (!Picking.TryHitSphere(eye, direction, centre, radius, out _)) continue;

                double4x4 asmb2Ego = craft.GetMatrixAsmb2Ego(centre - eye);
                ReadOnlySpan<Part> parts = tree.Parts;

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].RayCastEgo(in asmb2Ego, ray, out double hit, out _,
                                            out _, out _, out _, out _, out _, out _)
                        && hit > 0.0 && hit < range)
                    {
                        range = hit;
                        hitCraft = craft;
                    }
                }
            }

            return hitCraft is not null;
        }
        catch
        {
            return false;
        }
    }
```

3. **Let the ground stop it too, and publish what the beam found.** A designator head casts craft first, then terrain up to the craft hit; the nearer wins. The published spot carries the velocity of what it is on (the craft's, or the ground's there) so a consumer can replay it without losing it to the planet's motion:

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

4. **Judge each decoy once, when it first outshines the target.** Back-date target and decoys by the frame step, require the decoy to beat the target's strength (and pass a Doppler gate for radar), then roll once per decoy id; a chance rolled every frame would make flare effectiveness a function of frame rate:

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

5. **Remember an emitter that goes silent.** An anti-radiation round stores the back-dated emission position, velocity and its own age while the target emits, and replays position plus velocity times elapsed age afterwards, so the remembered spot rides the planet:

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

6. **Drop a command uplink when the launcher loses sight.** Test range and cone against the sensor volume, not the operator's track list (policy would cut every uplink the moment the player takes the target's seat), and treat a destroyed launcher as the end of the uplink:

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
// src/KSArmory/Sim/ThreatModel.cs L148-L160
    public static bool InSensorVolume(double3 r, double3 boresight, SensorProfile sensor,
                                      ContactSignature signature)
    {
        double rangeSquared = Vec.Len2(r);

        // The same reach the detection used. Taking the profile's raw range instead would keep an
        // uplink alive out to 36 km against something the set only sees at 9, so a round would go
        // on being steered at a contact its own launcher had lost.
        double reach = DetectionRange(sensor, signature);

        if (rangeSquared > reach * reach || rangeSquared < 1.0) return false;
        return Vec.Dot(Vec.Unit(r), boresight) >= Math.Cos(sensor.ConeHalfAngleRad);
    }
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3585-L3678) | `SampleTarget`: ground resample, emission flag, uplink |
| [src/KSArmory/Sim/SeekerLock.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SeekerLock.cs) | Decoy judging, hold, reacquire or lost |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L482-L540) | Anti-radiation memory, blind state, gimbal gate |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4312-L4396) | `TryRayCraftHit`, `TryCraftMasks` (own-hull masking) |
| [src/KSArmory/Ksa/OpticalHead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticalHead.cs#L344-L386) | Laser beam against craft and terrain |
| [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L139-L160) | `InSensorVolume` |

## Variations and gotchas

- **Losing the uplink is permanent here.** A null target clears the round's target reference, and the proximity fuse needs a target, so a command-linked round that loses its uplink also loses its fuse. The source comment says the fuse still works; the code says otherwise.
- **No grace period.** One frame outside the cone ends guidance for every command-linked round already flying.
- **No laser homing at this ref.** The laser spot is cast and published, but no guidance mode reads it.
- **Mask your own hull.** `TryCraftMasks` casts from the optic against its own craft and ignores the first few centimetres (the eye sits just outside its own glass).
- **Back-date decoys too.** Comparing end-of-step decoys against a start-of-step round puts every line of sight a frame of planetary motion off.

## See also

- [Overview](overview.md); [Proportional navigation](steer-by-zero-effort-miss-1.md)
- [Seekers, uplink and laser homing (mod feature)](../../../../mods/ksarmory/features/seekers-uplink-and-laser-homing.md)
- [Find the closest approach to a target along a trajectory](../../map-view/find-closest-approach-along-the-flight-plan/overview.md) (KSArmory's straight-line CPA threat test)
- [Anchor points to a rotating body and pick bodies under the cursor](../../map-view/anchor-and-pick-points-on-bodies/overview.md)

[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
