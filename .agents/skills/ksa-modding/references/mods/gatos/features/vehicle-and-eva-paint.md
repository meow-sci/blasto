---
type: Mod Feature
title: Vehicle and EVA paint
description: Recolour vessel parts by injecting GLSL into KSA's part fragment shader at compile time and packing colours into spare per-instance state bits, and recolour EVA kittens by binding gatOS-owned material clones, both opt-in and fully reversible
mod: gatos
category: shaders
techniques: [harmony-prefix, harmony-postfix, glsl-shader, reflection, dynamic-harmony-install]
hooks:
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - harmony-postfix KSA.PartTreeRenderData.WriteDynamicState
  - reflection KSA.KittenEva._renderable
  - reflection KSA.KittenRenderable._characterAvatar
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
lifecycle: [frame:prepare, frame:viewports, frame:draw-ui, frame:after-on-frame, frame:render]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - KSA.PartTreeRenderData
  - KSA.Program
  - KSA.Part
  - KSA.PartModelDynamicModule
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
tags: [shaders, glsl, paint, harmony, materials, kittens, render-state]
---

`/sim/paint` recolours any part of any vessel (global, per part template, per vessel or per part
instance, in `multiply`, `tint` or `replace` mode) and the suit, fur, helmet, visor and MMU of EVA
kittens. Both halves are off by default behind runtime masters and restore stock rendering when
disabled. They show two different, reusable ways to change how KSA shades things without touching
any file on disk.

## How it works

**Part paint: shader transform plus state bits.**

1. *Shader.* KSA compiles shaders through `ShaderReference`, which calls
   `ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)`
   ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L105-L130)). A Harmony prefix on that exact
   overload checks whether the path is `MeshIndirect.frag` (or the optional raytraced variant); if so it
   compiles transformed source with `ShaderModuleUtils.FromString`, passing KSA's original options and
   the original path (NUL-terminated) as the debug name so `#include`s still resolve, and skips the
   original ([gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs#L73-L101)).
2. *Transform* ([gatOS.Paint/PaintShaderTransform.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintShaderTransform.cs#L11-L72)): a pure string function finds
   the `vec3 sampledColor ...;` declaration (and requires the `inStateFlags` varying), then inserts a
   block that unpacks a 7:7:7 sRGB colour from `inStateFlags >> 11`, converts it to linear, and
   multiplies, tints or replaces `sampledColor`. Zero means unpainted. The game uses bits 0..10 of
   the per-instance state flags, so bits 11..31 are free.
3. *Bits.* Since 5482 part render data is cached per tree in `PartTreeRenderData`, and the only
   writers of a part's cached `StateBitFlags` slot are the private `WriteState(Batch, int, Part)`
   and `WriteDynamicState(DynamicBatch, int, PartModelDynamicModule)`
   ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1088-L1220)). Postfixes on both OR the resolved
   paint bits into the slot. The batch types are private nested classes, so the postfix parameters
   bind as `object` and the `int[] StateBitFlags` field is reached through an
   `AccessTools.FieldRefAccess` compiled once at arm time.
4. *Arm* ([gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs#L225-L250)): refuse if any other mod
   has a prefix on `FromFile` (two skipping prefixes cannot compose), preflight the shader transform,
   install all patches on `Harmony("fail.science.meow.gatos.paint")` transactionally, invalidate every loaded tree's
   cached states with the public `PartTreeRenderData.InvalidateStates()`, and set
   `Program.RendererRebuildNeeded = true` so the next frame recompiles shaders through the patched
   path. Rule changes and staging or docking (a Part-to-vessel index hash) invalidate states again.

**EVA paint: material clones** ([gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs)): KSA's
material buffer is device-local, so gatOS never edits a shared material. It reflects
`KittenEva._renderable` then `KittenRenderable._characterAvatar`, walks the avatar's renderables
and their protected `MaterialIndices` arrays, creates clone materials with
`GpuMaterialSystem.CreateObject` (pooled by source handle and quantized colour, capped by
`paint_max_material_clones`), and writes the clone handles into those arrays. Restore writes the
original handle back only if the slot still holds gatOS's handle, then frees the clone.

## Lifecycle and timing

- Paint commands execute in the Frame-phase drain (the main-viewport `GameViewport.OnFrame`
  prefix, `frame:viewports`). The per-frame `PaintManager.Tick` runs later in `DrivePaint`, from
  `StarMapBeforeGui` before the scene renders, or from `StarMapAfterOnFrame` on hidden-HUD frames,
  in which case its state invalidations and EVA material swaps first show in the next frame.
- `Program.RendererRebuildNeeded` is consumed at the top of the next `Program.PrepareFrame`
  (`RebuildRenderer`), where shader modules are recompiled and the prefix runs (`frame:prepare`).
- Cached part states are rewritten when `PartTree.UpdateRenderData` runs `RenderData.EnsureBuilt`.
  That happens in `Program.UpdateRenderingResources` -> `Vehicle.UpdateRenderData`, which
  `Program.Render` calls after `OnPreRender` and before `RenderGame` records commands
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4360)), so the postfixes execute in `frame:render`, not
  `frame:pre-render`.
- The idle cost while disarmed is one branch; while armed but unchanged, one reference and one
  integer compare per tick.

## Key code

| Path | Role |
|---|---|
| [gatOS.Paint/PaintShaderTransform.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintShaderTransform.cs) | pure GLSL injection |
| [gatOS.Paint/PaintBits.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintBits.cs) | 7:7:7 state-bit codec |
| [gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs) | `FromFile` prefix and state-writer postfixes |
| [gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs) | arming, conflict check, invalidation, rebuild requests, ticking |
| [gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/EvaPaintBridge.cs) | kitten material clones and conditional restore |
| [plans/PAINT_ASBUILT.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/plans/PAINT_ASBUILT.md) | the full maintenance contract |

## Snippets

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)`, `FromString` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L117) | prefix target, transformed compile |
| `ShaderReference` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L105-L130) | the caller of `FromFile` |
| `PartTreeRenderData.WriteState`, `WriteDynamicState`, `InvalidateStates`, `Batch.StateBitFlags` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L323-L330) | cached per-instance state bits |
| `Program.RendererRebuildNeeded`, `RebuildRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2148) | deferred shader recompile |
| `MeshIndirect.frag` (`inStateFlags`, `sampledColor`) | [current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L31-L108) | the transformed shader |
| `GpuObjectSystem.CreateObject` | [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L45-L45) | material clone allocation |
| `KittenRenderable._characterAvatar`, `CatFurRenderable.MaterialIndices` | [current/decomp/KSA/CatFurRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatFurRenderable.cs#L22-L64) | EVA material slots |

## Pitfalls and version notes

- Rev 5456 (5482) moved part rendering into the cached `PartTreeRenderData` and deleted the
  per-module `UpdateRenderData` methods; the rasterized static path also stopped calling
  `AddInstance`, so the old interception compiled but would have painted nothing. Re-verify the
  writers on every update (they are private, so the compiler cannot help).
- A `FromFile` prefix that returns `false` replaces compilation for everyone; check for other
  owners first (gatOS refuses to arm if standalone humble-arteest is installed).
- Never write shader files on disk; transform in memory and keep KSA's compile options and path.
- Glass uses a separate shader and stays stock.
- In-game validation of paint is listed as pending in the repo.

## See also

- [Ground-clutter texture override](clutter-texture-override.md)
- [Projected sticker decals](sticker-projected-decals.md)
- [Per-vessel scale and force-render cheats](scale-and-force-render-cheats.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [gatOS overview](../overview.md)
- [Bind mod-owned material clones to the EVA renderables and restore them conditionally](../../../howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-3.md) - how-to article demonstrating this feature
- [Shader transform at compile time with packed colours and runtime masters](../../../howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-2.md) - how-to article demonstrating this feature
- [Patch shaders in memory: a compile-time transform with packed colours, a conflict check and runtime masters](../../../howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-3.md) - how-to article demonstrating this feature
- [Smuggle data into stock shaders: spare high bits of the part state flags](../../../howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-2.md) - how-to article demonstrating this feature

