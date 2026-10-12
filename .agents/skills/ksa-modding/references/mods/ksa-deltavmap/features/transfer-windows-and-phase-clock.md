---
type: Mod Feature
title: Transfer windows list and live polar phase clock
description: For the map root and every shown sibling it computes the Hohmann lead angle, live phase, countdown, synodic period and ejection angle with the same branches as the game's OrbitalTransfers.AlignmentTime, and draws a resizable list plus a polar phase clock whose hover lights the matching map node
mod: ksa-deltavmap
category: flight-and-orbits
techniques: [imgui-drawlist, imgui-window, closed-form-orbital-math]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Orbit, KSA.MathEx, KSA.OrbitalTransfers, KSA.Universe, KSA.IOrbiter, KSA.ConsoleStyle]
tags: [flight-and-orbits, transfer-window, phase-angle, synodic-period, ejection-angle, imgui, draw-list]
---

The timing companion to the delta-v map: for the root body and every sibling sharing its parent
(the other planets for a planet root, the other moons for a moon root) it shows the optimal phase
angle, the live current phase, a countdown to the next window, the synodic period, the ejection
angle and the transfer time, in a compact list plus a live polar "phase clock". Mechanism: a pure
closed-form kernel (`Dv/TransferWindow.cs`) mirroring the branch structure of the game's
`OrbitalTransfers.AlignmentTime`, a game-typed builder that fixes the per-pair constants once per
root and refreshes two live fields every frame, and an ImGui overlay drawn with the window draw
list.[^dvmap-transferwindow][^dvmap-transferwindows][^dvmap-transferwindowrenderer]

## How it works

**Pairs.** `TransferWindows.Build(cache, root, visibleBodyIds, now)` re-roots at the map root,
takes the nearest hub (`Spine[0]`) and treats its `OtherChildren` as destinations, keeping only
bodies the map currently draws as their own lane, so the list mirrors the canvas (the "+N" swarm
never floods it, a searched body appears). A pair is skipped when either orbit has a non-finite
or non-positive semi-major axis or period (open-orbit comets), or when the synodic rate is zero
(co-orbital bodies never realign).

**Per-root constants** (`TransferWindowInfo`, computed once in `RebuildAt`):[^dvmap-transferwindowinfo]

| Field | Formula / source |
|---|---|
| Retrograde | `sourceOrbit.GetRelativeInclination(targetOrbit) > PI/2` |
| Target (lead) phase | `PI * (1 - ((aS + aT) / (2 aT))^1.5)` wrapped to (-PI, PI]; retrograde uses `2PI - wrapped` |
| Synodic rate | `2PI/Tt - 2PI/Ts` (prograde, source term dropped when `Ts == 0`), `2PI/Tt + 2PI/Ts` (retrograde) |
| Synodic period | `2PI / abs(rate)` |
| Transfer time, v_inf | Reused from `DvCache.GetTransfer(source, target)`: `TransferTimeSeconds` and `DepartDv` |
| Ejection angle | `PI - acos(-1/e)` with `e = 1 + rPark v_inf^2 / mu`, `rPark` = root's low-orbit radius, `mu` = root body `Mu`; ahead of prograde when the target is outer |
| Approximate | Cached edge approximate, or either eccentricity above the circular threshold |

The lead angle, rate and countdown branches are copied from `OrbitalTransfers.AlignmentTime`,
which has three branches: same-SOI, prograde and retrograde (relative inclination above PI/2). The
retrograde branch in the game adds the two rates and divides the raw angle gap without wrapping;
the mod passes `wrap: !info.Retrograde` to reproduce that.[^ksa-orbitaltransfers] The ejection
angle is the analytic patched-conic value (Bate-Mueller-White), not the game's own numerical
value, and the kernel reimplements `MathEx.ToOrbitAngle` / `ToDeviationAngle` locally so it stays
free of game types and unit-testable offline.[^ksa-mathex]

**Live fields.** `Refresh(info, now)` reads `Orbit.GetPhaseAngle(now)` for both bodies (the
game's own longitude-of-ascending-node + argument-of-periapsis + true-anomaly angle, already
wrapped), takes `MathEx.ToOrbitAngle(target - source)` (inverted for retrograde) as the current
phase, and computes the countdown with `TimeToWindowSeconds`.[^ksa-orbit]
`MapWindow.DrawCanvas` calls `RefreshTransferWindows()` exactly once per frame with
`Universe.GetElapsedTime()`, before both the on-map badges and the overlay read the values.

**The overlay.** `DrawTransferWindowOverlay` floats two bordered child windows in the bottom
left of the canvas, each pushed with `ImGuiCol.ChildBg` and guarded by try/catch/finally so the
child and style stacks stay balanced:

- The list panel (`TransferWindowRenderer.DrawOverlay`): two console checkbox rows (map badges,
  3D map-mode overlay), a 3-column `BeginTable` with `ScrollY` and
  `TableSetupScrollFreeze(0, 1)`, the soonest row tinted with `TableSetBgColor`, the name in
  `ConsoleStyle.Pending`, and a cached multi-line row tooltip. A full-width `Selectable` footer
  with a stable `###dvtwtoggle` id collapses and expands it; collapsed it reads
  "next: <body> ~<time>".
- The clock panel (`DrawClockPanel` / `DrawClockFace`): a square drawn on
  `ImGui.GetWindowDrawList()`. Each body is a dot on a ring whose radius is the log of its
  semi-major axis between the inner and outer ring, at its current phase angle (screen Y is
  flipped with `-sin`). For the emphasized sibling it draws hub-to-body lines, an amber arc and
  hollow marker at `sourcePhase + lead` (where the target must be at departure), and a bottom
  line "lead +X deg, now +Y, in ~T" that shortens itself when `CalcTextSize` says it would not
  fit. `ImGui.Dummy(side, side)` reserves the space.
- Resizable edges: `EdgeHandle` places an 8 px `InvisibleButton` strip just outside each free
  edge, sets `ImGuiMouseCursor.ResizeEW/NS` while hot, captures the panel size on
  `IsItemActivated`, and returns `start + GetMouseDragDelta(Left, 0)` while active. User sizes are
  kept until the root changes.

**Hover sync.** The overlay returns the body under the mouse (clock dot within 8 px, or a list
row). `MapWindow` stores it in `_windowsHoverBodyId`; after `HandleInput` it sets the canvas
`_hoverId` to that body's focus node, so the map node glows, and feeds it back next frame as the
emphasis hint so the clock highlights the same body. Emphasis order everywhere (list, clock, 3D
overlay) is hovered body, else the sibling on the selected route, else the soonest window. The
overlay also reports whether the mouse is over it, and the canvas suppresses hover, pan and
selection underneath.

**Allocation discipline.** Every string (row texts, footer label, clock title, decode line,
tooltip lines, on-map badge text) is held in a `CachedText` and rebuilt only when a key derived
from the shown value changes (for example `Format.DurationKey(seconds)`), so an open overlay
draws without allocating.[^dvmap-cachedtext]

## Lifecycle and timing

- Hook: `[StarMapAfterGui]`, main thread, `frame:draw-ui`, from `MapWindow.DrawCanvas`.
- Build: on every root build and every visibility rebuild (`RebuildAt` -> `RebuildTransferWindows`),
  after the visual tree exists, because the sibling filter reads the tree.
- Refresh: once per frame while the window is shown. Celestial orbits are analytic, so evaluating
  `GetPhaseAngle` at `Universe.GetElapsedTime()` is a pure read and safe while the vehicle
  solver runs on worker threads. The countdown does not tick while the window is closed; it is
  recomputed on the next open.
- Input suppression works because the overlay widgets are submitted after the full-canvas
  invisible button, which was created with `SetNextItemAllowOverlap`.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Dv/TransferWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/TransferWindow.cs) | Pure kernel: lead angle, synodic rate and period, countdown, ejection angle |
| [DeltaVMap/Model/TransferWindows.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferWindows.cs) | Sibling discovery, per-pair constants, per-frame refresh |
| [DeltaVMap/Model/TransferWindowInfo.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferWindowInfo.cs) | Static and live fields of one window |
| [DeltaVMap/Render/TransferWindowRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowRenderer.cs) | List table, footer toggle, polar clock face |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L488-L822) | Overlay placement, resize handles, refresh, rebuild, badges |

## Snippets

```csharp
// DeltaVMap/Dv/TransferWindow.cs L29-L73
public static double TargetPhaseAngle(double aSource, double aTarget, bool retrograde)
{
    double lead = Math.PI * (1.0 - Math.Pow((aSource + aTarget) / (2.0 * aTarget), 1.5));
    return retrograde ? (TwoPi - ToOrbitAngle(lead)) : ToOrbitAngle(lead);
}
// ...
public static double SynodicRate(double periodSource, double periodTarget, bool retrograde)
{
    double rateSource = (periodSource != 0.0) ? (TwoPi / periodSource) : 0.0;
    double rateTarget = TwoPi / periodTarget;
    return retrograde ? (rateTarget + rateSource) : (rateTarget - rateSource);
}
// ...
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

```csharp
// DeltaVMap/Render/TransferWindowRenderer.cs L284-L292
double lead = emphasisInfo.Retrograde
    ? (2.0 * Math.PI - emphasisInfo.TargetPhaseAngle)
    : emphasisInfo.TargetPhaseAngle;
double requiredAngle = sourceRef.SourcePhaseAngleNow + lead;
float2 reqPos = Polar(center, r, requiredAngle);

DrawArc(dl, center, minR * 0.72f, sourceRef.SourcePhaseAngleNow, requiredAngle, RequiredColor, 2.2f);
dl.AddLine(in center, in reqPos, RequiredColor, 1.5f);
dl.AddCircle(in reqPos, bigDot * 1.1f, RequiredColor, 18, 2f);
```

```csharp
// DeltaVMap/Render/MapWindow.cs L1165-L1169
// A clock dot or list row hovered in the overlay highlights that sibling's node on the
// map, reusing the canvas hover glow. Applied after HandleInput, which clears _hoverId
// while the cursor is over the overlay.
if (_windowsHoverBodyId != null)
    _hoverId = FindFocusNode(_windowsHoverBodyId)?.Id;
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `OrbitalTransfers.AlignmentTime` | [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs) | Not called; the formula the kernel mirrors so figures match the stock "Show Alignment" readout |
| `Orbit.GetPhaseAngle(UniverseTime)`, `GetRelativeInclination(Orbit)`, `SemiMajorAxis`, `Period`, `Eccentricity` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs) | Live phase, retrograde test, lead angle inputs |
| `MathEx.ToOrbitAngle` | [current/decomp/KSA/MathEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MathEx.cs) | Same angle wrap as stock for the live phase |
| `Universe.GetElapsedTime` | (see [map-mode transfer overlay](./map-mode-transfer-overlay.md)) | Sim time for the refresh |
| `ImDrawListPtr.AddCircle`, `AddCircleFilled`, `AddLine`, `AddText`, `AddTriangleFilled` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs) | Clock face primitives |

## Pitfalls and version notes

- If a game update changes `OrbitalTransfers.AlignmentTime`, these figures silently diverge from
  the stock planner; the mod's comment asks to re-verify on each update.
- The stock same-SOI branch (vehicle orbiting the source) is not reproduced: the mod always works
  body to body around a shared hub.
- The mod's comments say the binding "exposes no path-arc helper" and draws arcs as line
  segments. In build 2026.10.10.5554 `ImDrawListExtensions` does define `PathArcTo`,
  `PathArcToFast` and `PathArcToN`, so a modder can use those instead.[^imgui-drawlist-ext]
- Elliptical siblings are flagged with "*" because the lead angle uses the semi-major axis.
- The clock uses a log scale on semi-major axis so inner and outer planets both stay readable;
  it is a schematic, not to scale.

## See also

- [Map-mode transfer overlay](./map-mode-transfer-overlay.md)
- [Patched-conic transfer model](./patched-conic-transfer-model.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Mirror the branches of OrbitalTransfers.AlignmentTime with a live phase clock](../../../howto/flight-and-orbits/transfers/compute-transfer-windows/compute-transfer-windows-1.md) - how-to article demonstrating this feature

[^dvmap-transferwindow]: DeltaVMap/Dv/TransferWindow.cs
[^dvmap-transferwindows]: DeltaVMap/Model/TransferWindows.cs
[^dvmap-transferwindowinfo]: DeltaVMap/Model/TransferWindowInfo.cs
[^dvmap-transferwindowrenderer]: DeltaVMap/Render/TransferWindowRenderer.cs
[^dvmap-cachedtext]: DeltaVMap/Render/CachedText.cs
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-mathex]: KSA.MathEx (decompiled, build 2026.10.10.5554)
[^imgui-drawlist-ext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
