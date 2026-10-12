---
type: How-To
title: Draw a craft-centred polar scope and a cached hill-shaded terrain map
description: Build a north-up local frame from a celestial's rotation axis, plot contacts on a polar scope and render a 64x64 height grid as shaded draw-list cells, re-sampling only when needed
category: ui
group: draw-lists
topic: draw-a-chart-with-imdrawlist
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/radar-scope-and-relief-map]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImDrawListPtr, Brutal.ImGuiApi.ImGui, KSA.Celestial, KSA.Vehicle, KSA.Universe]
tags: [scope, radar, relief-map, terrain, height-field, east-north-up, draw-list]
---
KSArmory draws two map-like instruments in ordinary ImGui windows without a texture or render target: a radar scope (a plan-position indicator, craft-centred, north-up, polar) and a relief map (a north-up square of hill-shaded terrain under the craft). Every pixel is a draw-list primitive on the window's own list, laid out in a square reserved with `ImGui.Dummy`. This article covers the two parts that are not drawing: the local east-north-up frame, and the cached height grid. Demonstrated by [Radar PPI scope and shaded-relief terrain map drawn with ImGui primitives](../../../../mods/ksarmory/features/radar-scope-and-relief-map.md).

## Background: how the game does it

World positions in KSA are ecliptic (`Celestial.GetPositionEcl`, [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L386)[^ksa-celestial]); a map needs a local horizontal frame on a body. The game gives you the body's rotation axis as a vector (`Celestial.GetRotationAxisCce()`, [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L615-L618)[^ksa-celestial]) and a terrain height query along a direction (`GetTerrainHeightFromDirCce(direction, accurate)`, [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)[^ksa-celestial]):

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
{
	double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
	return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
}
```

`accurate: false` samples the height map bilinearly instead of bicubically ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L797-L803)[^ksa-celestial]), and a body without a height map returns `0.0`, which a map must not mistake for sea level. North on the map is the body's pole, not the ecliptic pole; on a tilted body (23 degrees on Earth, per the mod) the difference is visible everywhere.

## Lifecycle and timing

Both instruments are drawn each frame from KSArmory's UI pass, which is a `[StarMapAfterGui]` method: `frame:draw-ui`, inside the `if (DrawUI)` block between `PrepareImGui` and `ImGui.Render()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2286)[^ksa-program]). The scope reads track state as rebuilt by the mod's own per-frame step; the map samples terrain on the main thread during the GUI pass, only while its window is open. Nothing is drawn or sampled while the HUD is hidden. The cache counts frames rather than simulated seconds, so a paused game still picks up streaming terrain. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the local frame from the rotation axis.** East is `axis x up`, north is `up x east`; it fails at the poles, where the axis and local up coincide, and the caller must say so rather than draw a compass pointing nowhere:

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

`ToLocal` differences a position against the anchor; `ToLocalDirection` is for velocities and must not subtract the anchor ([src/KSArmory/Sim/TerrainMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainMap.cs#L48-L65)). A hostile's heading uses velocity minus the ground's velocity, never the ecliptic velocity, which carries the planet's orbital motion.

2. **Convert a contact to scope coordinates.** Bearing is compass-style `Atan2(east, north)`; swapping the arguments mirrors the scope silently. Range is horizontal ground range. `Plot` clamps to the rim and flips north to -Y:

```csharp
// src/KSArmory/Sim/ScopeGeometry.cs L26-L30
public static double BearingRad(double east, double north)
{
    double bearing = Math.Atan2(east, north);
    return bearing < 0.0 ? bearing + Math.Tau : bearing;
}
```

```csharp
// src/KSArmory/Sim/ScopeGeometry.cs L54-L66
public static float2 Plot(double bearingRad, double range, double scopeRange)
{
    if (!double.IsFinite(bearingRad) || !double.IsFinite(range) || !(scopeRange > 0.0))
    {
        return default;
    }

    double unit = Math.Clamp(range / scopeRange, 0.0, 1.0);

    // North up the screen, which is -Y; east to the right, which is +X.
    return new float2((float)(unit * Math.Sin(bearingRad)),
                      (float)(-unit * Math.Cos(bearingRad)));
}
```

3. **Reserve the square and draw the face.** Take the available region, the cursor's screen position and the window draw list; draw the face, rings, rim, cardinal spokes with letters, then the contacts; finish with `Dummy` so layout continues below:

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
// src/KSArmory/Ksa/Ui/UiScope.cs L154-L180
private static void DrawScopeFace(ImDrawListPtr draw, float2 centre, float radius)
{
    draw.AddCircleFilled(centre, radius, ScopeFace, 64);

    foreach (float ring in ScopeGeometry.Rings)
    {
        draw.AddCircle(centre, radius * ring, ScopeGrid, 64, 1.0f);
    }

    draw.AddCircle(centre, radius, ScopeRim, 64, 1.6f);

    // Cardinal spokes and their letters, so a bearing can be read off without counting.
    ReadOnlySpan<string> marks = ["N", "E", "S", "W"];
    for (int i = 0; i < 4; i++)
    {
        double bearing = i * Math.PI / 2.0;
        float2 to = Face(centre, radius, ScopeGeometry.Plot(bearing, 1.0, 1.0));

        draw.AddLine(centre, to, ScopeGrid, 1.0f);

        float2 label = Face(centre, radius * 0.90f, ScopeGeometry.Plot(bearing, 1.0, 1.0));
        draw.AddText(new float2(label.X - 4f, label.Y - 7f), ScopeRim, marks[i]);
    }

    // The site itself, dead centre by construction.
    draw.AddCircleFilled(centre, 3f, ScopeSelf);
}
```

4. **Draw contacts, clamped and marked.** A contact beyond the range stays on the rim with an outward tick so "that way" is not read as "there"; the lock gets a ring and a beam. Symbols are geometry (lines, `AddNgon`, `AddTriangle`) because the font may not carry a glyph:

```csharp
// src/KSArmory/Ksa/Ui/UiScope.cs L236-L252
for (int i = 0; i < system.Radar.Tracks.Count; i++)
{
    Track track = system.Radar.Tracks[i];

    double3 offset = frame.ToLocal(track.PositionEcl);
    double bearing = ScopeGeometry.BearingRad(offset.X, offset.Y);
    double ground = ScopeGeometry.GroundRange(offset.X, offset.Y);

    bool beyond = ScopeGeometry.Beyond(ground, range);
    float2 at = Face(centre, radius, ScopeGeometry.Plot(bearing, ground, range));

    bool isLocked = locked is not null && ReferenceEquals(track, locked);
    ScopeGeometry.Blip symbol = SymbolOf(track, policy);
    uint colour = isLocked ? ScopeLocked
                : symbol == ScopeGeometry.Blip.Hostile ? ScopeHostile
                : track.IsThreat ? ScopeThreat
                : ScopeBlip;
```

5. **Sample terrain into a cached grid.** One refresh is `cells * cells` height queries (4096 at 64x64). Re-sample only when the grid is missing, the span or cell count changed, a timer of 600 frames expired, or the anchor drifted more than `max(10 m, span / 10)`:

```csharp
// src/KSArmory/Ksa/TerrainMapScan.cs L65-L104
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

    double3 centre;
    double3 axis;
    try
    {
        centre = body.GetPositionEcl();
        axis = body.GetRotationAxisCce();
    }
    catch (Exception e)
    {
        Log.Warn($"map: cannot read the body's frame -- {e.Message}");
        return;
    }

    if (MapFrame.TryAt(centre, anchorEcl, axis) is not { } frame)
    {
        // At a pole, where there is no bearing. Nothing to draw rather than a rose pointing
        // at an arbitrary perpendicular.
        Frame = null;
        return;
    }

    Sample(new TerrainHeights(body), frame, span, cells);

    _sampledAt = anchorEcl;
    _sinceScan = 0;
}
```

6. **Sample cell centres along directions from the body's centre**, keeping unreadable cells as unknown, not zero:

```csharp
// src/KSArmory/Ksa/TerrainMapScan.cs L120-L142
for (int j = 0; j < cells; j++)
{
    double north = TerrainMap.CellOffset(j, cells, span);

    for (int i = 0; i < cells; i++)
    {
        double east = TerrainMap.CellOffset(i, cells, span);
        int at = j * cells + i;

        if (heights.TryHeight(frame.DirectionAt(east, north), out double metres))
        {
            Height[at] = metres;
            Known[at] = true;
            lowest = Math.Min(lowest, metres);
            highest = Math.Max(highest, metres);
        }
        else
        {
            Known[at] = false;
            unknown++;
        }
    }
}
```

```csharp
// src/KSArmory/Ksa/TerrainHeights.cs L19-L33
public bool TryHeight(double3 dirFromCentre, out double metres)
{
    metres = 0.0;

    try
    {
        metres = _body.GetTerrainHeightFromDirCce(dirFromCentre, accurate: _accurate);

        return double.IsFinite(metres);
    }
    catch
    {
        return false;
    }
}
```

7. **Hill-shade from the neighbours.** The surface normal comes from central differences of the four neighbours, lit from the north-west; no extra samples are needed:

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

8. **Draw one `AddRectFilled` per cell** (4096 rectangles per frame), mixing 78 % shading with 22 % normalised height, unknown cells dark:

```csharp
// src/KSArmory/Ksa/Ui/UiMap.cs L141-L175
private void DrawRelief(ImDrawListPtr draw, float2 origin, float side)
{
    int cells = _map.Cells;
    if (cells < 2) return;

    float step = side / cells;
    double lift = Math.Max(1.0, _map.Highest - _map.Lowest);

    for (int j = 0; j < cells; j++)
    {
        // Row 0 is the southern edge and the screen's bottom, so rows are drawn up the square.
        float top = origin.Y + side - (j + 1) * step;

        for (int i = 0; i < cells; i++)
        {
            float left = origin.X + i * step;
            float2 a = new(left, top);
            float2 b = new(left + step + 1f, top + step + 1f);

            if (_map.ReliefAt(i, j) is not { } relief || _map.At(i, j) is not { } height)
            {
                draw.AddRectFilled(a, b, MapUnknown);
                continue;
            }

            // Relief carries the shape; a little height on top separates a lit high ridge from
            // a lit low one, which pure shading cannot.
            double band = (height - _map.Lowest) / lift;
            float tone = (float)Math.Clamp(relief * 0.78 + band * 0.22, 0.0, 1.0);

            draw.AddRectFilled(a, b, ImGui.ColorConvertFloat4ToU32(
                new float4(tone * 0.52f + 0.05f, tone * 0.60f + 0.06f, tone * 0.55f + 0.08f, 1f)));
        }
    }
}
```

9. **Overlay graticule, contacts and heading** with the same unit-square mapping (`TerrainMap.ToUnitSquare`, `EdgeToward` for off-map contacts), as in `DrawGraticule` and `DrawContacts` ([src/KSArmory/Ksa/Ui/UiMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiMap.cs#L177-L215)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/UiScope.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiScope.cs) | Scope window and drawing |
| [src/KSArmory/Sim/ScopeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ScopeGeometry.cs) | Bearing, ground range, plot, range steps (pure, unit-tested) |
| [src/KSArmory/Ksa/Ui/UiMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiMap.cs) | Map window, relief cells, graticule, contacts |
| [src/KSArmory/Ksa/TerrainMapScan.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainMapScan.cs) | Cached height grid and staleness rules |
| [src/KSArmory/Sim/TerrainMap.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainMap.cs) | `MapFrame`, spans, cell offsets, hillshade |
| [src/KSArmory/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainHeights.cs) | Height lookup over `GetTerrainHeightFromDirCce` |

## Variations and gotchas

- **Never sample per frame.** The mod's own comments note 4096 lookups per refresh is the cost the cache exists to avoid and that the per-frame cost was never measured.
- **Unknown is not zero.** A height field that does not answer must stay unknown, or a whole square draws at sea level; `ReliefAt` returns null when any neighbour is unknown so the data edge does not shade as a cliff.
- **Heights only.** There is no ground-texture query in the checked code, so the map has no coastlines or colours.
- **Poles.** No bearing exists; show a message.
- **Row order.** Row 0 is the southern edge and the screen's bottom, so rows are drawn up the square.
- **Cost.** 4096 filled rectangles per frame are cheap; a much finer grid would need a texture (see the rendering category).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of the chart topic](overview.md)
- [Approach 1: time-series chart](draw-a-chart-with-imdrawlist-1.md)
- [Approach 2: function plot](draw-a-chart-with-imdrawlist-2.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- Mod feature: [Radar PPI scope and shaded-relief terrain map drawn with ImGui primitives](../../../../mods/ksarmory/features/radar-scope-and-relief-map.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
