---
type: How-To
title: Mirror the branches of OrbitalTransfers.AlignmentTime with a live phase clock
description: Compute the target phase angle, synodic rate and time to the next window in a game-free kernel, refresh the live phase each frame from Orbit.GetPhaseAngle, and show the result as a list and a polar clock
category: flight-and-orbits
group: transfers
topic: compute-transfer-windows
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/transfer-windows-and-phase-clock]
techniques: [closed-form-orbital-math, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.OrbitalTransfers, KSA.MathEx, KSA.IOrbiter, KSA.Universe]
tags: [transfer-window, phase-angle, synodic-period, ejection-angle, countdown, polar-clock]
---

Outcome: for the root body and every sibling sharing its parent, the optimal phase angle, the live current phase, a countdown to the next window, the synodic period, the ejection angle and the transfer time, in a compact list and a polar phase clock. KSA-DeltaVMap implements it.[Transfer windows list and live polar phase clock](../../../../mods/ksa-deltavmap/features/transfer-windows-and-phase-clock.md)
Prerequisite: StarMap for a draw hook.

## Background: how the game does it

See the [overview](overview.md) for `AlignmentTime`'s three branches. The mod copies the lead angle, rate and countdown branches into a pure kernel with no game types, so it is unit-testable offline, and reimplements `MathEx.ToOrbitAngle` and `ToDeviationAngle` locally. The ejection angle is the analytic patched-conic value (`pi - acos(-1/e)` with `e = 1 + rPark vInf^2 / mu`), not the game's own numerical one.
The live phase is the game's `Orbit.GetPhaseAngle(now)` for both bodies, already wrapped.[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L708-L754) [^ksa-mathex]

## Lifecycle and timing

The hook is `[StarMapAfterGui]` (`frame:draw-ui`, main thread), from `MapWindow.DrawCanvas`. The per-pair constants are built on every root build and every visibility rebuild, after the visual tree exists (the sibling filter reads the tree); the two live fields are refreshed once per frame while the window is shown, with `Universe.GetElapsedTime()`, before both the on-map badges and the overlay read them. The countdown does not tick while the window is closed.
Celestial orbits are analytic, so the evaluation is a pure read. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Choose pairs.** Take the nearest hub's other children as destinations, keeping only bodies the map currently draws; skip a pair whose orbit has a non-finite or non-positive semi-major axis or period (open-orbit comets) or whose synodic rate is zero (co-orbital bodies never realign).
2. **Compute the per-pair constants once per root:** retrograde = `GetRelativeInclination(target) > pi/2`; target (lead) phase; synodic rate; synodic period = `2 pi / |rate|`; transfer time and `vInf` reused from the delta-v cache; ejection angle. The kernel, with the retrograde branch reproduced by passing `wrap: !retrograde`:

```csharp
// DeltaVMap/Dv/TransferWindow.cs L29-L73
public static double TargetPhaseAngle(double aSource, double aTarget, bool retrograde)
{
    double lead = Math.PI * (1.0 - Math.Pow((aSource + aTarget) / (2.0 * aTarget), 1.5));
    return retrograde ? (TwoPi - ToOrbitAngle(lead)) : ToOrbitAngle(lead);
}

// The relative angular rate between the two orbits. Prograde subtracts the source rate
// from the target rate; retrograde adds them, because the bodies sweep opposite ways and
// the window recurs faster. The source term is dropped when its period is zero, guarding a
// parking-orbit source with no defined period. The game applies that guard on its prograde
// and same-SOI paths; it is kept here on the retrograde path too, which never differs in
// practice because a retrograde sibling always has a non-zero period.
public static double SynodicRate(double periodSource, double periodTarget, bool retrograde)
{
    double rateSource = (periodSource != 0.0) ? (TwoPi / periodSource) : 0.0;
    double rateTarget = TwoPi / periodTarget;
    return retrograde ? (rateTarget + rateSource) : (rateTarget - rateSource);
}

// How often the window recurs: the time for the relative angle to sweep a full turn.
public static double SynodicPeriod(double periodSource, double periodTarget, bool retrograde)
{
    return TwoPi / Math.Abs(SynodicRate(periodSource, periodTarget, retrograde));
}

// Seconds until the next departure window, given the current and target phase angles and
// the synodic rate. The gap is the angle the configuration still has to close; the wrap
// folds it into a single relative revolution so the result is the soonest window, never a
// past one. The wrap flag is true for the prograde and same-SOI cases (the game normalizes
// the gap there) and false for retrograde, where the game divides the raw gap directly
// without normalizing; passing it through faithfully keeps the result equal to the stock
// planner. The optional offset shifts the target phase, exposed by the game as a degrees
// offset and kept at zero by default.
public static double TimeToWindowSeconds(double currentPhase, double targetPhase, double synodicRate, double offset = 0.0, bool wrap = true)
{
    double gap = currentPhase - (targetPhase + offset);
    if (wrap)
    {
        if (gap > 0.0 && synodicRate > 0.0)
            gap -= TwoPi;
        if (gap < 0.0)
            gap += TwoPi;
    }
    return Math.Abs(gap / synodicRate);
}
```

3. **Refresh the live fields each frame** from `Orbit.GetPhaseAngle(now)` for both bodies; the current phase is `ToOrbitAngle(target - source)` (inverted for retrograde); the countdown is `TimeToWindowSeconds`:

```csharp
// DeltaVMap/Model/TransferWindows.cs L161-L179
public static void Refresh(TransferWindowInfo info, UniverseTime now)
{
    try
    {
        if (info.Source is not IOrbiter source || info.Target is not IOrbiter target)
            return;

        double sourcePhase = source.Orbit.GetPhaseAngle(now).Value();
        double targetPhase = target.Orbit.GetPhaseAngle(now).Value();
        info.SourcePhaseAngleNow = sourcePhase;
        info.TargetPhaseAngleNow = targetPhase;

        double raw = MathEx.ToOrbitAngle(targetPhase - sourcePhase);
        double currentPhase = info.Retrograde ? (TransferWindow.TwoPi - raw) : raw;

        info.CurrentPhaseAngle = currentPhase;
        info.TimeToWindowSeconds = TransferWindow.TimeToWindowSeconds(
            currentPhase, info.TargetPhaseAngle, info.SynodicRate, offset: 0.0, wrap: !info.Retrograde);
    }
```

4. **Draw the list** in a 3-column `BeginTable` with `ScrollY` and a frozen header row, the soonest row tinted, a cached tooltip, and a footer `Selectable` with a stable id that collapses to "next: body ~time".
5. **Draw the polar clock** on `ImGui.GetWindowDrawList()`: each body a dot on a ring whose radius is the log of its semi-major axis, at its current phase angle (screen Y flipped with `-sin`); for the emphasized sibling an amber arc and hollow marker at `sourcePhase + lead` where the target must be at departure.
6. **Sync hover** between the list, the clock, the map node and the 3D overlay, and have the overlay report whether the mouse is over it so the canvas suppresses pan and selection underneath.
7. **Never allocate in the draw**: hold every string in a cached text rebuilt only when a key derived from the shown value changes.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Dv/TransferWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/TransferWindow.cs) | pure kernel: lead angle, synodic rate and period, countdown, ejection angle |
| [DeltaVMap/Model/TransferWindows.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferWindows.cs) | sibling discovery, per-pair constants, per-frame refresh |
| [DeltaVMap/Model/TransferWindowInfo.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferWindowInfo.cs) | static and live fields of one window |
| [DeltaVMap/Render/TransferWindowRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowRenderer.cs) | list table, footer toggle, polar clock face |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L488-L822) | overlay placement, resize handles, refresh, rebuild, badges |

## Variations and gotchas

- **Prograde wraps; retrograde does not.** The game's retrograde branch adds the rates and divides the raw angle gap; copy that quirk if you want to agree with the planner.
- **The synodic rate is signed**: the wrap logic depends on it (`gap > 0 && rate > 0` subtracts a turn).
- **Same-SOI and sibling transfers use different radii** in the lead angle: the same-SOI branch takes the source's current CCI radius (`celestial.GetStateVectorsAt(startTime).PositionCci.Length()`) and measures the current phase from `transferInfo.Vehicle.Orbit`, while the prograde and retrograde branches use `Source.Orbit.SemiMajorAxis` and the source's own phase; the mod's sibling windows are the prograde/retrograde cases.
- Offsets (`offsetDegrees`) shift the target phase; the planner uses it for the departure margin.

## See also

- [Overview](overview.md); [multi-pass schedule](compute-transfer-windows-2.md)
- [Transfer windows list and live polar phase clock](../../../../mods/ksa-deltavmap/features/transfer-windows-and-phase-clock.md)
- [Estimate transfer delta-v in closed form with patched conics](../estimate-transfer-delta-v-with-patched-conics/overview.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-mathex]: KSA.MathEx (decompiled, build 2026.10.10.5554)
