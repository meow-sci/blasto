---
type: Mod Feature
title: Blast damage per part against the engine's crash tolerance, with health and engine dents
description: A burst judges every part of every craft on its own distance and the engine-derived Part.CrashTolerancePascals, charges a persistent per-part health ledger, queues the parts that break, and hands the loads short of breaking to KSA's own dent system when the blast front arrives
mod: ksarmory
category: vehicles-and-parts
techniques: [custom-physics, sim-boundary, weak-keyed-ledger, deferred-application, engine-dent-api]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, physics:vehicle-solver]
game_types:
  - KSA.Part
  - KSA.PartStructuralLimits
  - KSA.FxDeformation
  - KSA.PartFailure
  - KSA.Vehicle
  - KSA.GameSettings
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
tags: [damage, warhead, blast, overpressure, part-health, crash-tolerance, dents, deformation, blast-front]
---

When a KSArmory warhead or shell bursts, every craft nearby is judged part by part: each part is
measured from the burst to its own skin, compared with the strength KSA itself assigns that part,
and charged against a health ledger that remembers earlier hits. Parts that run out of health are
queued to break off through the engine's failure path; parts that survive are dented by the
engine's own impact-dent code, but only when the blast front reaches them, so a distant craft
flashes first and is struck a second later.

No gameplay damage number is invented: the scale comes from the charge (cube-root law), and the
per-part strength is `Part.CrashTolerancePascals`, which KSA derives from each part's mass and
collider volume.[^ksarmory-blastdamage][^ksa-part]

## How it works

**Radii from one number.** `Warhead` derives every radius from `ChargeKg` by Hopkinson-Cranz
scaling `R = Z * W^(1/3)`: lethal `Z = 7.368`, blast (near-miss) `Z = 22.104`, fireball `Z = 2.6`.
`MunitionProfile.LethalRadius` and `BlastRadius` are computed properties, so a profile cannot carry
a lethal radius larger than its blast radius.[^ksarmory-warhead]

**Every distance at the burst's own instant.** Rounds burst part-way through a step while vehicle
positions are end-of-step samples, so every gap is
`BlastSweep.SurfaceGap = Len(sampledPos + velocity * sinceSample - burst) - radius`, with
`sinceSample = DetonationElapsedInFrame` (negative). The shared orbital velocity cancels in the
difference.[^ksarmory-blastsweep]

**The pipeline in `WeaponSystem.Detonate` / `Splash` / `Damage`:**[^ksarmory-weaponsystem]

| Stage | What happens |
|---|---|
| Altitude | Charges of 1,000 kg or more are re-judged as `BlastAltitude.EquivalentChargeKg` for the air at the burst (Glasstone and Dolan efficiency, X-ray reach)[^ksarmory-blastaltitude] |
| Ground | `KsaWorld.GroundReflectionAt` builds a `GroundReflection` (surface doubling or air-burst Mach stem); each part's charge is multiplied by `GainAt(part)`, a whole craft's gap divided by its cube root[^ksarmory-groundreflection] |
| Craft sweep | Every vehicle in `KsaWorld.Vehicles`: `BlastSweep.Effect(gap)` gives `Lethal`, `NearMiss` or `Untouched`; the firing platform and the protected controlled craft are dented only |
| Part collection | `KsaWorld.TryCollectDamageableParts`: each part not `IsAttachedInternal`, at `vehicleEcl + Asmb2Ego * (PositionVehicleAsmb - CenterOfMassAsmb)`, radius half the `BoundingBoxVehicleAsmb` diagonal, strength `CrashTolerancePascals` |
| Shares | `BlastDamage.Shares`: inside the part's failure radius the share is at least 1; outside it, real overpressure over breaking overpressure (Kinney-Graham fit in `BlastWave`) |
| Health | `PartHealth.World.Hit(part, share, Config.DamageScale)`; a part breaks when cumulative cost reaches 1 |
| Craft fate | `PartFailure.TrippedTheFragmentGuard(failed, count)` true means the whole craft is queued dead; otherwise the failed parts are queued for the engine |
| Dents | `BlastDamage.Loads` for every surviving part inside the blast radius, queued to `BlastArrivals` |

**The failure radius re-anchors one law per part.** At the reference strength (`9.0e6` Pa, KSA's
`PartStructuralLimits.BaseStrength`) a part fails exactly at the lethal radius; a part of tolerance
`P` fails at `lethal * cbrt(9e6 / P)`, never beyond the blast radius.[^ksa-partstructurallimits]

**What KSA says a part can take.** `Part.CrashTolerancePascals` (Part.cs L858) is
`PartStructuralLimits.ResolveCrashTolerance(Template.CrashTolerance, ComputeSubtreeInertMass(),
ColliderVolumeCubicMetres)`: an authored value if the part template has one, otherwise
`clamp(9 MPa * clamp(density / 330, 0.1, 8), 0.1 MPa, 100 MPa)` (PartStructuralLimits.cs L30-L44).

**The health ledger.** `PartHealth` is a `ConditionalWeakTable<object, StrongBox<double>>` keyed on
the engine's `Part` object, so a part that leaves the world drops out with no pruning. One hit never
costs more than a whole share, so a low `DamageScale` slows guns as much as missiles.[^ksarmory-parthealth]

**A confirmed hit cannot come to nothing.** When the fuse already ruled a strike lethal and the sweep
breaks nothing, the struck part (nearest by gap) is topped up to a whole share; an unreadable part
tree falls back to destroying the whole craft.

**Dents, delivered on the front.** `Dent` queues each loaded part with `BlastArrivals.Queue`, anchored
to the ground the burst went off on (`KsaWorld.TryAnchorToGround`, body-fixed). Each simulated step
`BlastArrivals.Update`:[^ksarmory-blastarrivals]

1. Re-measures the gap from the burst (carried with the ground) to the part's box, so a craft that
   flies away or climbs faster than the front is caught later or never.
2. Strikes when `BlastFront.Radius(age) >= gap` (no air: at once).
3. If another front will reach the same part within this one's positive phase, hands its load on;
   the last front loads the part with what is left of each (Friedlander decay) plus the most head-on
   pair reflected as at a wall (`BlastDamage.Combine`). That meeting share is charged to health and
   can break the part.
4. Otherwise calls `KsaWorld.ReportBlastDent` on the face of the part box toward the burst, throws a
   dust puff, and adds the wind push (see [blast shove](./blast-shove-impulse.md)).

**The engine's dent API.** `ReportBlastDent` calls the public static
`FxDeformation.ReportContact(part, centreOfMassAsmb: 0, pointBody: face, normalBody: push,
impulsePerArea: ratio * tolerance * 0.01, area: pi * across^2)`. KSA turns that back into
`AccumulatedPressure(impulsePerArea) / tolerance = ratio`, ignores anything under
`PressureThresholdFraction` (0.5), sets depth `min(ratio * 0.099, 0.15)` of the part's scale radius,
and footprint `2.29 * sqrt(area / pi)` clamped to 0.314-0.709 of it.[^ksa-fxdeformation]
`DentRatio` bends the real blast fall-off so the engine's 0.5 threshold lands where real overpressure
is a fifth of what breaks the part.

## Lifecycle and timing

| Step | Where it runs | State touched |
|---|---|---|
| Detonation, sweeps, shares, health | `WeaponSystem.UpdateRounds` (L3482) inside `StepSimulation`, from `[StarMapAfterGui]` (`frame:draw-ui`), the `OnFrameCelestials` prefix or `[StarMapAfterOnFrame]` | Reads end-of-step vehicle samples; writes only mod state |
| Break / destroy queue | `ApplyPendingKills` at the end of `UpdateRounds` | See [safe destruction](./safe-vehicle-destruction-and-part-failure.md) |
| Front follow and dents | `BlastArrivals.Update(_lastSimStep)` in the tail of `StepSimulation` (KSArmoryMod.cs L944), every frame, on the simulated step | Reads part boxes; calls `FxDeformation.ReportContact` |
| Dent applied | `FxDeformation.UpdateModules` drains the queue from `VehicleUpdateState.UpdateModules` (L381-L401), called by `PhysicsBubble` on the vehicle worker | `physics:vehicle-solver`, next dispatched step |

- **Why the hook is safe for dents.** `ReportContact` only enqueues a `PendingImpact` under the
  module's lock (`FxDeformation.Enqueue`, L372-L383, at most 64 pending); the worker drains it on its
  own thread. So it can be called while the next vehicle step is already running on workers
  (dispatched at Program.cs L2220, before every StarMap hook).[^ksa-fxdeformation][^ksa-vehicleupdatestate]
- **Why fronts run on the simulated clock.** `_lastSimStep` is the step KSA applied this frame, so
  a pause holds the fronts and timewarp hurries them, matching the world they strike.[^ksarmory-mod]
- **Breaking is never done here.** Splitting a craft from a mod hook corrupts the next applied step;
  the part list is handed to the engine's own `PartFailureEvent` slot instead.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Warhead.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Warhead.cs) | Cube-root radii from the charge |
| [src/KSArmory/Sim/BlastSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastSweep.cs) | Surface gap at the burst instant; whole-craft verdict |
| [src/KSArmory/Sim/BlastDamage.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastDamage.cs) | Failure radius, shares, dent ratio, combining fronts |
| [src/KSArmory/Sim/PartHealth.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PartHealth.cs) | Weak-keyed health ledger |
| [src/KSArmory/Sim/BlastWave.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastWave.cs) | Kinney-Graham overpressure, positive phase, wind, reflection |
| [src/KSArmory/Sim/BlastAltitude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastAltitude.cs) | Blast efficiency and front radius in thin air |
| [src/KSArmory/Sim/GroundReflection.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/GroundReflection.cs) | Surface doubling and Mach stem gain |
| [src/KSArmory/Sim/BlastFront.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastFront.cs) | One front per burst: radius at an age, arrival at a distance |
| [src/KSArmory/Ksa/BlastArrivals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastArrivals.cs) | Loads held until the front arrives; fronts together; strike |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3758-L3980) | `Detonate` and `Splash` |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L4161-L4386) | `DentOnly`, `Dent`, `Damage`, struck part, queues |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3084-L3151) | `TryBlastFace`, `ReportBlastDent` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3226-L3305) | `TryCollectDamageableParts`, `LosingThatManyPartsIsFatal` |
| [src/KSArmory/Sim/Config.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Config.cs#L150-L175) | `DamageIndividualParts`, `DamageScale` |

## Snippets

```csharp
// src/KSArmory/Sim/BlastDamage.cs L84-L98
    public static double FailureRadius(double chargeKg, double crashTolerancePascals)
    {
        double lethal = Warhead.LethalRadius(chargeKg);
        if (lethal <= 0.0) return 0.0;

        // A tolerance the engine could not answer for is treated as the reference part rather
        // than as either invulnerable or made of paper.
        double tolerance = double.IsFinite(crashTolerancePascals) && crashTolerancePascals > 0.0
            ? crashTolerancePascals
            : ReferencePascals;

        double reach = lethal * Math.Cbrt(ReferencePascals / tolerance);

        return Math.Min(reach, Warhead.BlastRadius(chargeKg));
    }
```

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L4259-L4272
        // Each part pays what the burst puts on it out of what earlier bursts left, and breaks when
        // that runs out -- at a scale of one, a fresh part exactly where the sweep alone broke it.
        _failedParts.Clear();
        _partShares.Clear();
        BlastDamage.Shares(burst, elapsed, KsaWorld.VelocityEcl(v),
                           CollectionsMarshal.AsSpan(_partScratch), munition, _partShares);
        foreach ((int index, double share) in _partShares)
        {
            if (PartHealth.World.Hit(_partHandles[index], share, _config.DamageScale)) _failedParts.Add(index);
        }

        // And what it loads short of breaking, dented, before anything decides the craft's fate:
        // a craft that survives is the one the dents are for.
        Dent(v, burst, elapsed, munition, _failedParts, mayBreak: true);
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3122-L3138
    public static bool ReportBlastDent(Part part, double3 faceAsmb, double3 pushAsmb, double acrossMetres,
                                       double pressureRatio)
    {
        try
        {
            double tolerance = part.FullPart.CrashTolerancePascals;
            if (!(tolerance > 0.0) || !(pressureRatio > 0.0)) return false;

            int before = FxDeformation.Shared.TotalReported;

            // The engine reads an impulse per area and divides by its contact step, 0.01, to get the
            // pressure it compares with the part's tolerance.
            FxDeformation.ReportContact(part, float3.Zero, float3.Pack(in faceAsmb), float3.Pack(in pushAsmb),
                                        pressureRatio * tolerance * 0.01, Math.PI * acrossMetres * acrossMetres);

            return FxDeformation.Shared.TotalReported > before;
        }
```

```csharp
// current/decomp/KSA/PartStructuralLimits.cs L30-L44
	public static double DeriveCrashTolerance(double massKg, double volumeCubicMetres)
	{
		if (!(massKg > 0.0) || !double.IsFinite(volumeCubicMetres) || volumeCubicMetres <= 0.0)
		{
			return Math.Clamp(9000000.0, 100000.0, 100000000.0);
		}
		double num = massKg / Math.Max(volumeCubicMetres, 1E-06);
		double num2 = Math.Clamp(num / 330.0, 0.1, 8.0);
		return Math.Clamp(9000000.0 * num2, 100000.0, 100000000.0);
	}

	public static double ResolveCrashTolerance(double authoredPascals, double massKg, double volumeCubicMetres)
	{
		return (double.IsNaN(authoredPascals) || authoredPascals <= 0.0) ? DeriveCrashTolerance(massKg, volumeCubicMetres) : authoredPascals;
	}
```

## Game APIs touched

| API | Decompiled file | Used for |
|---|---|---|
| `Part.CrashTolerancePascals` (L858), `IsAttachedInternal` (L1149), `BoundingBoxVehicleAsmb` (L835), `PositionVehicleAsmb` (L708), `FullPart` (L1171) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs) | Per-part strength, position and size |
| `PartStructuralLimits.BaseStrength`, `ResolveCrashTolerance`, `AccumulatedPressure` | [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L10-L44) | Reference strength (mirrored as a constant in `Sim/`); how the dent pressure is decoded |
| `FxDeformation.ReportContact` (L318), `Shared.TotalReported` / `PressureThresholdFraction` / `DepthPerToleranceFraction` / `MaxDepthFraction` / `RadiusScale` | [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L13-L42) | Engine dents; acceptance count |
| `PartFailure.TrippedTheFragmentGuard` (L76-L79) | [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L76-L79) | `failed >= max(2, (int)(count * 0.5))` destroys the craft outright |
| `GameSettings.Graphics.VesselDeformation` (L394) | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L394) | "Impact Dents" setting that gates `ReportContact` |
| `VehicleUpdateState.UpdateModules` (L381) | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L381-L401) | Worker-side drain of queued dents |
| `Vehicle.CenterOfMassAsmb`, `Asmb2Ego`, `Parts` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502-L605) | Part positions in the ecliptic[^ksa-vehicle] |

## Pitfalls and version notes

- **The reference strength is copied, not read.** `BlastDamage.ReferencePascals = 9.0e6` mirrors
  `PartStructuralLimits.BaseStrength` because `Sim/` may not reference KSA; if KSA changes it, every
  warhead's reach moves by the cube root of the change.
- **The clamp the mod's comment quotes is wider than what derived parts reach.** `BlastDamage` says
  the engine clamps tolerance to 0.1-100 MPa (reach 4.48x to 0.45x lethal). In 5554 a *derived*
  tolerance is `9 MPa * clamp(density/330, 0.1, 8)`, so 0.9-72 MPa (reach about 2.15x to 0.5x); only an
  authored `CrashTolerance` can lie outside that, and authored values are not clamped at all
  (PartStructuralLimits.cs L37-L43).[^ksa-partstructurallimits]
- **Two different part counts feed the fragment guard.** `Damage` passes the count of collected
  parts (internal attachments excluded, WeaponSystem.cs L4306), `BlastArrivals.Break` passes
  `Vehicle.Parts.Count`, and KSA's own `PartFailure.Detect` passes `Parts.Count` (PartFailure.cs
  L72-L73). On craft with internally attached parts the mod's burst path trips the guard slightly earlier.
- **Dents are the player's setting.** `ReportContact` returns silently when
  `FxDeformation.Shared.Enabled` or "Impact Dents" is off, or the part has no `FxDeformation` module;
  `ReportBlastDent` detects acceptance by comparing `TotalReported` before and after.[^ksa-fxdeformation]
- **Doc drift.** README "Limitations" (README.md L479) still says "Damage is binary"; the code
  breaks individual parts whenever `Config.DamageIndividualParts` (default true) and the reflective
  probe of `Vehicle._threadWorkerUpdateState` succeed.[^ksarmory-readme][^ksarmory-config]
  `docs/KSA-MODDING-NOTES.md` "Dents are the engine's" matches build 5554 in every constant checked.[^ksarmory-modding-notes]
- **Protected craft are only spared the burst.** The platform and the flown craft are dented, never
  broken, but a craft the shove throws into the ground is crash-damaged by KSA as usual.

## See also

- [Safe vehicle destruction and part failure](./safe-vehicle-destruction-and-part-failure.md)
- [Blast shove impulse](./blast-shove-impulse.md)
- [Holed tank leaks](./holed-tank-leaks.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Hull triangle hit test](./hull-triangle-hit-test.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [KSA explosions from a point](./ksa-explosions-from-a-point.md)
- [Bullet hole decals](./bullet-hole-decals.md)
- [Damage parts against their crash tolerance with a weak-keyed health ledger](../../../howto/vehicles-and-parts/vehicles/make-a-vehicle-indestructible/make-a-vehicle-indestructible-4.md) - how-to article demonstrating this feature

[^ksarmory-blastdamage]: src/KSArmory/Sim/BlastDamage.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksarmory-warhead]: src/KSArmory/Sim/Warhead.cs
[^ksarmory-blastsweep]: src/KSArmory/Sim/BlastSweep.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-blastaltitude]: src/KSArmory/Sim/BlastAltitude.cs
[^ksarmory-groundreflection]: src/KSArmory/Sim/GroundReflection.cs
[^ksa-partstructurallimits]: KSA.PartStructuralLimits (decompiled, build 2026.10.10.5554)
[^ksarmory-parthealth]: src/KSArmory/Sim/PartHealth.cs
[^ksarmory-blastarrivals]: src/KSArmory/Ksa/BlastArrivals.cs
[^ksa-fxdeformation]: KSA.FxDeformation (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-readme]: README.md
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-modding-notes]: docs/KSA-MODDING-NOTES.md (author notes)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
