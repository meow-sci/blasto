---
type: Game Feature
title: Compile and write GLSL at runtime for a mod's own pipeline
description: How a mod compiles an embedded GLSL string with the game's shaderc wrapper, what debug path and includes it needs, and three mods that build their own quad, decal or preview pipeline this way
category: shaders
group: glsl-workflow
topic: compile-glsl-at-runtime
demonstrated_by:
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/unscience/features/pebbles-workshop-offscreen-preview
  - mods/gatos/features/sticker-projected-decals
lifecycle: [frame:draw-ui, frame:render, render:translucency, render:imgui]
game_types: [RenderCore.ShaderModuleUtils, KSA.ShaderReference, RenderCore.SimpleVkMesh]
tags:
  - glsl
  - shaderc
  - runtime-compile
  - fromstring
  - debug-name
  - include
  - vulkan-pipeline
  - premultiplied-alpha
---
A mod that builds its own Vulkan pipeline (a textured quad in the world, a screen-space decal pass, an offscreen preview) needs shaders, and it does not have to ship SPIR-V. The game links shaderc and exposes it as `ShaderModuleUtils.FromString`, so a mod can embed GLSL as a string or resource and compile it when the pipeline is first built. This
topic covers the contract of that call, what the three reference mods put in their shaders, and the two failure modes that cost people time: a missing NUL on the debug path, and ownership of the resulting module.

## Engine background

- **The call.** `ShaderModuleUtils.FromString(Device device, ReadOnlySpan<byte> shaderCode, VkShaderStageFlags shaderStage, CompileOptions? options = null, ReadOnlySpan<byte> debugName = default)` compiles GLSL to SPIR-V and creates a `VkShaderModule`; compile errors throw typed shaderc exceptions
  ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L115)). The `FromFile` sibling reads a file and forwards the path as the input name ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L159)).
- **The debug name is the include base.** The name is passed to shaderc as the input file name, and the game's include resolver builds each include path as the directory of the requesting file plus the include text ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)). Pass the path of a real stock shader's directory and the game's own
  headers resolve; pass an unrelated name and `#include "Common/Camera.glsl"` fails. The resolver reads the name as a C string, so it must be NUL-terminated.
- **Ownership.** A module you compile is yours: destroy it when the pipeline holds the code. Modules from `ModLibrary.Get<ShaderReference>(...)` are owned by the library and must not be destroyed.
- **No hot reload.** The reload machinery in [Recompile a mod's shader while the game runs](../reload-a-mod-shader-in-a-running-game/overview.md) covers `<Shader>` assets only; string-compiled shaders are rebuilt when you rebuild the pipeline.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Premultiplied-alpha fragment shader paired with the stock unlit vertex shader](compile-glsl-at-runtime-1.md) | purrtty | `glsl-shader` | `frame:draw-ui`, `render:translucency` | Only the fragment stage is custom; the vertex stage is a stock `ShaderReference` module |
| [Sticker fragment shader compiled with FromString and the game's includes](compile-glsl-at-runtime-2.md) | gatOS | `glsl-shader` | `frame:draw-ui`, `frame:render` | Both stages embedded; includes the game library through a debug path next to a stock shader; reconstructs positions from depth |
| [Derivative normal mapping in a preview shader](compile-glsl-at-runtime-3.md) | unscience (pebbles) | `glsl-shader` | `frame:draw-ui`, `render:imgui` | Embedded resources in the assembly; tangent-free normal mapping for meshes without tangents |

## See also

- [World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md), [Projected sticker decals](../../../../mods/gatos/features/sticker-projected-decals.md), [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)
- [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md), [Draw a textured quad attached to a part or the camera](../../../rendering/textures-and-decals/draw-a-textured-quad-in-the-world/overview.md)
