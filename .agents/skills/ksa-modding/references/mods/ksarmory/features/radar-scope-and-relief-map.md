---
type: Mod Feature
title: Radar PPI scope and shaded-relief terrain map drawn with ImGui primitives
description: A craft-centred polar radar scope and a 64x64 hill-shaded relief map are drawn only with window draw-list primitives in a north-up frame from the body's rotation axis, the heights resampled from the engine's height field only on movement, span change or every ten seconds
mod: ksarmory
category: ui
techniques:
  - starmap-lifecycle
  - imgui-window
  - imgui-drawlist
  - terrain-sampling
  - cached-grid
  - local-horizontal-frame
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Celestial
  - KSA.Vehicle
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListExtensions
  - KSA.Program
  - KSA.Universe
tags: [imgui, draw-list, scope, ppi, map, terrain, relief, hillshade, local-frame, cache]
---

KSArmory draws two map-like instruments in ordinary ImGui windows, and neither uses a texture or
a render target: every pixel is a draw-list primitive added to the window's own draw list, laid
out in a square reserved with `ImGui.Dummy`.[^ksarmory-uiscope][^ksarmory-uimap]

- **Radar scope** (a plan-position indicator): craft-centred, north-up, polar. Range rings at a
  quarter, half and three quarters of the selected range, cardinal spokes with N/E/S/W, the search
  sweep drawn at the array's actual angle, and one symbol per radar track by allegiance (X
  friendly/neutral, open triangle unknown, heading-pointing triangle hostile, M for a round), with
  the lock ringed and joined to the centre by a "track beam". Shown at the head of the Radar tab
  and optionally in its own window.
- **Relief map** for an optical director: a north-up square 0.5 to 10 km across around the ground
  under the craft, 64 by 64 hill-shaded cells sampled from the body's height field, 500 m range
  rings, where the sight is looking, the director's tracks, and the craft's heading over the ground.

The reusable pieces are the local east-north-up `MapFrame` built from the body's rotation axis, a
cached height grid paid on movement rather than per frame, cheap hill shading from neighbour
differences, and "clamp to the rim and mark it" for off-scale contacts.

## How it works

**North-up local frame** (`MapFrame.TryAt(centreEcl, anchorEcl, axis)`): `up` = unit radial from the
body centre, `east = unit(axis × up)`, `north = up × east`. The axis is
`Celestial.GetRotationAxisCce()`, so north is the body's pole, not the ecliptic pole (23° off on Earth
per the mod). It fails at the poles (no bearing) and the window says so. `ToLocal` differences a
point against the anchor; `ToLocalDirection` is for velocities (no anchor subtracted).[^ksarmory-terrainmap]

**Scope geometry** (`ScopeGeometry`, `Sim/`, tested):[^ksarmory-scopegeometry]

| Function | Rule |
|---|---|
| `BearingRad(east, north)` | `Atan2(east, north)`, compass convention (swapping arguments mirrors the scope) |
| `GroundRange` | Horizontal range only (a PPI plots ground track; slant range would keep an overflying craft off-centre) |
| `Plot(bearing, range, scopeRange)` | Clamp to the rim; screen `x = u·sin b`, `y = −u·cos b` (north up is −Y) |
| `RangeSteps` | Full reach (rounded up), half, quarter of the set's own `Range` |
| `SweepBearings` | One sweep line per radiating array face, from craft heading + turret bearing + array spin |
| `SymbolFor(isRound, allegiance)` | Blip type from the scope's own IFF classification |

**Scope drawing** (`DrawScopeFor`): reserve `side × side` from `GetContentRegionAvail`, take
`GetCursorScreenPos` as origin, `GetWindowDrawList()`, then `AddCircleFilled` face, `AddCircle`
rings and rim, `AddLine` spokes, `AddText` labels, sweep lines (skipped when the radar is silenced),
contact symbols with `AddLine`/`AddNgon`/`AddTriangle`/`AddText`, an outward tick for a clamped
contact, a ring and beam on the lock, then `ImGui.Dummy(side, side)` so layout continues below,
then a text key. The hostile triangle points along velocity minus `KsaWorld.GroundVelocityAt`
(ecliptic velocity would point everything the same way).

**Map sampling** (`TerrainMapScan.Update(body, anchor, span, cells)`): re-sample only when the grid
is missing, the span or cell count changed, the anchor drifted more than `max(10 m, span/10)`, or 600
frames passed (roughly 10 s at 60 fps, for terrain streaming in). A sample walks 64 × 64 cell centres
and asks `TerrainHeights.TryHeight(frame.DirectionAt(east, north))`, which calls
`Celestial.GetTerrainHeightFromDirCce(dir, accurate: false)`; unreadable cells are kept as *unknown*
rather than zero, and the scan time is logged and shown.[^ksarmory-terrainmapscan][^ksarmory-terrainheights]

**Map drawing** (`DrawRelief`, every frame while open): one `AddRectFilled` per cell (4096), tone =
`0.78 × hillshade + 0.22 × normalised height`; hillshade from `TerrainMap.Relief` using central
differences of the four neighbours and a light from the north-west, 45° up; unknown cells dark.
Then graticule (`AddCircle` every 500 m, cross-hair, `AddRect` border, "N"), the craft mark, the
sight line (`TryOpticViewEcl` to the ground, clipped to the edge), tracks (squares, or edge
triangles for off-map), and a heading arrow from velocity over the ground.

**Windows.** One scope window and one map window for the whole panel: opening one system's scope
(`TakeScope`) or one head's map (`TakeMap`) closes any other. The scope's open flag and range live on the per-system
`SystemConfig` (`ScopeOpen`, `ScopeRangeMetres`) and are saved with that system's settings
(`SystemSettings`); the map's (`MapOpen`, `MapSpanMetres`, default 2 km) live on the per-head
`OpticConfig`.

## Lifecycle and timing

Both instruments are drawn from `Ui.Draw()` (`DrawScopeWindow`, `DrawMapWindow`, and `DrawScope`
inside the Radar tab), which `KSArmoryMod.OnAfterGui` calls right after `StepOnce`.
`[StarMapAfterGui]` is a Harmony postfix on `Program.OnDrawUiViewports` (Program.cs L2268), inside
`if (DrawUI)` (L2265-L2269), between `PrepareImGui` (L2261) and `ImGui.Render()` (L2286), so window
and draw-list calls are legal.[^ksarmory-mod][^ksa-program]

- **What the scope shows.** `Radar.Tracks` as rebuilt by this frame's `StepOnce` (only on frames
  that advanced simulated time), positioned in a frame built from `body.GetPositionEcl()` and the
  system's `MountEcl` sampled this frame. Everything is state at `Universe.GetElapsedTime()`
  (Universe.cs L2399), after `PrepareFrame` applied the vehicle step (`Universe.ApplyVehicleSolvers`,
  L1922/L1948) and dispatched the next.[^ksa-universe]
- **Where the map samples terrain.** `TerrainMapScan.Update` runs on the main thread inside the GUI
  pass, only while the map window is open, and counts frames rather than simulated seconds, so a
  paused game still refreshes it.
- **Hidden UI.** With F2 or a screenshot (`Program.DrawUI == false`) neither is drawn and the map
  does not sample.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/UiScope.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiScope.cs#L50-L129) | Scope window, `DrawScopeFor` layout |
| [src/KSArmory/Ksa/Ui/UiScope.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiScope.cs#L154-L351) | Face, sweep, contact symbols, hostile heading, track beam |
| [src/KSArmory/Ksa/Ui/UiScope.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiScope.cs#L362-L396) | IFF symbol, emitter check, `ScopeFrame` |
| [src/KSArmory/Sim/ScopeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ScopeGeometry.cs) | Bearing, ground range, plot, range steps, sweep, symbols |
| [src/KSArmory/Ksa/Ui/UiMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiMap.cs) | Map window, relief cells, graticule, contacts, heading arrow, legend |
| [src/KSArmory/Ksa/TerrainMapScan.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainMapScan.cs) | Cached height grid, staleness rules, unknown cells |
| [src/KSArmory/Sim/TerrainMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainMap.cs) | `MapFrame`, spans, cell offsets, hillshade, heading, refresh distance |
| [src/KSArmory/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainHeights.cs) | Height lookup over `GetTerrainHeightFromDirCce` |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L283-L292) | Where the scope and map windows are submitted |

## Snippets

```csharp
// src/KSArmory/Ksa/Ui/UiScope.cs L112-L126
        float side = Math.Max(180f, Math.Min(ImGui.GetContentRegionAvail().X,
                                             ImGui.GetContentRegionAvail().Y - 120f));
        float2 origin = ImGui.GetCursorScreenPos();
        float radius = side * 0.5f;
        float2 centre = new(origin.X + radius, origin.Y + radius);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        DrawScopeFace(draw, centre, radius);
        // A silent set is not scanning, so it paints no sweep.
        if (!policy.RadarSilent) DrawScopeSweep(draw, centre, radius, system, local);
        DrawScopeContacts(draw, centre, radius, system, policy, local, range);

        ImGui.Dummy(new float2(side, side));
```

```csharp
// src/KSArmory/Sim/TerrainMap.cs L26-L40
    public static MapFrame? TryAt(double3 centreEcl, double3 anchorEcl, double3 axisEcl)
    {
        double3 radial = anchorEcl - centreEcl;
        double radius = Vec.Len(radial);
        if (!(radius > 1.0) || !Vec.IsFinite(radial)) return null;

        double3 up = radial / radius;

        double3 east = Vec.Cross(axisEcl, up);
        if (Vec.Len2(east) < 1e-12 || !Vec.IsFinite(east)) return null;

        east = Vec.Unit(east);

        return new MapFrame(centreEcl, up, east, Vec.Cross(up, east), radius);
    }
```

```csharp
// src/KSArmory/Ksa/TerrainMapScan.cs L65-L77
    public void Update(Celestial? body, double3 anchorEcl, double span, int cells)
    {
        if (body is null || !(span > 0.0) || cells < 2) return;

        _sinceScan++;

        bool stale = Frame is null
                     || Math.Abs(span - Span) > 1e-6
                     || cells != Cells
                     || _sinceScan >= RefreshFrames
                     || Vec.Len(anchorEcl - _sampledAt) > TerrainMap.RefreshDistance(span);

        if (!stale) return;
```

```csharp
// src/KSArmory/Sim/TerrainMap.cs L161-L177
    public static double Relief(double west, double east, double south, double north, double metresPerCell)
    {
        if (!(metresPerCell > 0.0)) return 0.5;

        // The surface normal in local terms, with the height differences over twice the spacing.
        double dzde = (east - west) / (2.0 * metresPerCell);
        double dzdn = (north - south) / (2.0 * metresPerCell);

        double3 up = Vec.Unit(new double3(-dzde, -dzdn, 1.0));
        if (Vec.Len2(up) < 0.5) return 0.5;

        // Light from the north-west and well up, which is the convention every relief map uses:
        // lit from below reads as terrain turned inside out.
        double3 light = Vec.Unit(new double3(-0.5, 0.5, 0.7));

        return Math.Clamp((Vec.Dot(up, light) + 0.15) / 1.15, 0.0, 1.0);
    }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCce(dir, accurate)` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L800) | Height per cell; converts to Ccf and samples the height map (bilinear when `accurate: false`)[^ksa-celestial] |
| `Celestial.GetRotationAxisCce`, `Celestial.GetPositionEcl` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L615) | North axis and frame origin (L386) |
| `Vehicle.Asmb2Ego`, `Vehicle.GetVelocityEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502) | Craft heading for the sweep; velocity over ground (L960)[^ksa-vehicle] |
| `ImGui.Begin`, `GetContentRegionAvail`, `GetCursorScreenPos`, `Dummy`, `ColorConvertFloat4ToU32` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6014-L6090) | Window and layout reservation (`ColorConvertFloat4ToU32` at L8105)[^brutal-imgui] |
| `ImGui.GetWindowDrawList`, `AddRectFilled`, `AddCircle(Filled)`, `AddLine`, `AddNgon(Filled)`, `AddTriangle`, `AddRect`, `AddText` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L222) | Every mark on both instruments[^brutal-drawlistext] |
| `Program.OnDrawUiViewports`, `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269) | Where and whether it draws |

## Pitfalls and version notes

- **No borrowed map.** The mod's comment says KSA offers no map view a mod can borrow, so terrain is
  heights only: no coastline or colour. Not checked beyond the absence of a ground-texture query
  in `GetTerrainHeightFromDirCcf`.
- **Do not sample per frame.** 4096 lookups per refresh is the cost the cache exists to avoid;
  `SensorProfile.TerrainSamples` defaults to 0 for the same unmeasured cost.
- **Keep unknown as unknown.** A height field that will not answer returns false, not zero; zero
  would draw a sea-level square. `ReliefAt` returns null when any neighbour is unknown so the data's
  edge does not shade as a cliff.
- **Compass, not maths.** `Atan2(east, north)` and north = −Y on screen; both have tests.
- **Ground velocity, not ecliptic.** Heading arrows use velocity minus the ground's velocity
  under the point; the ecliptic velocity carries the planet's orbital motion.
- **Font coverage.** Symbols are drawn as geometry (not a Greek delta) because, per the mod, the font
  may not carry one.
- **One instance each.** Only one scope window and one map window exist; opening another closes the
  first, so per-installation flags do not stack windows.

## See also

- [Radar search cone, masking and lock dwell](./radar-search-and-lock.md)
- [IFF teams and allegiance](./iff-teams-and-allegiance.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Optical director heads](./optical-director-heads.md)
- [Terrain ground impact test](./terrain-ground-impact-test.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Draw a craft-centred polar scope and a cached hill-shaded terrain map](../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/draw-a-chart-with-imdrawlist-3.md) - how-to article demonstrating this feature

[^ksarmory-uiscope]: src/KSArmory/Ksa/Ui/UiScope.cs
[^ksarmory-uimap]: src/KSArmory/Ksa/Ui/UiMap.cs
[^ksarmory-terrainmap]: src/KSArmory/Sim/TerrainMap.cs
[^ksarmory-scopegeometry]: src/KSArmory/Sim/ScopeGeometry.cs
[^ksarmory-terrainmapscan]: src/KSArmory/Ksa/TerrainMapScan.cs
[^ksarmory-terrainheights]: src/KSArmory/Ksa/TerrainHeights.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled)
[^brutal-drawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled)
