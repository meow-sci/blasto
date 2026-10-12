---
type: How-To
title: "Patch shaders in memory: a MeshIndirect.frag patch for vehicle paint, recompiled on a requested rebuild"
description: Prefix ShaderModuleUtils.FromFile to compile a transformed copy of the part fragment shader, then set Program.RendererRebuildNeeded so the game recompiles every part pipeline
category: shaders
group: stock-shaders
topic: patch-game-shaders-in-memory-at-compile-time
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/humble-arteest-vehicle-paint-shader-patch]
techniques: [harmony-prefix, harmony-postfix, glsl-shader, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - reflection KSA.PartTreeRenderData.Batch.StateBitFlags
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:prepare, frame:render]
game_types: [RenderCore.ShaderModuleUtils, KSA.ShaderReference, KSA.PartTreeRenderData, KSA.Program]
tags:
  - glsl
  - shader-patching
  - in-memory
  - fromstring
  - vehicle-paint
  - state-flags
  - renderer-rebuild
  - unscience
---
Vehicle Paint (the humble-arteest sub-mod of unscience) recolours parts by compiling a modified copy of the part fragment shader in memory. Nothing is written to
disk. This article is the recipe for patching a stock shader that way: choose the compile call to intercept, find an anchor line in the source, inject GLSL after it,
compile with `FromString` while keeping the caller's options and path, and request a renderer rebuild so the game compiles again through your prefix. You need StarMap
and Harmony; the colour transport (spare bits of the instance state word) is the subject of [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md).

## Background: how the game does it

KSA builds the part pipelines in `PartModelRenderer.BuildPipelineModel` / `BuildPipelineDynamic` (and the ray-tracing raster pipeline), each of which calls
`ShaderReference.CompileVariantWithCustomOptions`, which calls the four-argument `ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)` with per-variant
macro defines ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L120-L137)). The two-argument overload forwards to the four-argument one, so one prefix covers both. The patch point in
the stock shader is `vec3 sampledColor = ...;` at [current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L108), and `inStateFlags` is the `flat in uint` at L31 whose bits
0 to 10 the game uses.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Patches installed | `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Enable, blend change | ImGui panel, which sets `Program.RendererRebuildNeeded = true` | `frame:draw-ui` |
| Rebuild consumed, prefix runs | First of `Program.PrepareFrame` (L2143), `Render` (L2445, skips this frame's recording) or `PostRender` (L2351) | `frame:prepare` / `frame:render` |

`RebuildRenderer` waits for the device to go idle before destroying pipelines, so no frame in flight uses a module being replaced. Colour edits do not rebuild: they bump a
version and the cached state flags are rewritten on the next frame. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the exact overload.** Use the four-parameter signature, with `out VkShaderStageFlags` as `MakeByRefType()`:

   ```csharp
   // humble-arteest.lib/VehiclePaintPatches.cs L104-L111
       private static MethodBase? ResolveFromFile() =>
           AccessTools.Method(typeof(ShaderModuleUtils), nameof(ShaderModuleUtils.FromFile), new[]
           {
               typeof(Device),
               typeof(string),
               typeof(VkShaderStageFlags).MakeByRefType(),
               typeof(CompileOptions?),
           });
   ```

   Source: [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L104-L111). Apply it with `harmony.Patch(original, prefix: new HarmonyMethod(patch))` and keep a record to unpatch later.

2. **Write the prefix: transform, compile, skip the original.** Return `true` (stock) for every file you do not own and on any failure; on success set `__result` and the stage and return
   `false`. Pass the caller's `CompileOptions` through so every variant keeps its defines, and the original path as the debug name, NUL-terminated, so relative `#include`s resolve
   as stock:

   ```csharp
   // humble-arteest.lib/VehiclePaintPatches.cs L136-L165
       private static bool FromFilePrefix(Device device, string filePath, ref VkShaderStageFlags shaderStage,
           CompileOptions? options, ref VkShaderModule __result)
       {
           byte[]? source;
           try
           {
               source = VehiclePaintShaders.TryGetPatchedSource(filePath);
           }
           catch (Exception ex)
           {
               VehiclePaintShaders.NoteCompileFailed(filePath, ex);
               return true;
           }

           if (source == null) return true;

           try
           {
               var stage = ShaderModuleUtils.ShaderStageFromFileExtension(filePath);
               __result = ShaderModuleUtils.FromString(device, source, stage, options, NullTerminated(filePath));
               shaderStage = stage;
               VehiclePaintShaders.NoteCompiled();
               return false;
           }
           catch (Exception ex)
           {
               VehiclePaintShaders.NoteCompileFailed(filePath, ex);
               return true;
           }
       }
   ```

   Source: [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L136-L165). `FromString` takes the debug name as `ReadOnlySpan<byte>` and uses it as the shader file name, which is why it must
   be NUL-terminated ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L82)).

3. **Match which shaders to touch, by file name.** `IsTarget` returns false unless the patch is installed and the file name is `MeshIndirect.frag` or `MeshIndirectRaytraced.frag`
   ([humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs#L113-L123)). Matching the file name, not the full path, keeps it working wherever the game keeps the Core tree.

4. **Anchor on structure, not on an exact line.** Find the first line that starts with `vec3 sampledColor` and ends with `;`, require the `inStateFlags` declaration, then insert the snippet
   after the anchor; if either is missing record a precise error and fall back to stock:

   ```csharp
   // humble-arteest.lib/VehiclePaintShaders.cs L187-L195
           int anchor = -1;
           for (int i = 0; i < lines.Length; i++)
           {
               var trimmed = lines[i].Trim();
               if (!trimmed.StartsWith("vec3 sampledColor", StringComparison.Ordinal)) continue;
               if (!trimmed.EndsWith(";", StringComparison.Ordinal)) continue;
               anchor = i;
               break;
           }
   ```

   Source: [humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs#L181-L215). Normalise CRLF to LF first. The injected block decodes a 7:7:7 colour from `inStateFlags >> 11`:

   ```csharp
   // humble-arteest.lib/VehiclePaintShaders.cs L220-L245
           var apply = VehiclePaint.BlendMode switch
           {
               PaintBlendMode.Tint =>
                   "            sampledColor = hbPaintColor * (dot(sampledColor, vec3(0.2126, 0.7152, 0.0722)) * 2.0);",
               PaintBlendMode.Replace =>
                   "            sampledColor = hbPaintColor;",
               _ =>
                   "            sampledColor *= hbPaintColor;",
           };

           return
               "\n" +
               "    // --- humble-arteest: per-instance paint, packed into state-flag bits " +
               VehiclePaint.PaintBitShift + "..31 ---\n" +
               "    {\n" +
               "        uint hbPaintPacked = inStateFlags >> " + VehiclePaint.PaintBitShift + "u;\n" +
               "        if (hbPaintPacked != 0u)\n" +
               "        {\n" +
               "            vec3 hbPaintColor = gammaToLinear(vec3(\n" +
               "                float((hbPaintPacked >> " + (VehiclePaint.ChannelBits * 2) + "u) & " + HexMask + "u),\n" +
               "                float((hbPaintPacked >> " + VehiclePaint.ChannelBits + "u) & " + HexMask + "u),\n" +
               "                float( hbPaintPacked        & " + HexMask + "u)) * (1.0 / " +
               VehiclePaint.ChannelMax + ".0));\n" +
               apply + "\n" +
               "        }\n" +
               "    }\n";
   ```

   Source: [humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs#L220-L245). The blend mode is baked into the snippet, so changing it clears the source cache and requests another rebuild.

5. **Request the rebuild when you arm, disarm or retune.** `Install` pre-flights that the shader can still be patched (so a changed shader fails at enable, not silently), sets `Installed`, and
   asks for a rebuild; `Uninstall` and a blend-mode change do the same:

   ```csharp
   // humble-arteest.lib/VehiclePaintShaders.cs L96-L101, L108
       internal static void OnBlendModeChanged()
       {
           SourceCache.Clear();
           if (Installed) RequestRendererRebuild();
       }
   // ...
       private static void RequestRendererRebuild() => Program.RendererRebuildNeeded = true;
   ```

   Source: [humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs#L96-L108). Wire it to the setting change, never to a per-frame path: each rebuild recompiles every pipeline and costs a dropped frame.

6. **Write the data the shader reads.** The shader needs the colour bits in `inStateFlags`; the mod ORs them into the cached per-slot flags with postfixes on the private
   `PartTreeRenderData.WriteState` writers and invalidates the cache once per change. That half is described in
   [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md).

## Complete example

| File | Role |
|---|---|
| [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs) | Apply/remove, `FromFile` prefix, state postfixes, `EnsureBuilt` prefix |
| [humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs) | Install/uninstall, target detection, anchor injection, GLSL snippet |
| [humble-arteest.lib/VehiclePaint.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaint.cs) | Registries, `EncodeBits`, `RenderStateVersion` |

## Variations and gotchas

- **One skipping prefix per seam.** A `FromFile` prefix that returns `false` replaces compilation for everyone. Two such mods cannot compose unless each returns `true` for files it does not own
  (this one and the canopy patch do); gatOS refuses to arm when it finds another prefix owner ([Patch vehicle paint and canopy shaders in memory at compile time](overview.md) article 3).
- **Anchors and free bits move with the game.** Re-check on every update that bits 11..31 of `inStateFlags` are unused and that the `sampledColor` line still exists
  (it moved from L114 to L108 between builds 5482 and 5554).
- **The path tracer is not patched,** so painted parts look unpainted there; ray-traced IVA raster instances still copy the same flag word.
- **Failure falls back.** Any exception logs `humble-arteest: Patched <file> failed to compile: ...`, drops the cache entry and compiles the stock file.
- **Cost.** Enable and blend-mode changes rebuild the whole renderer and drop one frame; colour edits are free.

## See also

- Siblings: [canopy projection](patch-game-shaders-in-memory-at-compile-time-2.md), [gatOS transform](patch-game-shaders-in-memory-at-compile-time-3.md), [overview](overview.md)
- [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md)
- [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)
