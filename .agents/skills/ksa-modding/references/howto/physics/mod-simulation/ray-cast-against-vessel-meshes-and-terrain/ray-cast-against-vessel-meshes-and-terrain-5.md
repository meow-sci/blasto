---
type: How-To
title: Layered cast over cloth, part meshes, kitten sphere and a terrain march
description: Anchor a decal to whatever the cursor ray meets first, testing deployed parachute cloth, part meshes, kitten bounding spheres and then a CPU march of the terrain, each stored in a frame that survives the next frame
category: physics
group: mod-simulation
topic: ray-cast-against-vessel-meshes-and-terrain
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/graffiti-decal-placement-picking]
techniques: [ray-cast, imgui-input]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Part, KSA.Vehicle, KSA.Cursor, KSA.Camera, KSA.Celestial, KSA.KittenEva, KSA.Ray]
tags: [physics, ray-cast, picking, decal, cloth, terrain, barycentric, armed-placement]
---

Outcome: an "arm, then click the world" placement tool whose click anchors a decal to the nearest of four kinds of
target, with a stored form that survives vehicle motion, canopy deformation and save/load. unscience's Graffiti
submod does this. Prerequisites: StarMap. Part meshes use the same call as
[approach 2](ray-cast-against-vessel-meshes-and-terrain-2.md); the terrain march is a bisection variant of
[approach 4](ray-cast-against-vessel-meshes-and-terrain-4.md).

## Background: how the game does it

All four tests start from one ray, `Cursor.GetEgoRay(Program.MainViewport)`, built on demand from this frame's camera
and cursor; the pick therefore uses this frame's state.[^ksa-cursor]

| Target | Test | Anchor stored |
|---|---|---|
| Deployed canopy (every `Parachute` module of every vehicle) | Rebuild the canopy proxy triangles from `Parachute.ClothPositionsFront` and test with `Ray.RaycastWatertight` | Cloth nodes and a barycentric position, plus a normal sign |
| Ordinary part | `Part.RayCastEgo` per top-level part | Hit and first-vertex normal in the closest subpart's local frame, keyed by its `InstanceId` |
| `KittenEva` | `Ray.Raycast(BoundingSphere3D)` at the root part's ego position, radius `BoundingSphereRadiusBody` times the largest `ScaleTotal` element (mirrors `KittenEva.UpdateHighlight`) | Chord midpoint and camera-facing normal in the root part's frame |
| Terrain of `Camera.NearbyCelestial` | Body-fixed ray: 64-step march over the range, then 24 bisections, every sample `GetTerrainHeightFromDirCcf(dir, accurate: true)` | Latitude and longitude and a compass heading |

`ClothPositionsFront` is a front buffer filled by `ChuteClothSystem.ApplyResults` (from `Universe.ApplyClothSolvers`),
which swaps buffers, so the cloth step running on workers never writes the array being picked.

## Lifecycle and timing

The placement UI runs from `StarMapAfterGui` (`frame:draw-ui`), between `ImGui.NewFrame` and `ImGui.Render`, so
`IsMouseClicked`, `WantCaptureMouse` and the foreground draw list are valid. Everything read is main-thread state
that is stable there: part matrices from the last `ApplyVehicleSolvers`, canopy nodes from the front buffer. No
physics state is written. While the HUD is hidden StarMap skips `OnDrawUiViewports`, so placement is unavailable with
the HUD off. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Arm and gate the click. Disarm in the editor or on Escape, draw a hint at the mouse, and place only when
   `!WantCaptureMouse && IsMouseClicked(Left)`. A success disarms (one-shot); a miss keeps placement armed:

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

2. Sweep the targets in order, keeping a running `best` distance so the nearest wins. Skip vehicles further than
   `range + BoundingSphereRadiusBody` for the part and kitten tests (canopies are tested before that cull).
3. Part meshes: the same loop as approach 2; the hit is stored in the closest subpart's frame:

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

4. Cloth: test the canopy proxy triangles in ego space and capture the hit as barycentric coordinates in the cloth's
   own frame so the decal follows the deforming canopy:

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

5. Terrain: a coarse march in the body-fixed frame until the ray is below the height field, then bisection:

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

6. Choose the stored form per kind: part-local (keyed by `InstanceId`), cloth-barycentric, or geodetic. A decal needs
   a projection depth derived from the target (`max(auto depth, pick.SuggestedMinDepth)`).

## Complete example

| File | Role |
|---|---|
| [graffiti.lib/GraffitiSubmod.Placement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.Placement.cs#L24-L89) | Arm/disarm, click gate, cursor hint |
| [graffiti.lib/DecalPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalPicker.cs#L58-L189) | Ray, vehicle sweep, `RayCastEgo`, kitten sphere |
| [graffiti.lib/DecalPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalPicker.cs#L200-L284) | Terrain march, bisection, heading |
| [graffiti.lib/DecalPicker.Parachute.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalPicker.Parachute.cs) | Cloth proxy triangles and barycentric hit |

## Variations and gotchas

- The sweep is order-agnostic because the nearest distance wins; the "layering" is the single running `best`.
- Cloth normals are two-sided; the sign is recorded so a decal faces the camera that placed it.
- A kitten has no castable mesh: the sphere radius stands in, and the suggested depth equals the sphere diameter so
  the projection box reaches the fur.
- The terrain march uses `accurate: true` so the hit matches the colliders.
- Placement is gone with the HUD hidden because the hook does not run.
- `InstanceId` is a runtime counter; the persisted anchor uses a structural reference.

## See also

- [Overview and comparison](overview.md)
- [Cursor ray against every vehicle](ray-cast-against-vessel-meshes-and-terrain-2.md), [walk the ray over the height field](ray-cast-against-vessel-meshes-and-terrain-4.md)
- Mod feature: [unscience graffiti decal placement picking](../../../../mods/unscience/features/graffiti-decal-placement-picking.md)
- The topic choose-a-render-frame-and-anchor-effects (other batch) covers anchoring effects

[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
