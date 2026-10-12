---
type: How-To
title: "Paint holes that stay on a moving hull: sub-part frames and a tile bitmask gather"
description: Store a mark in the struck sub-part's own frame, bring it to each camera through the matrix the game draws that part with, and paint up to 400 procedural holes from the depth buffer in one dispatch
category: shaders
group: compute-effects
topic: reconstruct-scene-positions-from-depth-in-a-compute-shader
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/bullet-hole-decals]
techniques: [glsl-shader, vulkan-buffer, harmony-prefix]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Part
  - KSA.Vehicle
  - KSA.PartTree
  - KSA.Camera
  - Brutal.VulkanApi.Abstractions.BufferEx
  - KSA.Rendering.ComputePipelineWrapper
tags: [depth, decal, procedural, bullet-hole, sub-part, tile-gather, bitmask, compute, ksarmory]
---
Every gun shell that hits a hull in KSArmory leaves a ragged black hole with torn petals and soot, painted into the scene image by a compute shader. The hole stays on the mesh as the craft flies, turns, articulates or breaks up, because it is stored in the struck sub-part's own frame and brought to the camera through the very matrix the game draws that sub-part with. There is no texture and no decal mesh: the shader reads the depth buffer, reconstructs each pixel's point and measures it against the hole. This article is [Shell holes held in the struck sub-part's frame and painted from depth](../../../../mods/ksarmory/features/bullet-hole-decals.md) as a recipe; the buffer allocation is in [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md) and the pass set-up in [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Background: how the game does it

- **Parts form a tree and know how they are drawn.** `Part.MatrixAsmb2Ego(in double4x4 vehicleAsmb2Ego)` composes a sub-part's scale, rotation and offset with its parent chain, and `Vehicle.GetMatrixAsmb2Ego(Camera)` gives the vehicle's own placement relative to a camera ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1213-L1222), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1282)). Applying them in that order gives the same Ego-space transform the renderer uses for that part this frame.
- **A watertight hull cast.** `Part.RayCastEgo` casts against the part meshes and returns the near hit in the closest sub-part's own frame (`Part.cs` L2534-L2609).
- **Ownership follows breakups.** `PartTree.OwningVehicle` says which craft a part is on now, so a hole that holds the `Part` follows it onto another craft after a breakup.
- **Depth and hook.** The same colour-storage and depth-sampled state before `SunbloomRenderer.Render` that the ring pass uses ([Reconstruct scene positions from the depth buffer and paint on them](overview.md) approach 1 shows the barrier code).

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:draw-ui` or `frame:celestials` (the mod's simulation step, from the AfterGui postfix or the `OnFrameCelestials` prefix) | A round touches a hull: `BulletHoles.Strike` casts a fresh ray and stores the hole |
| `frame:draw-ui` (`OnAfterGui`) | `Enabled` follows the setting; `Prune()` every 60th call drops holes whose part left a live craft |
| `render:bloom` (prefix on `SunbloomRenderer.Render`, per viewport) | `CloudPass.Holes` places every hole for that camera, uploads them and dispatches |

Placement happens at record time, per recorded camera, with the matrices that camera draws with, so there is no stored epoch to get wrong and camera windows get their own holes. The strike happens on the main thread in the simulation step; the cast and mesh read do not run on a physics worker. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Cast the shell's path at the moment of contact, in the craft's frame.** Put the craft where it is at the instant of the burst, take the round's velocity relative to it, start the ray two metres back along that path, and use the round-centred matrix `GetMatrixAsmb2Ego(separation)` so the cast runs in metres around the round, not in the ecliptic. The nearest hit's sub-part and point are kept, and the ray origin is pulled back through the sub-part's inverse matrix so both points are in its frame:

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

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2751-L2774). Keep the path direction, not the surface normal: the mark is a cylinder along it, which is why it still lands on a hull the burst has dented.

2. **Store a small record that holds the `Part`, not an id.** At 400 holes the oldest goes:

   ```csharp
   // src/KSArmory/Ksa/BulletHoles.cs L23-L26
       internal readonly record struct Hole(Part SubPart, double3 Local, double3 CameFrom, float Core, float Scorch, float Seed);

       /// <summary>The most holes kept in the world; the oldest go first.</summary>
       public const int MaxHoles = 400;
   ```

   Source: [src/KSArmory/Ksa/BulletHoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BulletHoles.cs#L23-L26). Holding the reference means a breakup that moves the part takes the hole with it; `Prune` drops holes whose part is no longer on a live craft.

3. **Place each hole for the camera being recorded.** Per craft, compute the vehicle's matrix for this camera once and skip crafts that cannot reach a pixel; per struck sub-part compute its matrix once; per hole transform the centre and the inward direction, and fix a side vector to the part so the petals turn with the hull:

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

   Source: [src/KSArmory/Ksa/BulletHoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BulletHoles.cs#L115-L145). The resulting centre is already camera-relative (Ego), so it can go straight into a `float4`.

4. **Upload to a storage buffer, one run per view per frame.** Each hole is five `vec4`s (centre and soot radius; inward and reach; side and soot share; core and seed; screen rectangle) written to a slice of a host-visible buffer exactly as in [the storage-buffer approach](../pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-3.md). Sort nearest first, grow each hole's rectangle with `HoleRect`, drop what is under a pixel or off-screen, and let overlapping soot share its darkness so a dozen hits stack to 70 percent, not solid black. The struct on both sides:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1074-L1081
       public struct HoleData
       {
           public float4 CentreScorch;    // the hole, camera-relative, and the soot's radius
           public float4 InwardReach;     // the way the shell went in, and how far along it the mark reaches
           public float4 SideShare;       // a direction square to that, fixed to the part, and the soot's share
           public float4 CoreSeed;        // the hole's radius and its pattern's seed
           public float4 Rect;            // the pixels it can reach: first x, first y, last x, last y
       }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1074-L1081).

5. **Dispatch once over the union rectangle.** The push block is `InvViewProj` plus `(first, count, originX, originY)` (80 bytes); one dispatch of 16x16 groups covers the union of every hole's rectangle:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1177-L1198
           if (count > 0 && maxX > minX && maxY > minY)
           {
               HolePush push = new()
               {
                   InvViewProj = camera.VPInv.viewProjection,
                   First = first,
                   Count = count,
                   OriginX = minX,
                   OriginY = minY,
               };

               using (commandBuffer.TagRegion(GpuTag))
               using (commandBuffer.TagRegion(HolesTag))
               {
                   if (marks > 0) Hazard(commandBuffer);

                   view.Holes!.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
                   commandBuffer.Dispatch((maxX - minX + HoleGroup - 1) / HoleGroup,
                                          (maxY - minY + HoleGroup - 1) / HoleGroup, 1);
                   marks++;
               }
           }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1177-L1198).

6. **Gather per tile with a bitmask, not a list.** Each 16x16 workgroup's 256 invocations stride over the holes and set one bit per hole whose rectangle touches the tile, in a `shared` array of 13 words (416 bits, enough for 400). A mask, not an atomic list, means holes are walked in listed order every frame, so overlapping petals do not flicker:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryHole.comp L148-L165
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
   ```

   Source: [src/KSArmory/Shaders/KSArmoryHole.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryHole.comp#L148-L165). The comment in the shader gives the reason (atomics hand list slots out in whatever order threads arrive, which flickers where marks overlap).

7. **Reconstruct the point and walk the set bits.** Skip sky (reverse-Z depth 0), rebuild the point with `InvViewProj`, take the pixel's footprint from the neighbouring pixel, and fold every reaching hole into the colour in order:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryHole.comp L167-L198
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

       imageStore(sceneColor, at, vec4(c, was.a));
   ```

   Source: [src/KSArmory/Shaders/KSArmoryHole.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryHole.comp#L167-L198).

8. **Measure across the path, not around the point.** In `Mark`, a surface point belongs to the hole if its distance along the inward direction is under the reach and no more than a few centimetres out in front, and its distance across that line is under the soot radius. A droplet of the tank's own leak passing in front writes depth too and is excluded by the front limit:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryHole.comp L80-L89
   vec3 Mark(Hole h, vec3 p, float footprint, vec3 was)
   {
       float scorch = h.CentreScorch.w;
       vec3 d = p - h.CentreScorch.xyz;
       float along = dot(d, h.InwardReach.xyz);
       if (!(along < h.InwardReach.w) || !(along > -max(OutFront * scorch, LeastOutFront))) return was;

       vec3 across = d - h.InwardReach.xyz * along;
       float r = length(across);
       if (!(r < scorch)) return was;
   ```

   Source: [src/KSArmory/Shaders/KSArmoryHole.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryHole.comp#L80-L89). A sphere around the undented contact point never reaches the dented skin (about 0.57 m deep for a 5-inch shell), which is why the mark is a cylinder along the path. The rest of `Mark` is a seeded procedural look: a ragged outline, five to eight torn petals, a glint, pits and streaked soot.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BulletHoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BulletHoles.cs) | Strike, store (newest 400), per-view placement, prune |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | `TryHullHit`; `TryVehicleMatrixEgo`, `TryPartMatrixEgo` |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `HolePush`, `HoleData`, `Holes`, `HoleRect`, `ShareSoot`, buffer and pipeline build |
| [src/KSArmory/Shaders/KSArmoryHole.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryHole.comp) | Bitmask gather and the procedural mark |

## Variations and gotchas

- **Hold the `Part`.** An id would lose the hole in a breakup; the reference follows the part.
- **Painted nearest first, all of them.** `MostHolesPainted` equals `MaxHoles` (400); dropping the far ones changes which end of a riddled craft is marked as the view moves. CPU cost is logged at debug level every 300 recorded views.
- **Before leak streams, tracers and clouds.** Smoke in front of a hull covers its holes. Painted even when the cloud pass is switched off.
- **Plan versus build.** The author's design document planned image decals rasterised as projected boxes from a new patch; what was built uses the ring pass's route (no new patch, procedural looks, every viewport). Image decals on hulls remain unbuilt; see [Project decals onto vessels, terrain and clutter](../../../rendering/textures-and-decals/project-decals-onto-scene-geometry/overview.md) for a texture-based route.
- **A tank is convex.** The front limit (5 percent of the soot radius, at least 3 cm) is tuned for convex skins; a concave surface in front of the hole would be treated as outside.
- **Cost on the CPU.** Placement walks the matrix chain per struck sub-part and per hole per view; holes crowd onto few parts, so the matrices are cached per frame.

## See also

- Siblings: [rings from depth](reconstruct-scene-positions-from-depth-in-a-compute-shader-1.md), [depth-tested streaks](reconstruct-scene-positions-from-depth-in-a-compute-shader-3.md), [overview](overview.md)
- [Shell holes held in the struck sub-part's frame and painted from depth](../../../../mods/ksarmory/features/bullet-hole-decals.md), [Per-triangle hull hit test with the engine's part ray cast](../../../../mods/ksarmory/features/hull-triangle-hit-test.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md), [Project decals onto vessels, terrain and clutter](../../../rendering/textures-and-decals/project-decals-onto-scene-geometry/overview.md)
