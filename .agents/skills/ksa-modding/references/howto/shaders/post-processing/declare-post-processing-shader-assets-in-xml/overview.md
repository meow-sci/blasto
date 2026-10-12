---
type: Game Feature
title: Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets
description: The four ShaderExtensions XML asset types, how Id, RenderPassId, paths and binding children fit together, and how references let a later pass read an earlier pass's image
category: shaders
group: post-processing
topic: declare-post-processing-shader-assets-in-xml
demonstrated_by:
  - mods/mpfx/features/effect-chain-assets
  - mods/mpfx/features/separable-gaussian-blur
  - mods/ksa-aircrafthud/features/glass-post-process-chain
  - mods/ksa-aircrafthud/features/imgui-window-hud-shader
  - mods/shaderextensions/features/cross-pass-image-references
  - mods/shaderextensions/features/post-processing-pass-chain
lifecycle: [startup:asset-load, render:post-process]
game_types:
  - KSA.ShaderReference
  - KSA.SerializedId
  - KSA.ModLibrary
  - KSA.AssetBundle
  - KSA.Rendering.RenderImage
  - KSA.Program
  - RenderCore.RenderTechnique
tags: [xml-asset, post-processing, shaderextensions, render-pass-id, imguishader, kittenextensions]
---
A ShaderExtensions effect is declared, not coded: one XML element per pass, listed in the mod's
`assets` array. This topic covers which element to use, what its attributes and children mean, and
how a whole chain, including passes that read earlier images, fits in one small asset file with no
C# render code.

## Engine background

- **Asset bundles.** KSA loads every file listed under `assets` in `mod.toml` as an `AssetBundle`, a
  list of `SerializedId` entries. `AssetBundle.OnDataLoad` calls `OnDataLoad` on each top-level entry
  ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108)). Asset type names that are not in the base game
  (`ShaderEx`, `PostProcessingShader`, ...) exist because KittenExtensions turns each `[KxAsset]`
  attribute into an allowed child of `<Assets>`.
- **Shader references.** `<Shader>` elements in game XML are `ShaderReference` assets; the stage comes
  from the file extension and the game compiles the file
  ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L180)). Every ShaderExtensions asset is a
  `ShaderReference` subclass, so a pass can be looked up by `Id` like any shader.
- **Bind.** `ModLibrary.Bind` runs after `LoadAll` and resolves the child bindings of every `ShaderEx`
  on thread-pool threads. See [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md).
- **The chains.** `Program.RenderGame` has no post-process stage of its own; ShaderExtensions records one
  chain before the ImGui pass and one after it. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## The asset types

| Element | Runs | Input image | Typical use |
|---|---|---|---|
| `<ShaderEx Id Path>` | not by itself | n/a | A fragment shader with extra `<TextureBinding>`, buffer and push-constant children; the base of the others |
| `<PostProcessingShader Id Path RenderPassId>` | before ImGui, on the HDR scene | previous pass, or the game offscreen image | Colour grading, blur, glass |
| `<GlobalPostShader Id Path RenderPassId>` | after ImGui, on the finished swapchain image | previous pass, or a copy of the swapchain image | Grain, CRT curvature, anything covering the UI |
| `<ImGuiShader Id>` with `<Vertex Id>` and `<Fragment Path>` | per tagged ImGui window | the window re-rendered offscreen | Per-window effects; see [Bend the picture with CRT curvature and chromatic aberration](../../screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/overview.md) |

Attributes of the two chain elements ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L8-L25)):

| Attribute | Meaning |
|---|---|
| `Id` | Unique id; also how another element references this one's buffers |
| `Path` | Fragment shader file relative to the mod folder |
| `RenderPassId` | Integer order inside its own chain (default 0); ties keep load order but the README does not guarantee it |
| `VertexShaderID` | Vertex shader asset id, default `ScreenspaceVert` |
| `SubpassId`, `RequiresUniqueRenderpass` | Removed; a shader that still sets them is skipped with a logged error |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Paired pre and post assets sharing a RenderPassId and buffer](declare-post-processing-shader-assets-in-xml-1.md) | MPFX | `xml-asset` | `startup:asset-load`, `render:post-process` | 32 assets for 16 effects, no render code; every pass always runs |
| [A small file with a chain, input reference and ImGuiShader](declare-post-processing-shader-assets-in-xml-2.md) | KSA_AircraftHUD | `xml-asset` | `startup:asset-load`, `render:post-process` | Three chained passes plus an `ImGuiShader`; per-pass push constants; binding numbers follow document order; written for the old API |
| [Input and output references resolved while the chain is built](declare-post-processing-shader-assets-in-xml-3.md) | ShaderExtensions | `post-processing` | `render:post-process` | The rules behind references: stage defaults, resolution order, backwards-only |

## See also

- [Effect chain declared as RenderPassId-ordered post-process assets](../../../../mods/mpfx/features/effect-chain-assets.md), [Cross-pass image references](../../../../mods/shaderextensions/features/cross-pass-image-references.md), [RenderPassId-ordered fullscreen pass chain](../../../../mods/shaderextensions/features/post-processing-pass-chain.md)
- [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../choose-pre-or-post-imgui-post-processing/overview.md), [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md), [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md), [Blur the screen with a two-pass separable Gaussian](../../screen-effects/blur-the-screen-with-a-separable-gaussian/overview.md), [Bend the picture with CRT curvature and chromatic aberration](../../screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/overview.md)
