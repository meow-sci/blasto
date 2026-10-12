---
type: How-To
title: "Include the game's shader library: a relative include of a stock helper inside a patched copy"
description: When the mod already runs a copy of the stock shader tree, a plain relative include of a stock helper works with no generated header, because includes follow the redirected path
category: shaders
group: glsl-workflow
topic: include-the-game-shader-library-in-mod-glsl
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows]
techniques: [glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.ShaderReference, RenderCore.Shaders.ShaderCompilerResolve, KSA.AssetReloader.ShaderReloader]
tags: [glsl, include, relative-include, shadow-tree, ring-shadows, realatmospheres]
---
The generated-header trick of the other two articles is for shaders that live in the mod's folder. If your mod instead patches copies of the game's own shaders ([Replace or text-patch the game's GLSL without editing game files](../../stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md)), the patched file sits in a tree that mirrors `Content/Core/Shaders`, so a plain relative include of any other stock file resolves
exactly as in the game. RealAtmospheres uses this to add the stock ring-shadow helper to both cloud passes with one line.

## Background: how the game does it

shaderc includes resolve as the directory of the requesting file combined with the include text ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)). The redirect in [Replace or text-patch the game's GLSL without editing game files](../../stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md) compiles the file at its copy path (`FromFile` gets the rewritten `filePath`), so the requesting file's directory is the copy's
`Clouds/` folder. Because the whole `Shaders` tree was copied, `../Common/RingShadows.glsl` exists there, and its own includes resolve inside the copy too. No generated header and no absolute path are needed.

## Lifecycle and timing

The text patch that inserts the include runs in `[StarMapBeforeMain]` (`startup:starmap-before-main`) on the copy; the include is resolved when the shader compiles in `ModLibrary.LoadAll` (`startup:asset-load`). The reload bookkeeping stays valid because the copy folder is named `ShadowContent`, which contains `Content`
(see the first article of this topic). See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Make sure the helper exists in your copy of the tree** and that the target shader can see the data the helper needs. RealAtmospheres checks `Common/RingShadows.glsl` and the atmosphere uniform block's `ringTextureId` before it patches anything:

   ```csharp
   // ShadowBuilder.cs L714-L722
       private static void PatchCloudRingShadows(string shadersDir)
       {
           string uboPath = Path.Combine(shadersDir, "Atmosphere", "AtmosphereDataUbo.glsl");
           if (!File.Exists(Path.Combine(shadersDir, "Common", "RingShadows.glsl"))
               || !File.Exists(uboPath) || !ReadLf(uboPath).Contains("ringTextureId"))
           {
               Log("cloud ring shadows: this game build has none to add to");
               return;
           }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L714-L746).

2. **Insert a relative include in the patch text**, inside the block you add before `main()`:

   ```glsl
   // Payloads.cs L3147-L3151 (GLSL inside a C# raw string)
   // ---- Real Atmospheres: the rings' shadow on clouds ----
   // Same ring texture and transmittance as Atmosphere.comp. A compute shader has no derivatives,
   // so the mip comes from how far the ring coordinate moves across one pixel, traced at two
   // neighbouring points.
   #include "../Common/RingShadows.glsl"
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3136-L3160). The path is relative to `Clouds/2DCloud.comp` and `Clouds/RaymarchCloud.comp`; for a file in another folder adjust the number of `../`.

3. **Call the stock helpers.** `RingShadows.glsl` provides `TraceToRingPlane` and `GetRingShadowTransmittance` ([current/Content/Core/Shaders/Common/RingShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/RingShadows.glsl#L22-L70)); the full use, including choosing a mip without derivatives, is in [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../../stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md) article 4.

4. **Only include what the stock file does not already include.** GLSL has include guards only if the header has them; including a header twice can redeclare symbols. The mod skips the patch when the shader already mentions `RingShadow` or `useRingShadows`, which also avoids applying the shadow twice if a later game build adds its own.

## Complete example

| File | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | The inserted block with the relative include |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchCloudRingShadows` and the shadow-tree builder |

## Variations and gotchas

- **Only works in a copied tree.** For a shader that lives in the mod's own folder, use the generated absolute-path header.
- **In-memory patches.** If you compile patched source with `FromString` instead ([Patch vehicle paint and canopy shaders in memory at compile time](../../stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md)), pass the original path as the debug name (NUL-terminated); that is what includes resolve against.
- **Recheck on updates.** The helper's name and the data it reads can change between builds; the mod's existence checks turn that into "no ring shadows" rather than a compile failure.

## See also

- Siblings: [KSArmory generated header](include-the-game-shader-library-in-mod-glsl-1.md), [KSAStructures variant](include-the-game-shader-library-in-mod-glsl-2.md), [overview](overview.md)
- [Cloud pass shading and ring shadows](../../../../mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows.md)
- [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../../stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md), [Compile and write GLSL at runtime for a mod's own pipeline](../compile-glsl-at-runtime/overview.md)
