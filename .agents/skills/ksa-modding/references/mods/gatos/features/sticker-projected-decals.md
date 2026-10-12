---
type: Mod Feature
title: Projected sticker decals
description: Spray user images onto vessels, terrain and ground clutter as screen-space projected decals drawn after KSA resolves its offscreen target, reconstructing scene positions from the reverse-Z depth buffer in shaders compiled at runtime against the game's own GLSL includes
mod: gatos
category: rendering
techniques:
  - harmony-postfix
  - vulkan-pipeline
  - glsl-shader
  - vulkan-texture
  - dynamic-harmony-install
  - deferred-destruction
hooks:
  - harmony-postfix KSA.Rendering.RenderTarget.ResolveAttachments
  - harmony-prefix KSA.GameViewport.OnFrame
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
lifecycle: [render:grid, frame:viewports, frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Rendering.RenderTarget
  - KSA.Program
  - KSA.GridPass
  - RenderCore.ShaderModuleUtils
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.Ray
  - KSA.Cursor
  - KSA.Camera
  - KSA.Vehicle
  - KSA.Part
tags: [vulkan, decals, glsl, depth-buffer, reverse-z, raycast, bindless, harmony]
---

`/sim/paint/stickers` places uploaded images on the world: point the camera at a fuel tank and
spray, or place a sticker by latitude and longitude on a mountainside. Each sticker is a projected
decal, so it wraps hull curvature, tessellated terrain and even ground clutter, which has no
CPU-side transform at all. Stickers ride parts through flight and survive vessel switches, warp and
planet rotation. The renderer is a close port of KSA's own map-grid pass, which makes it a
reference for adding any post-resolve, depth-aware pass.

## How it works

**Injection point** ([gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs#L37-L86)):
a postfix on `RenderTarget.ResolveAttachments(CommandBuffer, bool)`. In 5554 `Program.RenderGame`
resolves colour only after translucency (`inResolveDepth: false`), draws bloom, orbit lines and
gizmos, then resolves colour and depth and runs `GridPass` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4904-L4936)).
After the full resolve the single-sample depth and colour images are both current and not bound
as attachments: the window `GridPass` draws in. The postfix bails unless the depth flag is true,
the scene is not the editor, `__instance` is `Program.OffscreenTarget` and
`Program.RenderedViewport` is `Program.MainViewport`, which keeps crew-portrait and secondary
viewports untouched.

**Pass** ([gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L436-L470)): barrier
the depth image to sampled-read and the colour image to attachment read/write with KSA's own
`BarrierBatch` presets, begin dynamic rendering on the colour image with `LoadOp.Load`, and draw one
unit cube per sticker with `CullFront`, no depth test and alpha-over blending.

**Fragment shader** ([gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L646-L738)):
fetch the resolved reverse-Z depth at the fragment, rebuild NDC and multiply by
`global.camera.inverseProjection` then `inverseView` to get the ego-space position, take the surface
normal from screen derivatives (before any discard), discard far-plane pixels (`z <= 0` in reverse-Z)
and anything outside the decal's unit box or facing away, then sample the sticker from the global
bindless texture array and shade with a sun term.

**Runtime-compiled GLSL.** The vertex and fragment shaders are C# raw strings compiled with
`ShaderModuleUtils.FromString(device, utf8, stage, null, debugName)`. The `debugName` is a
NUL-terminated path next to the stock `Grid.frag` (found through the `GridFrag` shader asset), so
`#include "Common/Camera.glsl"` and `Common/TextureSet.glsl` resolve against the game's own shader
library ([gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L341-L354)).

**Anchors and picking.** `StickerAnchors` composes decal space every frame in `double` from a part's
local frame or from geodetic lat/lon, never storing ecliptic coordinates, so anchors survive
floating-origin shifts and bubble switches. `StickerPicker` turns the camera centre or cursor ray
(`Cursor.GetEgoRay(Program.MainViewport)`) into an anchor using KSA's watertight triangle raycast
(`Ray.RaycastWatertight`) against part view meshes, falling back to a 64-step march plus 24 bisections
against the terrain height field.

**Textures and lifecycle.** Images come from the same store as the clutter override but are
allocated their own bindless slots with `AddTexture`; `FreeTexture` rewrites a freed slot to the
engine's empty texture so an already-recorded draw samples white, and the image itself goes on the
shared retire queue. The pipeline, unit cube and depth-descriptor ring exist only while the registry
is non-empty; the dynamic `Harmony("gatos.stickers")` patch exists only while at least one sticker is
live. Teardown clears `Active`, unpatches, then waits for the device to idle before freeing.

## Lifecycle and timing

- `render:grid`: the postfix records into the frame's command buffer on the main thread during
  `Program.RenderGame`, after the post-gizmo resolve and before the map grid and ImGui.
- `place`/`spray` commands execute in the Frame-phase drain (the main-viewport `GameViewport.OnFrame`
  prefix, `frame:viewports`). `StickerManager.Tick` runs later in `DrivePaint`, from
  `StarMapBeforeGui` (`frame:draw-ui`, after this frame's camera was built and before the scene
  renders): re-resolve anchors and textures, recompose ego-space decal matrices against the main
  camera, publish an immutable entry array for the postfix, and install or remove the patch on the
  0-to-1 and 1-to-0 live transitions.
- On hidden-HUD frames `DrivePaint` runs from `StarMapAfterOnFrame`, after the frame has rendered,
  so the next frame draws matrices composed against the previous frame's camera; ego space is
  camera-relative, so a moving camera can make stickers trail by one frame while the HUD is hidden
  (inferred from the code; not covered by the repo's validation notes).
- A dormant sticker (vessel despawned, part staged, image evicted) keeps its registry entry but does
  not keep the patch installed.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs) | postfix filter |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs) | pipeline, cube, descriptor ring, pass, GLSL |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerManager.cs) | registry, per-frame driver, lazy GPU and patch, teardown |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerAnchors.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerAnchors.cs) | double-precision decal-space composition |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerPicker.cs) | part raycast and terrain march |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerTextureBinder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerTextureBinder.cs) | bindless `AddTexture`/`FreeTexture` |
| [gatOS.SimFs/Paint/Stickers/StickerCommands.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Paint/Stickers/StickerCommands.cs) | `place`/`spray` grammars |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs L59-L77
    private static void Postfix(RenderTarget __instance, CommandBuffer inCmdBuffer, bool __1)
    {
        if (!__1 || !StickerManager.Active)
            return;
        try
        {
            // The editor renders the SAME offscreen target through the main viewport index, so both
            // identity checks below pass in the VAB. Stickers are a flight-scene feature: a body
            // anchor still resolves in the editor and would otherwise draw over the hangar.
            if (Program.EditorFlag)
                return;
            // Main viewport only: every other viewport resolves its own target with its own camera,
            // and the decal matrices were composed against the main camera this frame.
            if (!ReferenceEquals(__instance, Program.OffscreenTarget)
                || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport))
                return;
            StickerManager.Instance?.RecordPass(inCmdBuffer);
        }
        catch (Exception ex)
```

```glsl
// gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L668-L696 (GLSL inside a C# string)
          void main()
          {
              // Screen-sized and single-sample after ResolveAttachments: exactly one texel per fragment.
              ivec2 size = textureSize(sceneDepth, 0);
              float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;

              // Same convention as Camera.ScreenToEgoNearPlane (KSA/Camera.cs:658-671): ndc = 2*p/size - 1
              // on BOTH axes, no Y flip -- the projection already carries it (M22 is negated in
              // ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ).
              vec2 ndc = (gl_FragCoord.xy / vec2(size)) * 2.0 - 1.0;
              vec4 v = global.camera.inverseProjection * vec4(ndc, z, 1.0);
              v /= v.w;

              // The view matrix is rotation-only (KSA/Camera.cs:482-492), so undoing it lands in ego.
              vec3 pEgo = (global.camera.inverseView * vec4(v.xyz, 1.0)).xyz;

              // The receiving surface's normal, from the reconstructed position's screen derivatives.
              // Taken BEFORE any discard: derivatives are only defined in uniform control flow, and a
              // neighbour that has already been discarded would otherwise make this undefined rather
              // than merely noisy. (Noisy it still is at a depth discontinuity -- that is the known
              // one-pixel edge artifact of a projected decal, and the NaN-safe tests below eat it.)
              vec3 n = normalize(cross(dFdx(pEgo), dFdy(pEgo)));

              // Reverse-Z: 0 is the far plane AND what untouched background reads as. A decal has
              // nothing to stick to there.
              if (z <= 0.0) discard;

              vec4 p4 = vec4(pEgo, 1.0);
              vec3 pDec = vec3(dot(st.e2d0, p4), dot(st.e2d1, p4), dot(st.e2d2, p4));
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `RenderTarget.ResolveAttachments(CommandBuffer, bool)`, `DepthImage`, `ColorImage` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L315-L330), [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L36-L38) | postfix target, images |
| `GridPass` | [current/decomp/KSA/GridPass.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GridPass.cs) | the pass this one ports |
| `ShaderModuleUtils.FromString` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L79) | runtime GLSL compile with includes |
| `BindlessTextureLibrary.AddTexture`, `FreeTexture` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L220) | sticker texture slots |
| `Ray.RaycastWatertight` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L141-L141) | part picking |
| `Program.RenderedViewport`, `MainViewport`, `OffscreenTarget`, `EditorFlag` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456-L490) | filters |
| camera block in `Common/Global.glsl` (included by `Common/Camera.glsl`) | [current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L19-L20) | `inverseView`, `inverseProjection` |

## Pitfalls and version notes

- A decal pass needs single-sample depth; draw only after the depth resolve (the colour-only resolve
  call passes `false`, which the postfix checks via the bool argument).
- Derivatives are undefined after a discard in non-uniform control flow; compute the normal first.
- Reverse-Z: the far plane is depth 0, which is also what empty background reads as.
- `Cursor.InputRay` was removed at 5402 in favour of `Cursor.GetEgoRay(IViewport)` (gatOS notes).
- The include resolver reads the debug name as a C string; omit the NUL and includes fail.
- Stickers are main-viewport only in v1; in-game validation is listed as pending in the repo.

## See also

- [Ground-clutter texture override](clutter-texture-override.md)
- [World-space textured quad (thug_life)](thug-life-world-space-quad.md)
- [Vehicle and EVA paint](vehicle-and-eva-paint.md)
- [Programmable camera director](programmable-camera-director.md)
- [gatOS overview](../overview.md)
- [Sticker pass after resolve with runtime GLSL including the game library](../../../howto/rendering/textures-and-decals/project-decals-onto-scene-geometry/project-decals-onto-scene-geometry-2.md) - how-to article demonstrating this feature
- [Postfix RenderTarget.ResolveAttachments for a post-resolve pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-1.md) - how-to article demonstrating this feature
- [Compile GLSL at runtime: a sticker fragment shader compiled with FromString and the game's includes](../../../howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-2.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: Ray.RaycastWatertight against parts, then a terrain march with bisection](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-5.md) - how-to article demonstrating this feature

