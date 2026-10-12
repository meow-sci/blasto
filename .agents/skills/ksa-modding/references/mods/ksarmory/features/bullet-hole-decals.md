---
type: Mod Feature
title: Shell holes held in the struck sub-part's frame and painted from depth
description: Shell hits found with Part.RayCastEgo are stored in the struck sub-part's own frame and carried to the camera through the matrix the engine draws that sub-part with, and the newest 400 are painted as procedural holes and soot by one tile-gathered compute dispatch before bloom
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - vulkan-buffer
  - reflection
  - post-processing
  - depth-buffer-projection
  - part-frame-anchor
  - tile-bitmask-gather
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection Core.KSADeviceContextEx.Allocator
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.Part
  - KSA.Vehicle
  - KSA.PartTree
  - KSA.Camera
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - Core.KSADeviceContextEx
  - Core.Renderer
  - KSA.KsaVmaAllocator
  - Brutal.VulkanApi.Abstractions.IBufferAllocator
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - KSA.Program
tags:
  - decals
  - damage
  - bullet-holes
  - raycast
  - sub-part-frame
  - compute-shader
  - storage-buffer
  - procedural-texture
---

Every gun shell that actually touches a hull leaves a hole there: a ragged black centre, five to
eight torn petals, pits and patchy soot out to a radius that grows with the charge's cube root. The
hole stays on the mesh however the craft flies, turns, articulates or breaks up, because it is
stored in the struck sub-part's own frame and brought to the camera, every recorded view, through
the very matrix the engine draws that sub-part with. It is procedural (no texture, no decal mesh)
and painted by the mod's compute pass from the depth buffer, along the path the shell came in on,
so it lands on whatever surface the dented skin now has.[^ksarmory-bulletholes][^ksarmory-hole-comp]

## How it works

**1. Strike: a fresh ray cast at the moment of contact** (`BulletHoles.Strike`, from the kill path
in `WeaponSystem` when `round.StruckBody is Vehicle`):[^ksarmory-weaponsystem]

| Step | Detail |
|---|---|
| Instant | Craft carried to the burst: `PositionEcl(craft) + VelocityEcl(craft) x DetonationElapsedInFrame`; `separation = craftAt - round.PositionEcl` |
| Path | `travel = round.VelocityEcl - VelocityEcl(craft)` (relative), ray from 2 m back along it so a first-sub-step hit still starts outside |
| Frame | `v.GetMatrixAsmb2Ego(separation)`: the craft placed relative to the round, so the cast runs in metres around the round, not in the ecliptic |
| Cast | `Part.RayCastEgo` over every part; nearest `minDistance` wins; its `closestSubPart` and `nearIntersectionPositionAsmb` are kept |
| Anchor | `local` is already in the sub-part's frame (`RayCastEgoSubPart` inverts that sub-part's `MatrixAsmb2Ego`); `cameFrom` = the ray origin through the same inverse |
| Size | `HoleLook.RadiiFor(calibre, charge)`: core `1.5 x calibre`, soot `max(3 x core, 0.95 m x cbrt(kg))` (0.35 m for 50 g, 1.4 m for 3.3 kg) |
| Keep | `Hole(Part SubPart, Local, CameFrom, Core, Scorch, Seed = n mod 97)`; at `MaxHoles = 400` the oldest goes |

[^ksarmory-ksaworld][^ksa-part][^ksarmory-holelook]

**2. Place, per recorded view** (`BulletHoles.Place`, from `CloudPass.Holes`):

1. Group by craft (`part.FullPart.Tree.OwningVehicle`), widest soot per craft.
2. Once per craft: `v.GetMatrixAsmb2Ego(camera)` (the engine's draw placement for this camera),
   centre of mass and mean radius, then `CraftInView`: one projection rejects a craft behind the
   camera, off-screen, or whose widest soot would be under a pixel at its nearest point.[^ksa-vehicle]
3. Once per struck sub-part: `subPart.MatrixAsmb2Ego(in asmb2Ego)` (walks the parent chain,
   including articulation and scale).
4. Per hole: centre and `inward = unit(local - cameFrom)` in Ego; a side vector fixed to the part
   (its +X, or +Y if X lies along the path) so the petals turn with the hull, not the camera.

**3. Upload** (`CloudPass.Holes`):[^ksarmory-cloudpass]

- Sorted nearest first; soot is capped at 6 hole radii and the reach along the path is
  `max(soot, 1 m)`. Each gets a screen rectangle from `HoleRect`: the projected centre plus a sphere
  of radius `sqrt(soot^2 + reach^2)` across the line of sight, x1.5 + 2 px slack for a curved hull,
  dropped if under a pixel or off-screen.
- `ShareSoot`: holes whose soot overlaps (neighbours found after sorting by X) each get
  `1 - (1 - 0.7)^(1/n)`, so n overlapping holes stack to 70% dark rather than to solid black.
- Written into a persistent host-visible, host-coherent storage buffer
  (`HoleData`: 5 x `vec4` = 80 bytes) at slice `(frameIndex mod framesInFlight) x 4 + view` of
  400 entries. The allocator is `Renderer.Allocator` read by reflection, as for the tracers.[^ksa-ksadevicecontextex]
- One dispatch over the union rectangle of all holes, `ceil(w/16) x ceil(h/16)`; push is
  `InvViewProj` + `(first, count, originX, originY)` (80 bytes).

**4. Paint** (`KSArmoryHole.comp`, `local_size 16x16`; set 1: 0 = scene colour storage, 1 = depth,
2 = `std430` hole buffer):[^ksa-computepipelinewrapper]

| Stage | Detail |
|---|---|
| Tile gather | Threads stride over the holes and `atomicOr` a bit per hole whose rect touches the tile into a 13-word `shared` mask (416 bits, enough for 400). A mask, not an atomic list, so holes are walked in listed order every frame and overlapping petals do not flicker. |
| Surface point | Reverse-Z depth (`0` = sky skipped), `InvViewProj` reconstruction, pixel footprint from the neighbour pixel |
| Along the path | `along = dot(p - centre, inward)` must be under the reach (soot or 1 m, whichever is more) and no more than 5% of the soot radius (min 3 cm) out in front, so a leak droplet passing in front does not wear the hole |
| Across | `r = |across|` against the soot radius; angle around `inward` from the part-fixed side vector |
| Look | Noisy two-scale outline (never under 1.5 px), petals with seeded length and fold shading, a metal glint on torn edges, fragment pits, streaked soot scaled by the shared share; hole goes to black |

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `frame:draw-ui` / `frame:celestials` | Round stepping inside `StepSimulation`, from `StepOnce` (AfterGui postfix at Program.cs L2268, or the `OnFrameCelestials` prefix at L2320 with the HUD hidden) | `BulletHoles.Strike` on contact; vehicle state is the applied step's, read on the main thread[^ksarmory-mod] |
| `frame:draw-ui` | `OnAfterGui` (KSArmoryMod.cs L538-L540) | `BulletHoles.Enabled = Config.BulletHoles`; `Prune()` every 60th call drops holes whose part left the live craft |
| `render:bloom` | Prefix on `SunbloomRenderer.Render`: main view Program.cs L4915 (before `_overallBloomRenderer.Render` L4916), camera windows `RenderViewport` L4584 | `CloudPass.Holes`: place, upload, dispatch[^ksa-program] |

- **Placed at record time.** `GetMatrixAsmb2Ego(camera)` is computed inside the render for the
  camera of the viewport being recorded (`Program.GetRenderCamera()`, Program.cs L641), so a hole
  sits exactly where that view draws the part and there is no stored epoch to get wrong. Camera
  windows get their own holes.
- **Order in the pass:** after the rings, before leak streams, tracers and any cloud, so smoke in
  front of a hull covers its holes. Painted with the shader pass at zero too.
- **The strike is on the main thread.** The cast and the mesh read (`MeshReference.PositionCompare`)
  happen in the mod's step, not on a physics worker.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/BulletHoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BulletHoles.cs) | Strike, store (newest 400), per-view placement, prune |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2731-L2780) | `TryHullHit`: the ray cast into the sub-part frame |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2969-L3037) | `TryCraftOf`, `TryVehicleMatrixEgo`, `TryPartMatrixEgo`, `IsOnLiveCraft` |
| [src/KSArmory/Sim/HoleLook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/HoleLook.cs) | Hole and soot radii from calibre and charge |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1062-L1348) | `HolePush`, `HoleData`, rects, soot sharing, buffer, dispatch |
| [src/KSArmory/Shaders/KSArmoryHole.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryHole.comp) | Bitmask tile gather and the procedural mark |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3869-L3873) | Where a struck hull gets its hole |

## Snippets

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
    // ...
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

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Part.RayCastEgo`, `RayCastEgoSubPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2609) | Watertight per-triangle cast; near hit returned in the closest sub-part's own frame (L2603-L2604) |
| `Part.MatrixAsmb2Ego(in double4x4)`, `FullPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1171-L1222) | Sub-part to Ego through its parent chain (L1213); owning part (L1171) |
| `Vehicle.GetMatrixAsmb2Ego(Camera)`, `GetMatrixAsmb2Ego(double3)`, `CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1282) | Draw placement for a camera; the round-centred frame for the cast |
| `PartTree.OwningVehicle` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L46) | Which craft a struck sub-part is on now (follows a breakup)[^ksa-parttree] |
| `Camera.EgoToClipDouble`, `VPInv` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L277) | Craft and hole screen rects; inverse VP pushed (L65)[^ksa-camera] |
| `KSADeviceContextEx.Allocator` | [current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L67) | Allocator for the hole buffer, by reflection |
| `ComputePipelineWrapper` (`storageBuffers:`) | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32) | Image, depth sampler, storage buffer in that binding order |

## Pitfalls and version notes

- **Measure across the path, not around the point.** The same burst dents the skin (0.57 m deep for
  a 5"/54, per the author) through KSA's own impact path, and a sphere around the undented contact
  point never reaches the drawn surface; the mark is a cylinder along `inward`.
- **Hold `Part`, not an id.** The hole keeps the `Part` reference, so a breakup that moves the part
  to another craft takes its holes with it; `Prune` (once a second) drops holes whose part is no
  longer on a live craft.
- **Everything is painted, nearest first.** `MostHolesPainted = BulletHoles.MaxHoles` (400). The
  CPU cost is logged at debug level every 300 recorded views.
- **Plan vs build.** `docs/DAMAGE-DECALS.md` planned image decals as a rasterised projected box per
  decal from a new Harmony postfix on `RenderTarget.ResolveAttachments`, main viewport only, with
  a depth descriptor ring, anchors stored as sub-part instance id + position + normal reused from
  `HullTest`'s discarded out-parameters, and `Sim/Decal*.cs` files. What was built takes the ring
  pass's route instead: no new patch or pipeline state, a fresh `RayCastEgo` in `TryHullHit`
  (`HullTest.cs` L59 still discards them), the path direction instead of the normal, a `Part`
  reference instead of an id, every viewport, procedural looks, and one tiled compute dispatch.
  Image decals and a proximity burst's scorch on a hull are still unbuilt.[^ksarmory-damage-decals][^ksarmory-hulltest]
- **Doc drift.** `DAMAGE-DECALS.md` cites `RayCastEgo` at `KSA/Part.cs:2306`; in build 5554 it is
  L2534 (behaviour as described: the near point is in the closest sub-part's frame, the normal is
  the first vertex's). `CLAUDE.md`'s layout table says the hole shader is "one dispatch per hole over
  its own few pixels, the nearest 96 in view"; the code is one dispatch over the union rect with a
  per-tile bitmask, painting up to 400.[^ksarmory-claude]

## See also

- [Hull triangle hit test](./hull-triangle-hit-test.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Holed tank leaks](./holed-tank-leaks.md)
- [Compute shell tracers](./compute-shell-tracers.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Procedural bullet holes painted by compute from depth, stored in the struck subpart's frame](../../../howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-3.md) - how-to article demonstrating this feature
- [Paint holes that stay on a moving hull: sub-part frames and a tile bitmask gather](../../../howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/reconstruct-scene-positions-from-depth-in-a-compute-shader-2.md) - how-to article demonstrating this feature

[^ksarmory-bulletholes]: src/KSArmory/Ksa/BulletHoles.cs
[^ksarmory-hole-comp]: src/KSArmory/Shaders/KSArmoryHole.comp
[^ksarmory-holelook]: src/KSArmory/Sim/HoleLook.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-hulltest]: src/KSArmory/Ksa/HullTest.cs
[^ksarmory-damage-decals]: docs/DAMAGE-DECALS.md
[^ksarmory-claude]: CLAUDE.md
[^ksa-part]: KSA.Part (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-parttree]: KSA.PartTree (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-ksadevicecontextex]: Core.KSADeviceContextEx (decompiled)
[^ksa-computepipelinewrapper]: KSA.Rendering.ComputePipelineWrapper (decompiled)
