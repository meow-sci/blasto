---
type: How-To
title: Model a radar profile with a search cone, lock dwell and terrain-masked line of sight
description: Reject contacts cheapest first, sample the height field only in a closed-form ambiguous band, and promote a track to a lock after a dwell carried by object identity
category: vehicles-and-parts
group: craft-tracking
topic: target-and-gate-automated-weapons
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/radar-search-and-lock]
techniques: [closed-form-geometry]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.CelestialSystem, KSA.Program, KSA.Part, KSA.Celestial]
tags: [radar, lock, dwell, terrain-masking, census]
---
Outcome: a `Radar` on every weapon system that rebuilds its whole track list each simulated step. It looks at every live craft in the system and every round any weapon has in the air, throws out what its sensor profile cannot see (size-scaled range, cone, speed, Doppler notch, clutter floor, a planet or ridge in the way, chaff,
a nuclear fireball's ionised air), classifies the rest ([article 1](target-and-gate-automated-weapons-1.md)), and promotes the top threat to `Locked` once it has been held for `LockSeconds`.

Prerequisite: StarMap. Demonstrated by [ksarmory/radar-search-and-lock](../../../../mods/ksarmory/features/radar-search-and-lock.md).

## Background: how the game does it

- **Census.** There is no "all vehicles near me" call. Walk `Universe.CurrentSystem.All` and keep each `Vehicle` with `IsDisposed == false`. `Program.VehiclesInFrame` is filled in `PrepareFrame` and cleared later in the frame, so it is empty from `[StarMapAfterOnFrame]`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L582-L588)[^ksa-program]); `CelestialSystem.All` is valid from every hook.
- **Where the cone points.** The launcher `Part`'s `Asmb2VehicleAsmb` takes a part-frame axis into vehicle space and `Vehicle.Asmb2Ego` into ecliptic; `BoresightMode` picks the axis (`LocalUp`, `PartForward`, `TurretAxis`, `MountNormal`) and any failure falls back to local up
  (radial from the parent body).
- **Terrain.** `Celestial.GetTerrainHeightFromDirCce(dir, accurate)` is a height-map fetch, so terrain is a last, expensive reject.

## Lifecycle and timing

The radar runs inside `WeaponSystem.Update`, which `StepSimulation` calls in its fire loop after `CollectTeams` ([article 2 of the roster topic](../track-and-classify-craft/track-and-classify-craft-2.md)) and the airborne census, inside the gate that only runs when a new engine step was consumed.
That has two consequences: tracks and dwell freeze while paused, and dwell accrues in simulated seconds (at high warp a 1.5 s lock matures in a fraction of a wall second). `StepSimulation` is reached through `StepOnce` under `[StarMapAfterGui]` (`frame:draw-ui`; postfix on `Program.OnDrawUiViewports`, only while the HUD is shown), or with the UI hidden from a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`),
or from `[StarMapAfterOnFrame]` as the last fallback (`frame:after-on-frame`). All run after `PrepareFrame` applied the vehicle step and dispatched the next, so positions and velocities are end-of-step values at one instant. Reading vehicle state here is safe (the workers write their own copies);
whatever is decided only reaches a craft in a later step. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take one census per pass, invalidated explicitly.** Freshness is a flag, not a frame count; invalidate at the top of each pass and whenever anything takes a vehicle out of the world:

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

2. **Abstract contacts.** An `IContact` carries handle, names, declared team, radius, alive, position, velocity, acceleration, debris and munition flags; `VehicleContact` wraps a `Vehicle`, `RoundContact` wraps a mod round and is built once per step by an airborne snapshot, carried to the end of the step, so every system sees one instant.
3. **Reject cheapest first.** Order: radar silenced; own platform, controlled craft and own wreckage; allegiance; acceleration belief; planet occlusion against each body's mean sphere inflated by a margin (closed form); `TryAssess` (range, cone, speed, notch, clutter, CPA); chaff in the notch; fireball blackout; and
   last the terrain skyline. The expensive and ambiguous tests run only on what the cheap ones let through:

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

4. **Sample terrain only where the sphere test is ambiguous.** Solve the quadratic for the part of the segment below the body's highest terrain and sample only that band, interior points only (both ends often sit on the ground):

```csharp
// src/KSArmory/Sim/TerrainMask.cs L38-L69
public static bool Blocked(double3 eye, double3 target, double3 centre, double meanRadius,
                           double maxTerrainHeight, int samples, double clearanceMetres,
                           ITerrainHeights heights)
{
    if (heights is null || samples <= 0) return false;
    if (!Vec.IsFinite(eye) || !Vec.IsFinite(target) || !Vec.IsFinite(centre)) return false;
    if (!double.IsFinite(meanRadius) || meanRadius <= 0.0) return false;

    double ceiling = meanRadius + Math.Max(0.0, double.IsFinite(maxTerrainHeight) ? maxTerrainHeight : 0.0);
    double clearance = Math.Max(0.0, double.IsFinite(clearanceMetres) ? clearanceMetres : 0.0);

    if (!TryBandBelow(eye, target, centre, ceiling, out double from, out double to)) return false;

    double3 along = target - eye;

    // Interior points of the band, evenly spaced and both ends left out.
    for (int i = 1; i <= samples; i++)
    {
        double t = from + (to - from) * i / (samples + 1);

        double3 radial = eye + along * t - centre;
        double radius = Vec.Len(radial);
        if (!(radius > 0.0)) continue;

        if (!heights.TryHeight(radial / radius, out double height)) continue;
        if (!double.IsFinite(height)) continue;

        if (radius + clearance <= meanRadius + height) return true;
    }

    return false;
}
```

```csharp
// src/KSArmory/Sim/TerrainMask.cs L81-L108
public static bool TryBandBelow(double3 eye, double3 target, double3 centre, double ceiling,
                                out double from, out double to)
{
    from = to = 0.0;

    if (!Vec.IsFinite(eye) || !Vec.IsFinite(target) || !Vec.IsFinite(centre)) return false;
    if (!double.IsFinite(ceiling) || ceiling <= 0.0) return false;

    double3 along = target - eye;
    double a = Vec.Len2(along);
    if (!double.IsFinite(a) || a < 1e-12) return false;

    double3 offset = eye - centre;
    double b = 2.0 * Vec.Dot(offset, along);
    double c = Vec.Len2(offset) - ceiling * ceiling;

    double discriminant = b * b - 4.0 * a * c;
    if (!double.IsFinite(discriminant) || discriminant <= 0.0) return false;

    double root = Math.Sqrt(discriminant);
    double lo = (-b - root) / (2.0 * a);
    double hi = (-b + root) / (2.0 * a);

    from = Math.Max(0.0, Math.Min(lo, hi));
    to = Math.Min(1.0, Math.Max(lo, hi));

    return to > from;
}
```

5. **Carry dwell by object identity and rebuild it from this scan's survivors only.** One missed scan restarts the clock. Sort by priority, then pick the lock:

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

## Complete example

- [src/KSArmory/Ksa/Radar.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Radar.cs#L200-L330)[^ksarmory-radar] - `Consider`, `UpdateLock`, `IsFiringSolution`.
- [src/KSArmory/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainMask.cs#L1-L109)[^ksarmory-terrainmask] and [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L849-L875)[^ksarmory-ksaworld].

## Variations and gotchas

- **`Program.VehiclesInFrame` is frame-scoped**; use the system collection.
- **A held census list goes stale on destruction.** A destroyed `Vehicle` stays in the list as a disposed reference; invalidate on every removal.
- **Dwell is lost on a single missed scan**, including one rejected by chaff, a fireball or a ridge; intentional.
- **`TerrainSamples` defaults to 0.** The per-sample cost was never measured in game; only the inflated sphere masks by default.
- **Own salvo filtered twice** (by `LaunchedFrom` and by round identity) because a teamless system classifies everything as Unknown.
- **Watch versus lock.** `Watched` is `Locked ?? Tracks[0]`, what an instrument should look at, which is not what a weapon may shoot.

## See also

- [Overview](overview.md); [Article 1](target-and-gate-automated-weapons-1.md); [Article 3](target-and-gate-automated-weapons-3.md)
- [ksarmory/radar-search-and-lock](../../../../mods/ksarmory/features/radar-search-and-lock.md)
- [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)

[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-radar]: KSArmory: src/KSArmory/Ksa/Radar.cs
[^ksarmory-terrainmask]: KSArmory: src/KSArmory/Sim/TerrainMask.cs
[^ksarmory-ksaworld]: KSArmory: src/KSArmory/Ksa/KsaWorld.cs
