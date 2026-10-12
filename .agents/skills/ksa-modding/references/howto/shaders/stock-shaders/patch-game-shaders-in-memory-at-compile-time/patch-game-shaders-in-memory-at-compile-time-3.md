---
type: How-To
title: "Patch shaders in memory: a compile-time transform with packed colours, a conflict check and runtime masters"
description: Arm and disarm an in-memory shader transform at runtime with dynamic Harmony patches, refuse when another mod owns the compile seam, and force the recompile only on the setting change
category: shaders
group: stock-shaders
topic: patch-game-shaders-in-memory-at-compile-time
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/gatos/features/vehicle-and-eva-paint
  - mods/unscience/features/humble-arteest-vehicle-paint-shader-patch
techniques: [glsl-shader, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile (dynamic)
  - harmony-postfix KSA.PartTreeRenderData.WriteState (dynamic)
  - starmap StarMapBeforeGui
lifecycle: [frame:viewports, frame:prepare, frame:render]
game_types: [RenderCore.ShaderModuleUtils, KSA.ShaderReference, KSA.PartTreeRenderData, KSA.Program]
tags: [glsl, shader-patching, in-memory, dynamic-patch, conflict-check, renderer-rebuild, gatos, paint]
---
gatOS ships the same part-paint idea as Vehicle Paint but treats it as a runtime feature: nothing is patched until a master switch is turned on, turning it off removes every patch and
restores stock rendering, and arming refuses to proceed if another mod already owns the compile seam. This article is the lifecycle around the transform (arming, conflict check, rebuild,
disarming) rather than the transform itself, which is covered in article 1. It also answers the practical question "how do I make the game recompile after enabling or retuning a patch".

## Background: how the game does it

`Program.RendererRebuildNeeded` is a public static bool. `PrepareFrame` checks it first thing: if set, it calls `RebuildRenderer()` and clears it
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2148)). `Render` and `PostRender` also consume it (`PostRender` logs `Renderer rebuild requested by game code`). `RebuildRenderer` waits for the
device to go idle, rebuilds renderers and recompiles shader modules, which is where the patched `FromFile` prefix runs.
A `FromFile` prefix that returns `false` replaces compilation for every caller, so two mods that both skip the original cannot compose.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Paint commands (master on/off, rule changes) | Frame-phase drain in a main-viewport `GameViewport.OnFrame` prefix | `frame:viewports` |
| Per-frame `PaintManager.Tick` (state sync) | `StarMapBeforeGui`, or `StarMapAfterOnFrame` on hidden-HUD frames (effects then show one frame late) | `frame:draw-ui` / `frame:after-on-frame` |
| Rebuild consumed, shaders recompiled through the prefix | Top of the next `PrepareFrame` | `frame:prepare` |
| Cached part states rewritten, postfixes run | `Program.UpdateRenderingResources` -> `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` -> `EnsureBuilt` | `frame:render` |

The postfixes execute in `frame:render`, not `frame:pre-render`, because `UpdateRenderingResources` runs after `OnPreRender` and before `RenderGame` records
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4360)). See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The idle cost while disarmed is one branch.

## Steps

1. **Transform as a pure string function.** Keep the injection testable: no game types, just text in and text out. It finds the `vec3 sampledColor ...;` declaration (and requires `inStateFlags`), then inserts a
   block that unpacks a 7:7:7 sRGB colour from `inStateFlags >> 11`; zero means unpainted:

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

   Source: [gatOS.Paint/PaintShaderTransform.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintShaderTransform.cs#L41-L62). A marker comment makes the transform idempotent (`if (source.Contains(Marker)) return true;`).

2. **Compile the transformed source from the prefix.** Same contract as the other mods: return `true` for files that are not yours, call `FromString` with KSA's original options and the NUL-terminated path, return `false`:

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

   Source: [gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs#L73-L101).

3. **Arm on demand, with a conflict check and a preflight.** Do not patch at startup. When the master is switched on: refuse if any other Harmony owner has a prefix on `FromFile`, preflight that the shader can still be transformed, apply all
   patches transactionally, and set `Program.RendererRebuildNeeded = true`:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs L226-L244
           try
           {
               if (_cleanupPatches) RemovePatches();
               _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Arming, PartError = "" });
               var fromFile = PartPaintPatches.FromFileMethod ?? throw new MissingMethodException("ShaderModuleUtils.FromFile");
               var foreign = Harmony.GetPatchInfo(fromFile)?.Prefixes.FirstOrDefault(p => p.owner != HarmonyId);
               if (foreign is not null)
               {
                   var message = $"shader compiler prefix conflict with '{foreign.owner}'";
                   _store.PublishRuntime(s => s with { PartsStatus = PartPaintStatus.Conflict, PartError = message });
                   return new CommandResult(CommandOutcome.Busy, message);
               }

               PreflightShader("MeshIndirectFrag", required: true, out _);
               var ray = PreflightShader("MeshIndirectRaytracedFrag", required: false, out _);
               ApplyPatches();
               PartsArmed = true;
               _store.SetPartsMaster(true);
               Program.RendererRebuildNeeded = true;
   ```

   Source: [gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs#L226-L244). `ApplyPatches` rolls back on any failure (`catch { RemovePatches(); throw; }`) and then invalidates every loaded tree's cached state through
   the public `PartTreeRenderData.InvalidateStates()` so postfixes run again ([gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs#L280-L300)).

4. **Disarm symmetrically.** Switching the master off clears the source cache, removes every patch, sets `Program.RendererRebuildNeeded = true` and reports `Disabled` ([gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs#L213-L223)). Rule changes (the colours)
   do not rebuild: they invalidate states only. A blend-mode change does rebuild, because the mode is baked into the source (`_sources.Clear(); Program.RendererRebuildNeeded = true;`, [gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs#L89)).

5. **Wire the rebuild to the setting change, not to every frame.** Each rebuild recompiles every pipeline; the flag is picked up in `PrepareFrame` and the patched prefix then produces the new module.

## Complete example

| File | Role |
|---|---|
| [gatOS.Paint/PaintShaderTransform.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintShaderTransform.cs) | Pure GLSL injection |
| [gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PartPaintPatches.cs) | `FromFile` prefix and state-writer postfixes |
| [gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/PaintManager.cs) | Arming, conflict check, invalidation, rebuild requests |

## Variations and gotchas

- **Refuse when another mod owns `FromFile`.** Two skipping prefixes do not compose; gatOS also refuses to arm if standalone humble-arteest is installed.
- **Never write shader files on disk.** Transform in memory and keep KSA's compile options and path.
- **Re-verify the private writers on every update.** Build 5482 moved part rendering into the cached `PartTreeRenderData` and removed the per-module `UpdateRenderData` methods; the old interception compiled but painted nothing.
- **Glass uses a separate shader** and stays stock.
- **In-game validation of paint is listed as pending** in the repository.

## See also

- Siblings: [vehicle paint](patch-game-shaders-in-memory-at-compile-time-1.md), [canopy projection](patch-game-shaders-in-memory-at-compile-time-2.md), [overview](overview.md)
- [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md), [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md)
- [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
