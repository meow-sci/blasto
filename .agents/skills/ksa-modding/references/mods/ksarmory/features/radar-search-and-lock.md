---
type: Mod Feature
title: Radar search cone, masking and lock dwell
description: A per-weapon radar rebuilt every simulated step from a once-per-pass vehicle census plus the mod's own rounds, filtered by range, cone, Doppler, clutter, planet, terrain, chaff and fireball, and held to a dwell time before a track becomes a firing solution
mod: ksarmory
category: vehicles-and-parts
techniques: [starmap-lifecycle, harmony-prefix, vehicle-census, closed-form-geometry, terrain-sampling]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Program
  - KSA.Vehicle
  - KSA.Part
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.KinematicMeasurements
tags: [radar, sensor, census, line-of-sight, terrain-masking, lock, track, designation]
---

Every KSArmory weapon system carries a `Radar` that rebuilds its whole track list each simulated
step. It looks at every live craft in the system and at every round any weapon has in the air,
throws out what its `SensorProfile` cannot see (range scaled by size, cone, speed, Doppler notch,
clutter floor, a planet or ridge in the way, chaff, a nuclear fireball's ionised air), classifies
the rest by closest point of approach, and promotes the top threat to `Locked` once it has been
held for `LockSeconds`. The player gets a track list with allegiance, range and CPA, a "designate"
button per track, and counters that explain an empty scope ("3 behind the horizon").

The reusable part for modders is the shape: a once-per-pass vehicle census taken from
`Universe.CurrentSystem.All`, an `IContact` abstraction that lets mod-simulated objects sit beside
real `Vehicle`s, cheap-to-expensive reject ordering, and dwell carried between rebuilds in a
dictionary keyed by object identity.[^ksarmory-radar][^ksarmory-ksaworld]

## How it works

**Census.** `KsaWorld.Vehicles` is built at most once per pass by `CollectVehicles`, which walks
`Universe.CurrentSystem.All` and keeps every `Vehicle` with `IsDisposed == false`; only if that
comes back empty does it fall back to `Program.VehiclesInFrame`. Freshness is a flag, not a frame
count: `InvalidateCensus()` is called at the top of `StepSimulation`, at the top of the panel's
`Ui.Draw`, and by the mod paths that destroy or break a craft (`KsaWorld.Remove`, the
`DestroyVehicleFromEvent` path, the part-failure path).[^ksarmory-ksaworld][^ksa-celestialsystem]

**Contacts.** `IContact` is the identity half of a track (handle, names, declared team, radius,
alive, position, velocity, acceleration, debris, munition, drag shape, drawn position, launcher).
`VehicleContact` wraps a `Vehicle`; `RoundContact` wraps a mod round and is built only by
`KSArmoryMod.CollectAirborne`, which snapshots each flying round once per step and carries it to
the end of the step (`PositionEcl + VelocityEcl * step`) so every system sees one instant
regardless of roster order.[^ksarmory-contact][^ksarmory-roundcontact][^ksarmory-mod]

**Where the cone points.** `WeaponSystem.SampleWorld` (every frame) resolves the platform and the
launcher part, then `ResolveBoresight` picks a part-frame axis from `SensorProfile.BoresightSource`
and rotates it to Ecl through `Part.Asmb2VehicleAsmb` and `Vehicle.Asmb2Ego`; any failure falls back
to `KsaWorld.LocalUp` (radial from the parent body).[^ksarmory-weaponsystem][^ksarmory-launcherpart]

| `BoresightMode` | Part-frame axis | Typical use |
|---|---|---|
| `LocalUp` (default) | none; radial from parent body | Ground site, hemisphere search |
| `PartForward` | launcher `+Y` (tube axis) | Seeker heads on rails |
| `TurretAxis` | tube 0 at the current pod pose | Set slaved to the turret |
| `MountNormal` | launcher `+X` (mount face normal) | Bomb sight, designation pod |

**Scan order** (`Radar.Scan` then `Consider`, per contact), cheapest first:[^ksarmory-radar]

1. Radar silenced by the operator and the set `Emits`: clear dwell, drop lock, see nothing.
2. Skip own platform, the controlled craft if `ProtectControlledVehicle`, and wreckage this mod's
   warheads made (counted in `IgnoredWreckage`). Rounds launched from this platform are skipped too.
3. Allegiance from `IffPolicy.Classify(contact.DeclaredTeam)` (see the IFF feature).
4. Acceleration: `AccelerationEstimate.Believe` keeps the engine accelerometer unless a 0.5 s
   median of velocity differences disagrees by more than 3 m/s².
5. Planet occlusion against every body's mean sphere inflated by `TerrainMarginMetres`
   (`LineOfSight.BlockedByTerrain`, closed form).
6. `ThreatModel.TryAssess`: range (size-scaled by `RadarSignature`), cone, `MinTargetSpeed`,
   `NotchSpeed`, `ClutterFloorMetres`, then CPA (see the CPA feature).
7. Chaff in the notch (`ChaffNotch.Hides`), which resets dwell.
8. Fireball blackout: `NuclearClouds.BlacksOut` tests each cloud's ionised sphere
   (`FireballBlackout.Radius`, 1.5 peak fireball radii, about 60 s per megatonne scaled by cube root,
   shrinking over the last 30 %) against both endpoints and the segment.[^ksarmory-fireballblackout][^ksarmory-nuclearclouds]
9. Terrain skyline: `TerrainMask.Blocked` solves the quadratic for the part of the segment below
   the body's highest terrain and samples only that band, `TerrainSamples` times, with
   `TerrainClearanceMetres` slack.[^ksarmory-terrainmask]

**Dwell and lock.** `_dwell` maps handle to held seconds; each surviving track gets
`previous + dt`, and the map is rebuilt from this scan's tracks only, so one missed scan restarts
the clock. Tracks are sorted by priority (`ThreatModel.SortByPriority`), then `UpdateLock` applies:

- `ManualDesignation` (set from the panel's "designate" button) wins while the contact is on scope;
  otherwise it is cleared.
- Otherwise `Locked` is the first threat; `Watched` is `Locked ?? Tracks[0]`, which optical
  instruments follow.
- `IsFiringSolution` = threat and `HeldSeconds >= LockSeconds`; this is what fire control reads.

Profiles are code (`Arsenal`) or pack XML `<Sensor>` elements read by `PackReader.ReadSensor`,
whose defaults match the class defaults (36 km, 90° cone, `LocalUp`, 8 km threat radius, 40 s
horizon, 1.5 s lock).[^ksarmory-packreader][^ksarmory-sensorprofile]

## Lifecycle and timing

The radar runs inside `WeaponSystem.Update`, which `StepSimulation` calls in the fire loop
(KSArmoryMod.cs L776) after `CollectTeams` (L759) and `CollectAirborne` (L763), inside the gate that
only runs when `KsaWorld.ConsumeSimStep()` reported a new engine step. `SampleWorld` (platform,
launcher, boresight) runs earlier (L695) on every frame, outside that gate.[^ksarmory-mod]

`StepSimulation` is reached through `StepOnce`, claimed once per frame by whichever hook gets
there first:

| Hook | Where in `Program.OnFrame` | When it steps |
|---|---|---|
| `[StarMapAfterGui]` (postfix on `OnDrawUiViewports`) | Program.cs L2268, inside `if (DrawUI)` | Normal frames |
| Harmony prefix on `Program.OnFrameCelestials` | Program.cs L2320, after `ImGui.Render` | UI hidden (F2, screenshot)[^ksarmory-prerenderhook] |
| `[StarMapAfterOnFrame]` | after `PostRender` | Fallback if the prefix failed to install |

All three run after `PrepareFrame` applied the finished vehicle step (`Universe.ApplyVehicleSolvers`,
Universe.cs L1922, `_lastSimStep = _nextSimStep` L1948) and dispatched the next one
(`ExecuteNextVehicleSolvers`, Program.cs L2220, Universe.cs L2126).[^ksa-program][^ksa-universe]
So every `GetPositionEcl`/`GetVelocityEcl` the scan reads is the state at `Universe.GetElapsedTime()`
(Universe.cs L2399), the end of the step just applied, and rounds are carried to the same instant
by `CollectAirborne`. Reading vehicle state here is safe (the workers write their own copies until
the next `ApplyVehicleSolvers`); anything the radar decides only reaches the craft in a later
step.

The census choice follows from the frame order: `Program.RefreshVehiclesInFrame` fills
`_vehiclesInFrame` in `PrepareFrame` (Program.cs L2189) and `PostRender` clears it (L2368), so it is
valid from the GUI hook and the celestials prefix but empty from `[StarMapAfterOnFrame]`.
`CelestialSystem.All` is valid from all three.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Radar.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Radar.cs#L118-L203) | `Scan`: census loop, airborne loop, dwell map, sort, lock |
| [src/KSArmory/Ksa/Radar.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Radar.cs#L208-L325) | `Consider` reject ladder, `UpdateLock`, `IsFiringSolution` |
| [src/KSArmory/Sim/SensorProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SensorProfile.cs#L27-L256) | `BoresightMode`, every sensor knob with its default |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L608-L693) | `Vehicles` census, `InvalidateCensus`, `CollectVehicles` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L769-L875) | `IsOccluded` (all bodies), `MaxTerrainHeightMetres`, `IsHiddenByTerrain` |
| [src/KSArmory/Sim/LineOfSight.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LineOfSight.cs) | Segment-sphere test, horizon range |
| [src/KSArmory/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainMask.cs) | Band-limited height-field sampling along the sightline |
| [src/KSArmory/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainHeights.cs) | `ITerrainHeights` over `Celestial.GetTerrainHeightFromDirCce` |
| [src/KSArmory/Sim/FireballBlackout.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireballBlackout.cs) | Ionised sphere radius over time, blocks test |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L68-L91) | `BlacksOut`: places each sphere on its cloud's cap |
| [src/KSArmory/Ksa/Contact.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Contact.cs) | `IContact`, `VehicleContact` |
| [src/KSArmory/Ksa/RoundContact.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundContact.cs) | A mod round as a contact, snapshot kinematics |
| [src/KSArmory/Ksa/Track.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Track.cs) | `TrackState` plus the contact |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L660-L844) | `SampleWorld` (platform, launcher, boresight), `Update` (scan then rounds then fire control) |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L994-L1008) | `ResolveBoresight` |
| [src/KSArmory/Sim/TubeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TubeGeometry.cs#L357-L382) | Boresight mode to part-frame axis |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L894-L908) | Part-frame direction to Ecl |
| [src/KSArmory/Ksa/Ui/UiSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSystem.cs#L700-L767) | Track list, masked counters, designate buttons |
| [src/KSArmory/Sim/SensorMode.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SensorMode.cs) | Not radar: TV/WHOT/BHOT channel of a director camera window |

## Snippets

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L608-L624
    public static IReadOnlyList<Vehicle> Vehicles
    {
        get
        {
            if (_censusFresh) return _census;

            CollectVehicles(_census);
            _censusFresh = true;
            return _census;
        }
    }

    /// <summary>
    /// Throws the shared census away. Called at the top of each pass, and by anything that takes a
    /// vehicle out of the world.
    /// </summary>
    public static void InvalidateCensus() => _censusFresh = false;
```

```csharp
// src/KSArmory/Ksa/Radar.cs L240-L271
        if (!ThreatModel.TryAssess(targetPos - originEcl, targetVel - originVel,
                                   boresight, _sensor, signature, out var a)) return;

        // Chaff in the notch, which takes the track rather than hiding the contact: dropping it here is
        // what resets its dwell, so the lock has to mature again once it is found.
        bool wasBroken = _notch.IsBroken(contact.Handle);
        if (_notch.Hides(_sensor, contact.Handle, originEcl, originVel, targetPos, targetVel,
                         RadarSignature.CrossSectionFor(contact.MeanRadius), Countermeasures.Live))
        {
            LostToChaff++;
            if (!wasBroken) Log.Info($"{_sensor.DisplayName} lost {contact.DisplayName} to chaff in the notch");
            return;
        }

        // A fireball's ionised air, which absorbs a beam that crosses it. A handful of spheres, so
        // cheap, but asked only of what the cone let through and only of a set that transmits.
        if (_sensor.Emits && _config.NuclearBlackout && NuclearClouds.BlacksOut(originEcl, targetPos))
        {
            MaskedByBurst++;
            return;
        }

        // The skyline, and last of all the rejects. Every sample is a height-map fetch, so it is
        // only worth spending on a contact that range, cone and the planet's own bulk have all
        // already let through.
        if (_sensor.HorizonMasking
            && KsaWorld.IsHiddenByTerrain(originEcl, targetPos, _sensor.TerrainSamples,
                                          _sensor.TerrainClearanceMetres, out _))
        {
            MaskedByTerrain++;
            return;
        }
```

```csharp
// src/KSArmory/Ksa/Radar.cs L291-L325
    private void UpdateLock()
    {
        // An operator designation wins as long as the contact is still on scope.
        if (ManualDesignation is not null)
        {
            Track? designated = Tracks.Find(t => ReferenceEquals(t.Contact.Handle, ManualDesignation));
            if (designated is not null)
            {
                Locked = designated;
                return;
            }
            ManualDesignation = null;
        }

        int first = ThreatModel.IndexOfFirstThreat(Tracks);
        Locked = first >= 0 ? Tracks[first] : null;

        // What an instrument should look at, which is not what a weapon may shoot. Tracks are
        // already sorted by priority, so the best thing on scope is the head of the list whether
        // or not it qualifies as a threat.
        //
        // A weapon is right to hold fire at something that will never come close; a sight pointed
        // at it is doing its job. Without this a director watches nothing at all whenever the only
        // contact is a passer-by, and lags the launcher whenever a threat is still maturing —
        // fire control reaches its own verdict first and the missiles leave before the picture
        // has moved.
        Watched = Locked ?? (Tracks.Count > 0 ? Tracks[0] : null);
    }

    /// <summary>True when the locked contact has been held long enough to shoot at.</summary>
    public bool HasFiringSolution => IsFiringSolution(Locked);

    /// <summary>Whether a track is a threat held long enough to shoot at, whether or not it is the lock.</summary>
    public bool IsFiringSolution(Track? track) =>
        track is not null && track.IsThreat && track.HeldSeconds >= _sensor.LockSeconds;
```

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L812-L839
    public void Update(double dt, IReadOnlyList<IContact>? airborne = null)
    {
        if (Platform is null || !KsaWorld.IsAlive(Platform)) return;

        if (double.IsFinite(dt) && dt > 0.0) _finTestSeconds += dt;

        _clock += dt;

        FillIncoming(airborne);

        Radar.Scan(Platform, Boresight, dt, _incoming);
        AttributeRoundsToTracks();
        UpdateTurret(dt);

        // Rounds before fire control, so a round fired this frame is not integrated until the
        // next one. TravelSinceLaunch differences two platform-relative offsets, which cancels the
        // platform's ~29.8 km/s only while the sample advances alongside the round. Integrating a
        // new round in its own launch frame leaves the sample still for one step, so a frame of
        // ecliptic motion lands in travel permanently: 658.78 m of travel at an age of 0.04 s on
        // a round doing 124 m/s.
        //
        // The cost is one frame before a new round moves, which is correct anyway: it is still in
        // the tube on the frame the trigger is pulled.
        UpdateRounds(dt);
        UpdateFireControl(dt);
        UpdateGunFireControl(dt);
        TrimEvents();
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Universe.CurrentSystem`, `CelestialSystem.All` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66) | Census source (Universe.cs L114) |
| `Program.VehiclesInFrame`, `Program.RefreshVehiclesInFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L474-L589) | Fallback census; filled L2189, cleared L2368 |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502) | `ProtectControlledVehicle` skip |
| `Vehicle.GetPositionEcl`, `Vehicle.GetVelocityEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963) | Contact kinematics |
| `Vehicle.IsDisposed`, `Vehicle.IsDebris` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L393) | Liveness (L618), never engage shed debris |
| `Vehicle.AccelerationBody`, `Vehicle.Body2Cce` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L476-L573) | Accelerometer reading plus gravity = contact acceleration |
| `KinematicMeasurements.AccelerationBody` | [current/decomp/KSA/KinematicMeasurements.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KinematicMeasurements.cs#L9) | Backing field of the above[^ksa-kinematicmeasurements] |
| `Part.Asmb2VehicleAsmb`, `Vehicle.Asmb2Ego` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L724) | Part-frame boresight to Ecl (Vehicle.cs L502)[^ksa-part][^ksa-vehicle] |
| `Celestial.GetTerrainHeightFromDirCce(dir, accurate)` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777) | Height samples along the sightline, `accurate: false`[^ksa-celestial] |
| `Celestial.MaxTerrainHeightApprox`, `Astronomical.MaxTerrainRadius` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L114) | Ceiling for the band reject (Celestial.cs L145)[^ksa-astronomical] |
| `Universe.GetElapsedTime`, `Universe.ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1948) | Epoch of every sample (L2399) |

## Pitfalls and version notes

- **`Program.VehiclesInFrame` is frame-scoped.** In build 5554 it is filled at Program.cs L2189
  (`PrepareFrame`) and cleared at L2368 (end of `PostRender`), so it reads empty from
  `[StarMapAfterOnFrame]`. docs/KSA-MODDING-NOTES.md L221-L222 says it is refilled "at a point in
  the tick that does not line up"; the decomp shows the exact window.[^ksarmory-moddingnotes]
- **A held census list goes stale on destruction.** A destroyed `Vehicle` stays in the list as a
  disposed reference; invalidate on every removal and at the start of every independent pass.
- **The radar only rebuilds on a simulated step.** `Scan` runs inside `Update`, behind the clock
  gate, so tracks and dwell freeze while paused, and dwell accrues in simulated seconds: at high
  warp a 1.5 s lock matures in a fraction of a wall second.
- **Dwell is lost on a single missed scan**, including one rejected by chaff, a fireball or a ridge;
  that is intentional (the lock must re-mature).
- **`MaxTerrainHeightApprox` is not an upper bound.** The mod uses `MaxTerrainRadius - MeanRadius`
  plus 8 km headroom instead; its comment says the approximate value is computed before modifiers
  are populated (KsaWorld.cs L814-L847). The mechanism claim is the mod's, not re-measured here.
- **`TerrainSamples` defaults to 0** because the per-sample cost was never measured in game; only the
  inflated sphere masks by default.
- **`SensorMode.cs` is misnamed for this scope**: it is the director camera's colour/TV/thermal
  cycle, unrelated to radar search.[^ksarmory-sensormode]
- **Own salvo filtered twice**: by `LaunchedFrom` in `Radar.Scan` and by round identity in
  `WeaponSystem.FillIncoming`, because a teamless system classifies everything as Unknown.

## See also

- [CPA threat classification](./cpa-threat-classification.md)
- [IFF teams and allegiance](./iff-teams-and-allegiance.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Countermeasure dispensers](./countermeasure-dispensers.md)
- [Radar scope and relief map](./radar-scope-and-relief-map.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Seekers, uplink and laser homing](./seekers-uplink-and-laser-homing.md)
- [Optical director heads](./optical-director-heads.md)
- [Model a radar profile with a search cone, lock dwell and terrain-masked line of sight](../../../howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/target-and-gate-automated-weapons-2.md) - how-to article demonstrating this feature

[^ksarmory-radar]: src/KSArmory/Ksa/Radar.cs
[^ksarmory-sensorprofile]: src/KSArmory/Sim/SensorProfile.cs
[^ksarmory-sensormode]: src/KSArmory/Sim/SensorMode.cs
[^ksarmory-terrainmask]: src/KSArmory/Sim/TerrainMask.cs
[^ksarmory-contact]: src/KSArmory/Ksa/Contact.cs
[^ksarmory-roundcontact]: src/KSArmory/Ksa/RoundContact.cs
[^ksarmory-fireballblackout]: src/KSArmory/Sim/FireballBlackout.cs
[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-launcherpart]: src/KSArmory/Ksa/LauncherPart.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-prerenderhook]: src/KSArmory/Ksa/PreRenderHook.cs
[^ksarmory-packreader]: src/KSArmory/Sim/PackReader.cs
[^ksarmory-moddingnotes]: docs/KSA-MODDING-NOTES.md
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-part]: KSA.Part (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-astronomical]: KSA.Astronomical (decompiled)
[^ksa-kinematicmeasurements]: KSA.KinematicMeasurements (decompiled)
