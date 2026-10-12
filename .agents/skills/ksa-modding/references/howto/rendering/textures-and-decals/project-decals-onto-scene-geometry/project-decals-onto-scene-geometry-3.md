---
type: How-To
title: Procedural bullet holes painted by compute from depth, stored in the struck subpart's frame
description: Paint procedural damage marks with a compute pass from the depth buffer along the shell's path, anchored in the struck sub-part's frame and placed with the engine's own draw matrix
category: rendering
group: textures-and-decals
topic: project-decals-onto-scene-geometry
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/bullet-hole-decals]
techniques: [vulkan-pipeline, harmony-prefix, glsl-shader]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection Core.KSADeviceContextEx.Allocator
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types: [KSA.Part, KSA.SunbloomRenderer, KSA.Vehicle]
tags: [decal, damage, compute, procedural, subpart, depth]
---

Outcome: every gun shell that touches a hull leaves a ragged hole that stays on the mesh however the craft flies, turns, articulates or breaks up. It needs no texture and no decal mesh: the hole is stored in the struck sub-part's own frame and brought to the camera, for every recorded view, through the very matrix the engine draws that sub-part with, then painted by a compute pass from the depth buffer along the
path the shell came in on, so it lands on whatever surface the dented skin now has. Demonstrated by KSArmory ([Shell holes held in the struck sub-part's frame and painted from depth](../../../../mods/ksarmory/features/bullet-hole-decals.md)). Prerequisites: StarMap, Harmony; the compute hook and bindings are in [modify the HDR scene with a compute pass](../../vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md) approach 1.

## Background: how the game does it

`Part.RayCastEgo(ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, out double minDistance, ..., out double3 nearIntersectionPositionAsmb, ..., out Part? closestSubPart, out Part? farthestSubPart)` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534) returns the near hit in the closest sub-part's frame. `Vehicle.GetMatrixAsmb2Ego(camera)` and `Part.MatrixAsmb2Ego(in asmb2Ego)` (which walks the parent chain,
including articulation and scale) are the matrices the engine draws parts with. `SunbloomRenderer.Render` is the compute hook ([current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326)).

## Lifecycle and timing

| Moment | Where | What happens |
|---|---|---|
| `frame:draw-ui` / `frame:celestials` | round stepping inside the mod's step (the AfterGui postfix, or the `OnFrameCelestials` prefix with the HUD hidden) | `BulletHoles.Strike` on contact; vehicle state is the applied step's, read on the main thread |
| `frame:draw-ui` | `OnAfterGui` | `Prune()` every 60th call drops holes whose part left the live craft |
| `render:bloom` | prefix on `SunbloomRenderer.Render` (main view, and camera windows through `RenderViewport`) | place, upload, dispatch |

The hole is placed at record time with `Program.GetRenderCamera()` (the viewport being recorded), so it sits exactly where that view draws the part and there is no stored epoch to get wrong. It is painted after the rings and before leak streams, tracers and clouds, so smoke in front of a hull covers its holes. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Strike: cast a fresh ray at the moment of contact** from 2 m back along the relative path (so a first-sub-step hit still starts outside), in the frame of the craft placed relative to the round, over every part; keep the nearest hit and its sub-part and invert that sub-part's matrix for the origin:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L2751-L2774
   double3 along = travel / length;
   double4x4 asmb2Round = v.GetMatrixAsmb2Ego(separation);
   Ray ray = new() { Origin = along * -castFromMetres, Direction = along };

   double nearest = double.MaxValue;
   foreach (Part part in v.Parts.Parts)
   {
       if (!part.RayCastEgo(in asmb2Round, ray, out double near, out _, out double3 at, out _,
                            out _, out _, out Part? struck, out _)
           || struck is null || near < 0.0 || near >= nearest)
       {
           continue;
       }

       nearest = near;
       subPart = struck;
       local = at;
   }

   if (subPart is null) return false;

   double4x4.Invert(subPart.MatrixAsmb2Ego(in asmb2Round), out double4x4 toLocal);
   cameFrom = ray.Origin.Transform(toLocal);
   return Vec.IsFinite(local) && Vec.IsFinite(cameFrom);
   ```

2. **Store** `Hole(Part SubPart, Local, CameFrom, Core, Scorch, Seed)`: hold the `Part`, not an id, so a breakup that moves the part to another craft takes its holes with it. Size from calibre and charge (`core 1.5 x calibre`, soot `max(3 x core, 0.95 m x cbrt(kg))`). At 400 holes the oldest goes.

3. **Place per recorded view:** group by craft, reject a craft behind the camera, off screen or whose widest soot would be under a pixel, then once per struck sub-part take `subPart.MatrixAsmb2Ego(in asmb2Ego)` and per hole compute centre and `inward = unit(local - cameFrom)` in ego plus a side vector fixed to the part so the petals turn with the hull.

   ```csharp
   // src/KSArmory/Ksa/BulletHoles.cs L115-L145
   if (!_craftMatrices.TryGetValue(craft, out double4x4? vehicle))
   {
       vehicle = KsaWorld.TryVehicleMatrixEgo(craft, camera, out double4x4 m, out double3 centre, out double radius)
                 && inView(centre, radius, _widestScorch[craft])
                     ? m
                     : null;
       _craftMatrices[craft] = vehicle;
   }

   if (vehicle is not { } asmb2Ego) continue;

   if (!_partMatrices.TryGetValue(hole.SubPart, out double4x4? part))
   {
       part = KsaWorld.TryPartMatrixEgo(hole.SubPart, in asmb2Ego, out double4x4 m) ? m : null;
       _partMatrices[hole.SubPart] = part;
   }

   if (part is not { } toEgo) continue;

   double3 centreEgo = hole.Local.Transform(toEgo);
   double3 inward = Vec.Unit(centreEgo - hole.CameFrom.Transform(toEgo));
   if (!Vec.IsFinite(centreEgo) || !Vec.IsFinite(inward) || Vec.Len2(inward) < 0.5) continue;

   // The part's own x, or its y where x lies along the path.
   double3 path = hole.Local - hole.CameFrom;
   double3 reference = Math.Abs(Vec.Unit(path).X) < 0.9 ? new double3(1, 0, 0) : new double3(0, 1, 0);
   double3 offset = (hole.Local + reference).Transform(toEgo) - centreEgo;
   double3 side = Vec.Unit(offset - (inward * Vec.Dot(offset, inward)));
   if (!Vec.IsFinite(side) || Vec.Len2(side) < 0.5) continue;

   into.Add(new Placed(Vec.Len(centreEgo), centreEgo, inward, side, hole.Core, hole.Scorch, hole.Seed));
   ```

4. **Upload** to a persistent host-visible storage buffer (80 bytes per hole), sliced `(frameIndex mod framesInFlight) x 4 + view`, sorted nearest first with a screen rectangle each; overlapping soot shares darkness so n holes stack to 70 percent rather than solid black. One dispatch over the union rectangle.

5. **Paint** in a `local_size 16x16` compute shader: threads gather the holes whose rectangle touches the tile into a shared bitmask (so holes are walked in listed order every frame and overlapping petals do not flicker), reconstruct the surface point from reverse-Z depth, and measure along the inward path and across it:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryHole.comp L148-L196
   ivec2 size = imageSize(sceneColor);
   ivec2 tileMin = pc.FirstCountOrigin.zw + ivec2(gl_WorkGroupID.xy) * 16;
   vec2 lo = vec2(tileMin);
   vec2 hi = lo + vec2(16.0);

   if (gl_LocalInvocationIndex < uint(MaskWords)) tileMask[gl_LocalInvocationIndex] = 0u;
   barrier();

   int first = pc.FirstCountOrigin.x;
   int count = min(pc.FirstCountOrigin.y, MaskWords * 32);
   for (int i = int(gl_LocalInvocationIndex); i < count; i += 256)
   {
       vec4 rect = holes[first + i].Rect;
       if (rect.x > hi.x || rect.y > hi.y || rect.z < lo.x || rect.w < lo.y) continue;

       atomicOr(tileMask[i >> 5], 1u << uint(i & 31));
   }
   barrier();

   ivec2 at = tileMin + ivec2(gl_LocalInvocationID.xy);
   if (at.x >= size.x || at.y >= size.y) return;

   uint reached = 0u;
   for (int w = 0; w < MaskWords; w++) reached |= tileMask[w];
   if (reached == 0u) return;

   // Reverse-Z: zero is the sky.
   float deviceZ = texelFetch(sceneDepth, at, 0).r;
   if (deviceZ <= 0.0) return;

   vec2 uv = (vec2(at) + 0.5) / vec2(size);
   vec3 p = WorldAt(uv, deviceZ);

   // What one pixel spans here, so the edges are a pixel soft at any range.
   float footprint = max(length(WorldAt(uv + vec2(1.0 / float(size.x), 0.0), deviceZ) - p), 1.0e-5);

   vec4 was = imageLoad(sceneColor, at);
   vec3 c = was.rgb;

   for (int w = 0; w < MaskWords; w++)
   {
       uint bits = tileMask[w];
       while (bits != 0u)
       {
           int b = findLSB(bits);
           bits &= bits - 1u;
           c = Mark(holes[first + (w * 32) + b], p, footprint, c);
       }
   }
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BulletHoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BulletHoles.cs) | Strike, store, place |
| [src/KSArmory/Shaders/KSArmoryHole.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryHole.comp) | The painting shader |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `Holes` dispatch within the shared pass |

## Variations and gotchas

- **Measure across the path, not around the point:** the same burst dents the skin through KSA's own impact path, and a sphere around the undented contact point never reaches the drawn surface; the mark is a cylinder along `inward`.
- **Hold `Part`, not an id,** and prune once a second.
- **Everything is painted nearest first,** at most 400 per view; CPU cost is logged at debug level.
- **Planned versus built:** the design doc planned image decals as a projected box per decal from a new `ResolveAttachments` postfix; what was built reuses the ring pass's route (no new patch or pipeline state). Image decals are still unbuilt.

## See also

- [Overview](overview.md)
- [Approach 1: unit-cube decal volumes](project-decals-onto-scene-geometry-1.md)
- [Shell holes held in the struck sub-part's frame and painted from depth](../../../../mods/ksarmory/features/bullet-hole-decals.md)
- [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
