---
type: How-To
title: Shader transform at compile time with packed colours and runtime masters
description: Apply the same shader and state-bit technique with global, per-template, per-vessel or per-instance scope, restore stock rendering when disabled, and clone materials for EVA suit, fur and visor
category: rendering
group: textures-and-decals
topic: paint-vessel-parts-with-custom-colours
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/vehicle-and-eva-paint]
techniques: [glsl-shader, harmony-postfix, reflection]
hooks:
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - harmony-postfix KSA.PartTreeRenderData.WriteDynamicState
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.GameViewport.OnFrame
lifecycle: [frame:prepare, frame:viewports, frame:draw-ui, frame:after-on-frame, frame:render]
game_types: [KSA.PartTreeRenderData, KSA.ShaderReference, RenderCore.ShaderModuleUtils, KSA.PartModelRenderer]
tags: [paint, colour, shader-transform, eva, material-clone, masters]
---

Outcome: the vehicle paint of approach 1 driven from a runtime command surface (`/sim/paint`), with scopes global, per part template, per vessel or per part instance, modes `multiply`, `tint` and `replace`, a transactional arm/disarm that restores stock rendering, and a second half for EVA kittens (suit, fur, helmet, visor, MMU) that clones materials instead of editing shared ones. Demonstrated by gatOS ([Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md)). Prerequisites: StarMap, Harmony;
read approach 1 for the mechanism.

## Background: how the game does it

The same seams as approach 1: `ShaderModuleUtils.FromFile` called from `ShaderReference` [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L105); the private `WriteState` / `WriteDynamicState` writers of the cached `StateBitFlags` slots [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1088); the public `PartTreeRenderData.InvalidateStates()` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L323); `Program.RendererRebuildNeeded` consumed at the top of the next `PrepareFrame` (`RebuildRenderer`).
Kitten materials: `GpuMaterialSystem.CreateObject` makes a material record; the avatar's renderables hold protected `MaterialIndices` arrays; KSA's material buffer is device-local, so a shared material is never edited.

## Lifecycle and timing

- Paint commands execute in the Frame-phase drain (the main-viewport `GameViewport.OnFrame` prefix, `frame:viewports`). The per-frame `PaintManager.Tick` runs later in `DrivePaint`, from `StarMapBeforeGui` before the scene renders, or from `StarMapAfterOnFrame` on hidden-HUD frames, in which case invalidations and EVA material swaps first show in the next frame.
- `Program.RendererRebuildNeeded` is consumed at the top of the next `Program.PrepareFrame` (`frame:prepare`), where shader modules are recompiled and the prefix runs.
- Cached part states are rewritten when `PartTree.UpdateRenderData` runs `EnsureBuilt`, inside `Program.UpdateRenderingResources`, which `Program.Render` calls after `OnPreRender` and before `RenderGame`; the postfixes execute in `frame:render`, not `frame:pre-render`.
- Idle cost while disarmed is one branch; armed but unchanged, one reference and one integer compare per tick.

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Transform the shader source as a pure string function:** find the `vec3 sampledColor ...;` declaration, require the `inStateFlags` varying, and insert a block that unpacks a 7:7:7 sRGB colour from `inStateFlags >> 11`, converts it to linear, and multiplies, tints or replaces `sampledColor` (zero means unpainted).

   ```csharp
   // gatOS.Paint/PaintShaderTransform.cs L41-L62
           var apply = mode switch
           {
               PaintBlendMode.Tint =>
                   "            sampledColor = gatosPaint * (dot(sampledColor, vec3(0.2126, 0.7152, 0.0722)) * 2.0);",
               PaintBlendMode.Replace => "            sampledColor = gatosPaint;",
               _ => "            sampledColor *= gatosPaint;",
           };
           var snippet = $$"""

       {{Marker}}
       {
           uint gatosPaintPacked = inStateFlags >> {{PaintBits.Shift}}u;
           if (gatosPaintPacked != 0u)
           {
               vec3 gatosPaint = gammaToLinear(vec3(
                   float((gatosPaintPacked >> 14u) & 0x7Fu),
                   float((gatosPaintPacked >> 7u) & 0x7Fu),
                   float( gatosPaintPacked        & 0x7Fu)) * (1.0 / 127.0));
   {{apply}}
           }
       }
   """;
   ```

2. **Prefix `FromFile`** (exact four-argument overload): if the path is `MeshIndirect.frag` (or the raytraced variant) compile the transformed source with `FromString`, KSA's original options and the original path NUL-terminated, and skip the original.

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs L73-L101
   private static bool FromFilePrefix(Device device, string filePath, ref VkShaderStageFlags shaderStage,
       CompileOptions? options, ref VkShaderModule __result)
   {
       var manager = PaintRuntime.Current;
       byte[] source;
       try
       {
           if (manager is null || !manager.TryGetShaderSource(filePath, out source)) return true;
       }
       catch (Exception ex)
       {
           manager?.FaultShader(filePath, ex);
           return true;
       }
       try
       {
           var stage = ShaderModuleUtils.ShaderStageFromFileExtension(filePath);
           __result = ShaderModuleUtils.FromString(device, source, stage, options,
               System.Text.Encoding.UTF8.GetBytes(filePath + "\0"));
           shaderStage = stage;
           manager.NoteShaderCompile(filePath);
           return false;
       }
       catch (Exception ex)
       {
           manager.FaultShader(filePath, ex);
           return true;
       }
   }
   ```

3. **Arm transactionally:** refuse if any other mod has a prefix on `FromFile` (two skipping prefixes cannot compose), preflight the shader transform, install all patches on a private `Harmony` instance, invalidate every loaded tree's cached states with `InvalidateStates()`, and set `Program.RendererRebuildNeeded = true`. Rule changes and staging or docking (a Part-to-vessel index hash) invalidate states again. Disarming unpatches and rebuilds, restoring stock rendering.

4. **EVA kittens: clone materials.** Reflect `KittenEva._renderable` then `KittenRenderable._characterAvatar`, walk the avatar's renderables and their protected `MaterialIndices` arrays, create clone materials (pooled by source handle and quantized colour, capped) and write the clone handles into those arrays. Restore writes the original handle back only if the slot still holds the mod's handle, then frees the clone.

## Complete example

| File | Role |
|---|---|
| [gatOS.Paint/PaintShaderTransform.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintShaderTransform.cs) | Source transform |
| [gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs) | Prefix and state postfixes |
| [gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs#L225-L250) | Transactional arm |
| [gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs) | EVA material clones |

## Variations and gotchas

- **Re-verify the writers on every update:** they are private, so the compiler cannot help; part rendering moved into cached render data in a past build and an old interception would have compiled but painted nothing.
- **A `FromFile` prefix that returns `false` replaces compilation for everyone;** gatOS refuses to arm if the standalone Humble Arteest is installed.
- **Never write shader files on disk;** transform in memory and keep KSA's compile options and path.
- **Glass uses a separate shader and stays stock.**
- In-game validation of paint is listed as pending in the repo.

## See also

- [Overview](overview.md)
- [Approach 1: 7:7:7 colour and the patched fragment shader](paint-vessel-parts-with-custom-colours-1.md)
- [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md)
