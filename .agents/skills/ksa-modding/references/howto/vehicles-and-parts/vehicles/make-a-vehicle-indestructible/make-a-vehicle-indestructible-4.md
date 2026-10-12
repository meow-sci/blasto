---
type: How-To
title: Damage parts against their crash tolerance with a weak-keyed health ledger
description: Judge each part of nearby craft against Part.CrashTolerancePascals with a cube-root blast law, keep cumulative damage in a ConditionalWeakTable, break through the engine's failure path and dent through FxDeformation.ReportContact
category: vehicles-and-parts
group: vehicles
topic: make-a-vehicle-indestructible
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/blast-damage-and-part-health]
techniques: [weak-keyed-ledger, reflection]
hooks: [starmap StarMapAfterGui, FxDeformation.ReportContact]
lifecycle: [frame:draw-ui, frame:after-on-frame, physics:vehicle-solver]
game_types:
  - KSA.Part
  - KSA.PartStructuralLimits
  - KSA.PartFailure
  - KSA.PartFailureEvent
  - KSA.FxDeformation
  - KSA.Vehicle
tags: [damage, crash-tolerance, dents, blast, ledger]
---
Outcome: the opposite problem to articles 1 to 3. A burst damages each part of every craft nearby by comparing
a blast load against that part's own strength; parts accumulate damage across bursts; parts that run out
break off through the engine's failure path and surviving parts are dented by the engine's impact-dent code.
No gameplay damage number is invented: the scale comes from the charge, the strength from
`Part.CrashTolerancePascals`.

Prerequisite: StarMap. Demonstrated by [ksarmory/blast-damage-and-part-health](../../../../mods/ksarmory/features/blast-damage-and-part-health.md). The queue-and-break half is
article 3 of [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md).

## Background: how the game does it

- **Strength.** `Part.CrashTolerancePascals` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L858)[^ksa-part] is the authored `CrashTolerance` or a value
  derived from subtree inert mass over collider volume, clamped 0.1 to 100 MPa
  [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L30-L44)[^ksa-partstructurallimits]; `PartStructuralLimits.BaseStrength` is 9 MPa
  [current/decomp/KSA/PartStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartStructuralLimits.cs#L11).
- **Break.** Failed parts go to the engine in a `PartFailureEvent` applied right after the staged results; the
  fragment guard `TrippedTheFragmentGuard` decides when losing that many parts is fatal
  [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L77-L80)[^ksa-partfailure].
- **Dent.** `FxDeformation.ReportContact(Part, float3 centerOfMassAsmb, float3 pointBody, float3 normalBody, double
  impulsePerArea, double areaSquareMetres)` is public static. It returns early when deformation is off in the
  player's settings (`GameSettings.Current.Graphics.VesselDeformation`), when the part has no `FxDeformation`
  module, or when `AccumulatedPressure(impulsePerArea) / tolerance` is under `PressureThresholdFraction` (0.5)
  [current/decomp/KSA/FxDeformation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FxDeformation.cs#L318-L370)[^ksa-fxdeformation].

## Lifecycle and timing

| Step | Where it runs | State touched |
|---|---|---|
| detonation, sweeps, shares, health | the mod's `StepSimulation` from `StarMapAfterGui` (`frame:draw-ui`) or the frame-end fallback | reads end-of-step vehicle samples; writes only mod state |
| break / destroy queue | applied at the end of the round update | see article 3 of the race topic |
| dent | `FxDeformation.ReportContact` enqueues a pending impact | the vehicle worker drains the queue on the next dispatched step (`physics:vehicle-solver`) |

`ReportContact` only enqueues under the module's lock (at most 64 pending), so it can be called while the next
vehicle step is already running. Fronts run on the simulated clock, so a pause holds them and warp hurries them.
Breaking is never done inline: splitting a craft from a hook corrupts the next applied step. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Gather the damageable parts of a craft with their strength, position and radius. Skip internally attached
   parts; the position pairing is `vehicleEcl + Asmb2Ego * (PositionVehicleAsmb - CenterOfMassAsmb)`:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3226-3270
    public static bool TryCollectDamageableParts(Vehicle v, double3 vehicleEcl,
                                                 List<DamageablePart> into, List<Part> handles)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(handles);

        into.Clear();
        handles.Clear();

        try
        {
            if (v.IsDisposed) return false;

            double3 centreOfMass = v.CenterOfMassAsmb;
            doubleQuat asmb2Ego = v.Asmb2Ego;

            ReadOnlySpan<Part> parts = v.Parts.Parts;
            if (parts.Length == 0) return false;

            for (int i = 0; i < parts.Length; i++)
            {
                Part part = parts[i];
                if (part.IsAttachedInternal) continue;

                double3 ecl = vehicleEcl + asmb2Ego * (part.PositionVehicleAsmb - centreOfMass);
                if (!Vec.IsFinite(ecl)) continue;

                (double3 min, double3 max) = part.BoundingBoxVehicleAsmb;
                double radius = Vec.Len(max - min) * 0.5;
                if (!double.IsFinite(radius) || radius < 0.0) radius = 0.0;

                into.Add(new DamageablePart(handles.Count, ecl, radius, part.CrashTolerancePascals));
                handles.Add(part);
            }

            return handles.Count > 0;
        }
        catch (Exception e)
        {
            Log.Warn($"could not read the parts of {DisplayName(v)}: {e.Message}");
            into.Clear();
            handles.Clear();
            return false;
        }
    }
```

2. Turn each part's strength into a failure radius by re-anchoring one cube-root law, so a part of the
   reference strength fails exactly at the lethal radius and a part of tolerance `P` fails at
   `lethal * cbrt(9e6 / P)`, never beyond the blast radius:

```csharp
// src/KSArmory/Sim/BlastDamage.cs L84-98
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

3. Keep a health ledger keyed weakly on the engine's `Part` object, so a part that leaves the world takes its
   entry with it. One hit never costs more than a whole share, so a low damage scale slows guns as much as
   missiles:

```csharp
// src/KSArmory/Sim/PartHealth.cs L17-57
internal sealed class PartHealth
{
    /// <summary>The ledger the game runs on; a test makes its own.</summary>
    public static PartHealth World { get; } = new();

    private readonly ConditionalWeakTable<object, StrongBox<double>> _taken = new();

    /// <summary>What is left of a part, one for a part nothing has touched.</summary>
    public double Of(object part)
    {
        ArgumentNullException.ThrowIfNull(part);

        return _taken.TryGetValue(part, out StrongBox<double>? taken)
            ? (taken.Value >= 1.0 - Rounding ? 0.0 : Math.Clamp(1.0 - taken.Value, 0.0, 1.0))
            : 1.0;
    }

    /// <summary>
    /// Takes <paramref name="share"/> times <paramref name="scale"/> off a part, and answers whether
    /// it has nothing left — also true for a part already spent, since one still on its craft is
    /// one the engine has yet to break.
    ///
    /// <para><b>No one hit costs more than a whole share.</b> A shell bursting against the skin
    /// loads it hundreds of times over, so uncapped a tenth of that still breaks the part at once and
    /// the scale does nothing against a gun. At a scale of one the cap changes nothing, because a
    /// whole share already breaks a fresh part.</para>
    /// </summary>
    public bool Hit(object part, double share, double scale)
    {
        ArgumentNullException.ThrowIfNull(part);

        double cost = Math.Min(share, 1.0) * scale;
        StrongBox<double> taken = _taken.GetValue(part, static _ => new StrongBox<double>(0.0));
        if (double.IsFinite(cost) && cost > 0.0) taken.Value += cost;

        return taken.Value >= 1.0 - Rounding;
    }

    // Ten hits at a tenth sum to 0.9999999999999999, and must break the part.
    private const double Rounding = 1.0e-9;
}
```

4. Charge each part and collect the ones that ran out:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L4259-4272
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

5. Ask the engine whether losing that many parts destroys the craft, rather than copying its threshold, and
   queue either the whole craft or the part list (article 3 of [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md)).
6. Dent survivors through the engine's own path. The engine reads an impulse per area and divides by its
   contact step, 0.01, so the mod passes `ratio * tolerance * 0.01`:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3122-3149
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
        catch (Exception e)
        {
            if (!_warnedAboutDents)
            {
                _warnedAboutDents = true;
                Log.Warn($"a blast dent could not be reported: {e.Message}");
            }

            return false;
        }
    }
```

7. Expect dents only if the player's Impact Dents setting is on; the returned bool says whether the engine took
   the report.

## Complete example

- [src/KSArmory/Sim/BlastDamage.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastDamage.cs)[^ksarmory-blastdamage] - radii, shares, dent ratios (pure C#, no game types)
- [src/KSArmory/Sim/PartHealth.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PartHealth.cs)[^ksarmory-parthealth] - the ledger
- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3122-L3149)[^ksarmory-ksaworld] and `:3226-3263` - the game-facing edges

## Variations and gotchas

- **A confirmed hit cannot come to nothing.** When the fuse already ruled a strike lethal and the sweep breaks
  nothing, KSArmory tops the struck part up to a whole share; an unreadable part tree falls back to destroying
  the craft.
- **Keep `ReferencePascals` in step with `BaseStrength`.** The mod writes 9.0e6 itself because nothing under
  `Sim/` may reference KSA; if KSA's value moves, every warhead's reach moves by the cube root of the change.
- **Extremes.** The engine clamps tolerance to 0.1 to 100 MPa, so the flimsiest part reaches 4.48x the lethal
  radius and the densest 0.45x; the weak end is capped at the blast radius.
- **Dent threshold.** The engine ignores anything under half the tolerance, so `DentRatio` bends the real
  fall-off so that threshold lands where the real overpressure is a fifth of what breaks the part.
- **Dents need an `FxDeformation` module** on the part; parts without one report nothing.

## See also

- [Overview](overview.md), [Article 3](make-a-vehicle-indestructible-3.md)
- [ksarmory/blast-damage-and-part-health](../../../../mods/ksarmory/features/blast-damage-and-part-health.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md), [write-worker-thread-safe-solver-patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)

[^ksarmory-parthealth]: KSArmory: src/KSArmory/Sim/PartHealth.cs
[^ksa-fxdeformation]: current/decomp/KSA/FxDeformation.cs (decompiled, build 2026.10.10.5554)
[^ksa-partfailure]: current/decomp/KSA/PartFailure.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-partstructurallimits]: current/decomp/KSA/PartStructuralLimits.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-ksaworld]: KSArmory: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-blastdamage]: KSArmory: src/KSArmory/Sim/BlastDamage.cs
