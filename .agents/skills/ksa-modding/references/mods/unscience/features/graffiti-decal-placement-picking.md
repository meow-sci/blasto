---
type: Mod Feature
title: One-shot click placement with mesh, cloth, kitten and terrain picking
description: An armed ImGui placement mode turns the next world click into a decal anchor by casting Cursor.GetEgoRay against live parachute cloth triangles, Part.RayCastEgo view meshes, a KittenEva bounding sphere and finally a CPU march over the accurate terrain height field
mod: unscience
submod: graffiti
category: rendering
techniques: [imgui-drawlist, starmap-lifecycle, raycast-picking]
hooks: [starmap StarMapAfterGui (via unscience ISubmod.RenderFloatingWindows)]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Cursor
  - KSA.Camera
  - KSA.Ray
  - KSA.Part
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.Parachute
  - KSA.ChuteClothSystem
  - KSA.ChuteClothTopology
  - KSA.BoundingSphere3D
  - KSA.Double3Ex
  - KSA.Celestial
  - KSA.Program
tags: [decals, picking, raycast, cursor, terrain, parachutes, kittens, imgui]
---

Graffiti's placement UX is "arm, then click the world": after **Place at Click...** a hint follows
the mouse, the next left click that is not over an ImGui window casts a ray from the cursor, and the
nearest surface within range becomes the decal's anchor. Esc cancels; a miss keeps placement armed so
a slightly-off click does not cost a round trip.[^graffiti-placement]

Mechanism in one line: `Cursor.GetEgoRay(Program.MainViewport)` is tested against four kinds of
target in one sweep (deployed canopy cloth, part view meshes, kitten bounding spheres, then terrain),
and the closest hit is converted into a frame that survives the next frame: part-local,
cloth-barycentric or geodetic.

## How it works

**Arming and the click** (`GraffitiSubmod.RenderFloatingWindows`, run every frame even when the
panel is closed):

- disarm in the editor (`Program.EditorFlag`) or on `ImGui.IsKeyPressed(ImGuiKey.Escape)`;
- draw a two-pass (shadowed) hint with `ImGui.GetForegroundDrawList().AddText` at the mouse;
- place only when `!ImGui.GetIO().WantCaptureMouse && ImGui.IsMouseClicked(Left)`, so a click on any
  ImGui window, including the graffiti panel, never places;
- success disarms (one-shot); a miss sets an error status and stays armed.[^graffiti-placement]

`PlaceAtCursor` first resolves the PNG to a bindless slot (fails fast if missing), then calls
`DecalPicker.TryPick(range)`, and turns the `PickResult` into a `DecalEntry` with an automatic
projection depth (`max(auto depth, pick.SuggestedMinDepth)`).[^graffiti-submod]

**The sweep** (`DecalPicker.TryPick`): bail on a missing main camera or a non-finite ray, then
`TryPickVehicle || TryPickTerrain`, keeping a running `best` distance so the nearest hit
wins. Triangle and sphere tests are the game's own `Ray` helpers.[^decal-picker][^ksa-ray]

| Target | Test | Anchor stored |
|---|---|---|
| Deployed canopy (every `Parachute` module of every vehicle, debris included) | Rebuild the canopy proxy from `Parachute.ClothPositionsFront`: apex fan to ring 0 plus two triangles per ring/spoke quad of the 8-ring x 16-spoke `ChuteClothTopology` (240 triangles), transformed with the same attach-point-to-ego matrix as `Parachute.DrawCanopy`; `Ray.RaycastWatertight` per triangle | Three node indices, barycentric weights, clicked side (`ClothNormalSign`), canopy index |
| Ordinary part | `Part.RayCastEgo` per top-level part (sphere broad phase, then the game's watertight triangle test over each sub-part's view mesh, the same call flight hover-picking uses) | Hit position and first-vertex normal in the **closest sub-part's** local frame, keyed by that sub-part's `InstanceId` |
| `KittenEva` | `Ray.Raycast(BoundingSphere3D)` at the root part's ego position, radius `BoundingSphereRadiusBody * GetAbsoluteLargestElement(root.ScaleTotal)` (mirrors `KittenEva.UpdateHighlight`) | Chord midpoint and camera-facing normal in the root part's frame; `SuggestedMinDepth` = sphere diameter so the box reaches the fur |
| Terrain of `Camera.NearbyCelestial` | Body-fixed (CCF) ray; 64-step march over `range`, then 24 bisections, every sample `GetTerrainHeightFromDirCcf(dir, accurate: true)` | Latitude/longitude (degrees) and a compass heading that makes the PNG read upright from the camera |

Vehicles further than `range + BoundingSphereRadiusBody` skip the part and kitten tests (canopies are
tested before that cull).[^decal-picker][^decal-picker-parachute]

## Lifecycle and timing

`RenderFloatingWindows` runs from unscience's `[StarMapAfterGui]` (a postfix of
`Program.OnDrawUiViewports`), i.e. `frame:draw-ui`, between `ImGui.NewFrame` and `ImGui.Render`, so
`IsMouseClicked`, `WantCaptureMouse` and the foreground draw list are all valid.[^unscience-mod] Since
KSA 5402 the cursor ray is built on demand: `Cursor.GetEgoRay(viewport)` =
`viewport.GetCamera().ScreenToEgoRay(Cursor.GetPosition(viewport))`, where the desktop position was
captured in `PrepareImGui` (`Program.cs` L2138) and the camera was moved in `OnFrameViewports`
(L2263). The pick therefore uses this frame's camera and cursor.[^ksa-cursor][^ksa-program]

Everything it reads is main-thread state that is stable at this point: part matrices from the last
`ApplyVehicleSolvers`, and canopy nodes from `ClothPositionsFront`, which `ChuteClothSystem.ApplyResults`
(called from `Universe.ApplyClothSolvers`,
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1954-L1960)) fills by copying the solver output into the back buffer and
swapping, so the next cloth step running on workers never writes the array being picked.
The canopy renderer reads the same front buffer later in the frame.[^ksa-chuteclothsystem][^ksa-universe]
No physics state is written. While the HUD is hidden StarMap skips `OnDrawUiViewports`, and unscience
does not replay `RenderFloatingWindows`, so placement is unavailable with the HUD off.[^unscience-mod]

## Key code

| Path | Role |
|---|---|
| [graffiti.lib/GraffitiSubmod.Placement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.Placement.cs#L24-L89) | Arm/disarm, click gate, cursor hint |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs#L145-L200) | `PlaceAtCursor`: texture first, pick, build the entry |
| [graffiti.lib/DecalPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalPicker.cs#L58-L189) | Ray, vehicle sweep, `RayCastEgo`, kitten sphere |
| [graffiti.lib/DecalPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalPicker.cs#L200-L284) | Terrain march, bisection, heading |
| [graffiti.lib/DecalPicker.Parachute.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalPicker.Parachute.cs) | Cloth proxy triangles and barycentric hit |

## Snippets

The armed click gate:

```csharp
// graffiti.lib/GraffitiSubmod.Placement.cs L43-L78
        if (!_armed)
            return;

        if (Program.EditorFlag)
        {
            Disarm("Placement cancelled — decals are a flight-scene feature.");
            return;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            Disarm("Placement cancelled.");
            return;
        }

        DrawCursorHint();

        // A click on any ImGui window (including our own panel) must not place a decal.
        if (!ImGui.GetIO().WantCaptureMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var (decal, error) = PlaceAtCursor(_armedDecalName, _range,
                _width, _height, _rollDeg, _alpha, _brightness,
                _depth > 0f ? _depth : null);
            if (decal != null)
            {
                // One-shot: back to normal after a successful placement.
                _armed = false;
                _placeStatus = $"Placed #{decal.Id} on {DescribeTarget(decal)}.";
                _placeStatusIsError = false;
            }
            else
            {
                // A miss keeps placement armed so a slightly-off click isn't a whole round trip.
                _placeStatus = $"{error}  Click again or press Esc.";
                _placeStatusIsError = true;
            }
        }
```

The mesh-precise part test; the hit is in the sub-part's frame:

```csharp
// graffiti.lib/DecalPicker.cs L124-L141
            foreach (var part in vehicle.Parts.Parts)
            {
                if (!part.RayCastEgo(in vehicleMatrix, ray, out var distance, out _,
                        out var positionAsmb, out var normalAsmb, out _, out _,
                        out var closestSubPart, out _))
                    continue;
                if (!(distance >= 0) || !(distance < best))
                    continue;

                var normalLength = normalAsmb.Length();
                if (!double.IsFinite(normalLength) || normalLength <= 0)
                    continue;

                best = distance;
                found = true;
                result = new PickResult(DecalAnchorKind.Vehicle, vehicle, closestSubPart ?? part, null, null,
                    positionAsmb, normalAsmb / normalLength, distance, 0.0);
            }
```

Live cloth triangle test with barycentric capture:

```csharp
// graffiti.lib/DecalPicker.Parachute.cs L54-L85
        void TestTriangle(int ia, int ib, int ic)
        {
            var a = double3.Unpack(in positions[ia]);
            var b = double3.Unpack(in positions[ib]);
            var c = double3.Unpack(in positions[ic]);
            var aEgo = a.Transform(local2Ego);
            var bEgo = b.Transform(local2Ego);
            var cEgo = c.Transform(local2Ego);
            if (!ray.RaycastWatertight(aEgo, bEgo, cEgo, out var distance)
                || !(distance >= 0) || !(distance < best))
                return;

            var hitLocal = (ray.Origin + ray.Direction * distance).Transform(ego2Local);
            if (!TryBarycentric(hitLocal, a, b, c, out var barycentric))
                return;

            var normal = double3.Cross(b - a, c - a);
            var normalLength = normal.Length();
            if (!double.IsFinite(normalLength) || normalLength <= 0)
                return;
            normal /= normalLength;
            var normalEgo = double3.TransformNormal(normal, local2Ego);
            var normalSign = double3.Dot(normalEgo, ray.Direction) <= 0 ? 1.0 : -1.0;

            best = distance;
            found = true;
            bestResult = new PickResult(DecalAnchorKind.Parachute, vehicle, parachute.Parent,
                parachute, null, hitLocal, normal * normalSign, distance, 0.0,
                ParachuteCanopyIndex: parachute.CanopyIndex,
                ClothNodeA: ia, ClothNodeB: ib, ClothNodeC: ic,
                ClothBarycentric: barycentric, ClothNormalSign: normalSign);
        }
```

CPU ray-versus-terrain: coarse march, then bisection, in body-fixed coordinates:

```csharp
// graffiti.lib/DecalPicker.cs L219-L245
        var above = 0.0;
        var below = double.NaN;
        for (var step = 1; step <= TerrainMarchSteps; step++)
        {
            var t = range * step / TerrainMarchSteps;
            if (Depth(body, originCcf + directionCcf * t) <= 0)
            {
                below = t;
                break;
            }

            above = t;
        }

        if (double.IsNaN(below))
            return false;

        for (var i = 0; i < TerrainBisections; i++)
        {
            var middle = 0.5 * (above + below);
            if (Depth(body, originCcf + directionCcf * middle) <= 0)
                below = middle;
            else
                above = middle;
        }

        var hitCcf = originCcf + directionCcf * below;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Cursor.GetEgoRay(IViewport)` / `GetPosition` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L22-L30) | Ego-space cursor ray for the main viewport |
| `Program.PrepareImGui` -> `Cursor.SetDesktopPosition` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2138) | Where this frame's cursor position comes from |
| `Part.RayCastEgo(ref readonly double4x4, Ray, out ... x8)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2573) | Whole-part sphere early-out, then per-sub-part `RayCastEgoSubPart` |
| `Part.PositionEgo`, `MatrixAsmb2Ego`, `ScaleTotal` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1200-L1215) | Kitten sphere centre and part-local conversion |
| `KittenEva.UpdateHighlight(IGameViewport)` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1102-L1127) | The game's own kitten sphere pick that graffiti mirrors |
| `Parachute.ClothPositionsFront`, `AttachLocationPartAsmb`, `CanopyIndex`, `BuildLocal2Ego` | [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1185-L1230) | Live cloth nodes and the canopy's attach-to-ego transform |
| `ChuteClothSystem.Topology`, `ApplyResults` | [current/decomp/KSA/ChuteClothSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteClothSystem.cs#L280-L296) | Shared topology; front/back node buffer swap on the main thread |
| `ChuteClothTopology.Rings` / `Spokes` / `ApexIndex` / `CanopyNodeCount` / `NodeIndex` | [current/decomp/KSA/ChuteClothTopology.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteClothTopology.cs#L8-L28) | Proxy triangulation (`NodeIndex` at L300) |
| `Ray.RaycastWatertight(double3, double3, double3, out double)`, `Ray.Raycast(BoundingSphere3D, out double, out bool)` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L38-L141) | Triangle and sphere tests |
| `Celestial.GetTerrainHeightFromDirCcf(double3, bool accurate)`, `GetCce2Ccf`, `GetLatitudeFromCcf`, `GetLongitudeFromCcf`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L800) | Height field and geodetic conversion; procedural modifiers only when `accurate` (L875) |

## Pitfalls and version notes

- **`accurate: true` is load-bearing.** Only the accurate path samples the heightmap bicubically and
  runs the procedural terrain modifiers the rendered surface includes; the cheap path would park the
  anchor metres off the visible ground and the decal would never draw.[^ksa-celestial]
- **Sub-part frames.** `RayCastEgo` returns position and normal in `closestSubPart`'s frame and only
  iterates `SubParts`; a top-level part with no sub-parts can never be hit. Since 5482 a whole-part
  bounding-sphere test runs first; it scales by the largest `ScaleTotal` element, so heavily
  per-axis-scaled parts deserve a live check.[^ksa-part][^scope-decals]
- **Canopies are not part meshes.** The visible canopy is a bone-skinned GLB driven by the cloth
  nodes, not the 240-triangle proxy, so the hit can be slightly off the drawn surface; the projection
  depth absorbs it.[^ksa-parachute][^ksa-chuteclothtopology]
- **Kittens** are rendered through the character avatar, not part view meshes, so only the sphere test
  works; the decal then rides the root part, not the animated limbs. Unlike the game's own kitten
  highlight, the mirror ignores the `CursorTarget.IsHitTestViewport` and 2 km gates.[^ksa-kitteneva]
- **Cursor convention.** `GetEgoRay` subtracts `viewport.Position`; 5402 removed the old cached
  `Cursor.InputRay` (a compile break) and made the ray same-frame.[^ksa-cursor][^scope-decals]
- Starting the terrain march underground returns no hit, and anything beyond `range` (default 2 km) is
  a miss.[^decal-picker]

## See also

- [Projected decal pass](./graffiti-projected-decal-pass.md)
- [Per-frame decal anchoring](./graffiti-decal-anchoring.md)
- [Hot Pursuit mesh-ray placement](./hot-pursuit-mesh-ray-placement.md) (also uses `Part.RayCastEgo`)
- [Graffiti sub-mod](../submods/graffiti.md) and the [mod overview](../overview.md)
- [Layered cast over cloth, part meshes, kitten sphere and a terrain march](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-5.md) - how-to article demonstrating this feature

[^graffiti-placement]: graffiti.lib/GraffitiSubmod.Placement.cs
[^graffiti-submod]: graffiti.lib/GraffitiSubmod.cs
[^decal-picker]: graffiti.lib/DecalPicker.cs
[^decal-picker-parachute]: graffiti.lib/DecalPicker.Parachute.cs
[^unscience-mod]: unscience/Mod.cs
[^scope-decals]: scope/decals.md (authors' graffiti integration analysis)
[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: KSA.Parachute (decompiled, build 2026.10.10.5554)
[^ksa-chuteclothsystem]: KSA.ChuteClothSystem (decompiled, build 2026.10.10.5554)
[^ksa-chuteclothtopology]: KSA.ChuteClothTopology (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
