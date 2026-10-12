---
type: Mod Feature
title: Targeting rings painted on the depth buffer
description: Sight, reach, designation and ICBM target rings are collected in the UI pass anchored to their body and painted before bloom by one compute dispatch per ring that rebuilds the scene point from depth and draws an exact circle or ellipse with a fixed pixel-width line
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - post-processing
  - depth-buffer-projection
  - octahedral-packing
  - screen-tile-dispatch
  - body-fixed-anchor
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:draw-ui, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Camera
  - KSA.ViewProjection
  - KSA.IViewport
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.ImageBarrierInfo
  - KSA.Program
tags:
  - targeting-ring
  - ground-overlay
  - depth-reconstruction
  - compute-shader
  - push-constants
  - octahedral-encoding
  - screen-space-decal
---

Every targeting ring KSArmory shows on the ground (the bomb sight's impact ring, a falling store's
dashed landing ring and its blue reach ring, the click-to-shoot designation marker, ICBM aim and
target rings, and the bus's divert-reach ellipse) is painted into the scene image by the mod's own
compute shader, not drawn as lines. Each ring is a circle or ellipse measured from the scene point
the depth buffer holds under each pixel, so it lies exactly on terrain, a pad or a rock at any size,
and its line stays a fixed number of pixels wide however far off or oblique. It needs no terrain
lookups at all, which is why it replaced the earlier draped rings (80 terrain lookups a frame, 0.98
of the mod's 1.16 ms on the pad, per the author).[^ksarmory-ring-comp][^ksarmory-claude]

## How it works

**1. Collect, anchored to the body** ([src/KSArmory/Ksa/GroundRings.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundRings.cs)):[^ksarmory-groundrings]

| Call | Stored |
|---|---|
| `Add(centreEcl, radius, inner, colour, dashes)` | `KsaWorld.TryAnchorToGround`: the nearest `Celestial` by `|p - GetPositionEcl()| - MeanRadius`, and `conj(GetBodyFixed2Ecl()) x (p - GetPositionEcl())`, a body-fixed offset that does not move with the planet[^ksarmory-ksaworld] |
| `AddEllipse(centreEcl, majorEcl, minor, colour)` | Centre and the long axis's tip as two ground anchors on the same body, so the axis turns with the planet |
| `BeginFrame()` | Clears the list and restarts a wall-clock stopwatch; `Fresh` is false after 0.5 s without one |

Callers, all in the UI pass after `BeginFrame` (KSArmoryMod.cs L537): `BombSightOverlay` (impact ring
plus an inner ring at 0.15 R, radius = the store's `Warhead.LethalRadius`), `StoreReach` (white dashed
landing ring with `GroundRings.Dashed = 48`, blue reach ring), `Designator` and `SiteDesignator`
(cursor markers), `IcbmOverlay` (aim, other targets, divert-reach ellipse).[^ksarmory-bombsightoverlay][^ksarmory-storereach][^ksarmory-designator][^ksarmory-sitedesignator][^ksarmory-icbmoverlay]
The colour's alpha is the brightness kept over dark ground (0.04 to 0.05 in every caller).

**2. Resolve at record time.** `GroundRings.TryAt` turns each anchor back into ecliptic with
`KsaWorld.TryGroundAnchorEcl` (`GetPositionEcl() + GetBodyFixed2Ecl() x anchor`), takes the local
vertical as `unit(centre - body centre)`, and for an ellipse projects the tip direction onto the
ground plane. This happens inside the render, against the camera of that instant, so the ring does
not lag the planet's ~30 km/s.[^ksa-celestial]

**3. Dispatch** (`CloudPass.Rings`, L1009-L1060):[^ksarmory-cloudpass]

- Skipped when `GroundRings.Count == 0` or `!Fresh`; pipeline built lazily per view with
  `storage [scene colour]`, `depth [scene depth]`, one push range (`BuildRings` L1756-L1784).
- Centre is differenced against `camera.PositionEcl` in `double`, then narrowed.
- `TileFor(camera, centre, AnyPerpendicular(up), radius, 1.15, ...)` projects the 8 corners of a box
  (`-1.2R .. +1.15R` along, `+-1.8R` in the two other axes, reusing the `ScorchFootprint`
  constants) to whole 8x8 workgroups. Any corner behind the camera takes the whole screen.[^ksarmory-scorchfootprint]
- A compute write-to-read `Hazard` barrier between consecutive painted things; `Dispatch(tile.GroupsX,
  tile.GroupsY, 1)` per ring.

**Push constant, exactly 128 bytes** (Vulkan's guaranteed minimum):

| Field | Circle | Ellipse |
|---|---|---|
| `mat4 InvViewProj` | `camera.VPInv.viewProjection` (clip to camera-relative world) | same |
| `CentreRadius` | centre (Ego), radius | centre, semi-major `a` |
| `UpInner` | up (xyz), inner ring radius or 0 | `OctahedralPack(up)` (xy), `OctahedralPack(major)` (zw) |
| `Colour` | rgb ink, brightness floor | same |
| `TileWidth` | tile origin (xy), half-width 1.75 px, dash count or 0 | origin, half-width, **`-minor`** (negative marks an ellipse) |

`OctahedralPack` (CloudPass.cs L203-L220) stores a unit vector as two floats; the shader's
`OctahedralUnpack` reverses it.

**4. Shade** (`KSArmoryRing.comp`, `local_size 8x8`, bindings: 0 = `sceneColor` rgba16f storage,
1 = `sceneDepth`):

1. Pixel = invocation + tile origin; reverse-Z depth `<= 0` is sky, skip.
2. `p = InvViewProj x (uv*2-1, depth, 1)`, divided by w.
3. Height `h = dot(p - centre, up)`; skip if `|h| > max(R, 50 m)` (a craft or tower over the ring is
   not the ground). `r = |p - centre - up h|`.
4. Ellipse: normalised radius `n = sqrt((u/a)^2 + (v/b)^2)`, distance off the edge `(n - 1)/|grad n|`,
   then treated as a circle of radius `a`.
5. Pixel footprint along the radius: neighbouring pixels reconstructed at the same depth, slid along
   the view ray onto the tangent plane (`RadialStep`), so the line keeps its width at grazing angles.
6. Coverage with a half-pixel smoothstep, plus a dark border one pixel wider; optional inner ring;
   dashes by `atan` angle around `up` (55% duty).
7. Ink scales with the ground: `colour x max(5 x groundLuma, 4 x floor)`, because the image is not yet
   exposed and a fixed brightness would glare at night and vanish by day. Border darkens to 15%.

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` = postfix on `Program.OnDrawUiViewports` (Program.cs L2268), `OnAfterGui` after `StepOnce` | `GroundRings.BeginFrame()`, then every overlay adds its rings[^ksarmory-mod] |
| `render:bloom` | Prefix on `SunbloomRenderer.Render`: main view at Program.cs L4915 (before `_overallBloomRenderer.Render` L4916; colour in `General` via `StorageReadFc`, depth `DepthSampledReadVfc`, L4910-L4914), camera windows via `RenderViewport` L4584 | `CloudPass.Record` -> `Rings` per viewport[^ksa-program][^ksa-imagebarrierinfo] |

- **Collected in the UI pass on purpose.** A ring is an operator's sight, so it goes with the HUD:
  KSA skips `OnDrawUiViewports` while `Program.DrawUI` is false (F2, screenshots), the list is not
  renewed, and `Fresh` lets the last list stand for 0.5 s (a screenshot's few frames) before F2 takes
  the rings away.
- **Painted with the cloud pass off.** `Record` with `tint <= 0` (`Config.ShaderPass = 0`) still
  calls `Rings`, `Holes`, `Streams` and `Tracers` (CloudPass.cs L404-L414). With a burst on screen the
  rings go after the scorch marks and before the clouds, so a cloud over a target covers its ring.
- **Before bloom** so the line blooms and tonemaps with the ground it lies on.
- `Program.GetRenderCamera()` is `RenderedViewport.GetCamera()` (Program.cs L641), so inside the
  prefix it is the camera of the viewport being recorded and a camera window gets its own rings.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/GroundRings.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundRings.cs) | Per-frame ring list, body anchors, staleness |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L992-L1060) | `RingPush` layout and the per-ring dispatch |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L2096-L2141) | `TileFor`: screen patch as whole workgroups |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L200-L220) | `OctahedralPack` |
| [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp) | Depth reconstruction, circle/ellipse distance, fixed-pixel line |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L922-L1000) | `TryAnchorToGround` / `TryGroundAnchorEcl` |
| [src/KSArmory/Ksa/IcbmOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmOverlay.cs#L100-L140) | Rings and the reach ellipse for a ballistic shot |

## Snippets

```csharp
// src/KSArmory/Ksa/GroundRings.cs L98-L128
        if (index < 0 || index >= _rings.Count) return false;

        Ring ring = _rings[index];
        if (!KsaWorld.TryGroundAnchorEcl(ring.Body, ring.Anchor, out centreEcl, out _)) return false;
        if (ring.Body is not Celestial body) return false;

        try
        {
            up = Vec.Unit(centreEcl - body.GetPositionEcl());
        }
        catch
        {
            return false;
        }

        radius = ring.Radius;
        inner = ring.Inner;
        colour = ring.Colour;
        dashes = ring.Dashes;

        if (ring.Minor > 0.0)
        {
            if (!KsaWorld.TryGroundAnchorEcl(ring.Body, ring.TipAnchor, out double3 tipEcl, out _)) return false;

            double3 axis = tipEcl - centreEcl;
            major = Vec.Unit(axis - (up * Vec.Dot(axis, up)));
            minor = ring.Minor;
            if (Vec.Len2(major) < 0.5) return false;
        }

        return Vec.Len2(up) > 0.5;
```

```csharp
// src/KSArmory/Ksa/CloudPass.cs L1023-L1056
                if (!GroundRings.TryAt(i, out double3 centreEcl, out double3 up, out double radius,
                                       out double inner, out float4 colour, out int dashes,
                                       out double3 major, out double minor)) continue;

                double3 centre = centreEcl - camera.PositionEcl;
                if (!Vec.IsFinite(centre)) continue;

                Tile tile = TileFor(camera, centre, Vec.AnyPerpendicular(up), radius, RingTileReach,
                                    width, height);
                if (tile.Empty) continue;

                if (marks > 0) Hazard(commandBuffer);

                RingPush push = new()
                {
                    InvViewProj = camera.VPInv.viewProjection,
                    CentreRadius = new float4((float)centre.X, (float)centre.Y, (float)centre.Z, (float)radius),
                    UpInner = new float4((float)up.X, (float)up.Y, (float)up.Z, (float)inner),
                    Colour = colour,
                    TileWidth = new float4(tile.OriginX, tile.OriginY, RingHalfWidthPixels, dashes),
                };

                // An ellipse: both directions packed into the four floats the up and the inner ring
                // take, and the short radius where the dashes go, negative to say which this is.
                if (minor > 0.0)
                {
                    float2 upPacked = OctahedralPack(up);
                    float2 majorPacked = OctahedralPack(major);
                    push.UpInner = new float4(upPacked.X, upPacked.Y, majorPacked.X, majorPacked.Y);
                    push.TileWidth.W = -(float)minor;
                }

                view.Rings!.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
                commandBuffer.Dispatch(tile.GroupsX, tile.GroupsY, 1);
```

```glsl
// src/KSArmory/Shaders/KSArmoryRing.comp L62-L111
    ivec2 size = imageSize(sceneColor);
    ivec2 at = ivec2(gl_GlobalInvocationID.xy) + ivec2(pc.TileWidth.xy);
    if (at.x >= size.x || at.y >= size.y) return;

    // Reverse-Z: zero is the sky, which a ring on the ground has no business with.
    float deviceZ = texelFetch(sceneDepth, at, 0).r;
    if (deviceZ <= 0.0) return;

    vec2 uv = (vec2(at) + 0.5) / vec2(size);
    vec3 p = WorldAt(uv, deviceZ);

    bool ellipse = pc.TileWidth.w < 0.0;
    vec3 up = ellipse ? OctahedralUnpack(pc.UpInner.xy) : pc.UpInner.xyz;
    vec3 d = p - pc.CentreRadius.xyz;
    float h = dot(d, up);
    vec3 across = d - up * h;
    float r = length(across);
    float R = pc.CentreRadius.w;

    // A craft or a tower standing well over the ring is not the ground it circles.
    if (abs(h) > max(R, 50.0) || r < 1.0e-3) return;

    vec3 dir = normalize(p);
    vec3 radial = across / r;
    // ...
    float stepX = RadialStep(p, dir, WorldAt(uv + vec2(1.0 / float(size.x), 0.0), deviceZ) - p, up, radial);
    float stepY = RadialStep(p, dir, WorldAt(uv + vec2(0.0, 1.0 / float(size.y)), deviceZ) - p, up, radial);
    float footprint = max(length(vec2(stepX, stepY)), 1.0e-4);

    float halfWidth = pc.TileWidth.z;
    float border;
    float cover = Line(r, R, footprint, halfWidth, border);
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `SunbloomRenderer.Render` call sites, `RenderViewport`, `GetRenderCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4905-L4916) | Where the paint is recorded; camera of the viewport being drawn (L641) |
| `Universe.CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114) | Bodies searched for the nearest surface[^ksa-universe] |
| `Celestial.GetBodyFixed2Ecl`, `MeanRadius`, `GetPositionEcl` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L87-L174) | Body-fixed anchor and its live ecliptic position (L174, L87) |
| `Camera.VPInv`, `PositionEcl`, `EgoToClipDouble` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L63-L118) | Inverse view-projection for the shader (L65), camera origin (L110), tile projection (L277)[^ksa-camera] |
| `ViewProjection.viewProjection` | [current/decomp/KSA/ViewProjection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewProjection.cs#L13) | The `float4x4` pushed[^ksa-viewprojection] |
| `ComputePipelineWrapper` ctor, `BindPipeline<T>` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32) | Binding 0 storage colour, binding 1 depth sampler in `DepthReadOnlyOptimal`; push constant upload (L210)[^ksa-computepipelinewrapper] |
| `ImageBarrierInfo.Presets.StorageReadFc`, `DepthSampledReadVfc` | [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L45-L57) | The layouts KSA leaves the targets in before the prefix |

## Pitfalls and version notes

- **No fallback.** If `KSArmoryRingCompute` is missing or the hook is not installed, rings do not
  draw at all; the author chose that over the 80-lookup draped lines.
- **Fixed pixel width needs the tangent-plane step.** A footprint taken from the raw neighbour
  distance along the view ray swells the line toward the horizon; `RadialStep` slides it back onto
  the ground plane (clamped at a facing of 0.05).
- **Push constant budget.** 128 bytes is the Vulkan minimum and is full; the ellipse case fits only
  by octahedral packing and by signalling ellipse-ness through the sign of the dash field.
- **Tile behind the camera.** Any box corner with `clip.w <= 1e-6` makes the dispatch full-screen,
  which is correct (the ring the viewer stands in fills the frame) but costs a whole-screen pass.
- **Staleness is wall-clock.** `Fresh` uses a `Stopwatch`, not simulated time, so a paused game with
  the HUD visible keeps renewing rings every frame and keeps them.
- **The 50 m height gate** means a ring will not paint on a structure much taller than the ring is
  wide; a ring on a pad is fine, a ring under a tall tower shows a gap.

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Guided bomb tail kit and sight](./guided-bomb-tail-kit-and-sight.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [Ground target designation and reach](./ground-target-designation-and-reach.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [Gizmo world overlays](./gizmo-world-overlays.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Bullet-hole decals](./bullet-hole-decals.md)
- [Depth-reading compute dispatches for rings, holes, tracers and relighting](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-1.md) - how-to article demonstrating this feature
- [Paint an exact ring on the ground: depth reconstruction, one dispatch per ring](../../../howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/reconstruct-scene-positions-from-depth-in-a-compute-shader-1.md) - how-to article demonstrating this feature

[^ksarmory-groundrings]: src/KSArmory/Ksa/GroundRings.cs
[^ksarmory-ring-comp]: src/KSArmory/Shaders/KSArmoryRing.comp
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-bombsightoverlay]: src/KSArmory/Ksa/BombSightOverlay.cs
[^ksarmory-storereach]: src/KSArmory/Ksa/StoreReach.cs
[^ksarmory-icbmoverlay]: src/KSArmory/Ksa/IcbmOverlay.cs
[^ksarmory-designator]: src/KSArmory/Ksa/Designator.cs
[^ksarmory-sitedesignator]: src/KSArmory/Ksa/SiteDesignator.cs
[^ksarmory-scorchfootprint]: src/KSArmory/Sim/ScorchFootprint.cs
[^ksarmory-claude]: CLAUDE.md
[^ksa-program]: KSA.Program (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-viewprojection]: KSA.ViewProjection (decompiled)
[^ksa-computepipelinewrapper]: KSA.Rendering.ComputePipelineWrapper (decompiled)
[^ksa-imagebarrierinfo]: KSA.Rendering.ImageBarrierInfo (decompiled)
