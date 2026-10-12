---
type: How-To
title: "Smuggle data into stock shaders: spare high bits of the part state flags"
description: Pack a 21-bit value into bits 11 to 31 of PartTreeRenderData's per-instance state word with postfixes on the private writers, and invalidate the cache so the new bits are rewritten
category: shaders
group: stock-shaders
topic: send-extra-data-to-stock-shaders-without-new-uniforms
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/humble-arteest-vehicle-paint-shader-patch
  - mods/gatos/features/vehicle-and-eva-paint
techniques: [harmony-postfix, harmony-prefix, reflection, glsl-shader]
hooks:
  - harmony-postfix KSA.PartTreeRenderData.WriteState (private)
  - harmony-postfix KSA.PartTreeRenderData.WriteDynamicState (private)
  - harmony-prefix KSA.PartTreeRenderData.EnsureBuilt
  - reflection KSA.PartTreeRenderData.Batch.StateBitFlags
lifecycle: [startup:all-mods-loaded, frame:render, frame:prepare]
game_types: [KSA.PartTreeRenderData, KSA.Program]
tags:
  - state-flags
  - bit-packing
  - side-channel
  - part-render-data
  - vehicle-paint
  - private-types
  - unscience
  - gatos
---
Part fragment shaders already receive a per-instance 32-bit word, `inStateFlags`, of which the game uses only the low 11 bits. Vehicle Paint (unscience) and gatOS both put a 7:7:7 colour in bits 11 to 31 and decode it in a patched shader. This article is
the data half of that technique: choose the carrier, encode, write it from postfixes on the game's private writers, and make the game rewrite its cache when the value changes. The shader half (injecting the decode) is
[Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md) article 1.

## Background: how the game does it

- **A new field is impossible.** `PerInstanceData` is 80 bytes in every part-model variant and the std430 stride is full, so a new field would change the stride; `StateBitFlag` (offset 64) is an `int`.
- **Used bits.** The private `PartTreeRenderData.StateBit` constants run `HIGHLIGHTED = 1` to `HIGHLIGHT_GREEN = 1024`, bit 10 ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L196-L219)). The stock fragment shader tests bits 0 to 10
  ([current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L306-L347)) and forwards the whole word from the vertex shader. Bits 11 to 31 are free. Re-check this on each game update.
- **Cache and writers.** Since build 5482 the per-part flags are cached per tree slot in private `PartTreeRenderData` batches and written only by the private instance methods `WriteState(Batch, int, Part)` ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1210))
  and `WriteDynamicState(DynamicBatch, int, PartModelDynamicModule)` ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1088)). The cache is rewritten only when dirty; the public `InvalidateStates()` marks every state dirty and the next
  `EnsureBuilt` (once per tree per frame, from `PartTree.UpdateRenderData`) runs the writers again.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Patches installed | `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Paint edited in the UI | Version counter bumped | `frame:draw-ui` |
| `EnsureBuilt` prefix, `WriteState` / `WriteDynamicState` postfixes | `Program.Render` -> `UpdateRenderingResources` -> `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData`, after `OnPreRender` and before `RenderGame` records | `frame:render` |
| Bits reach the GPU | `Compose` copies the cached flags into `PerInstanceData`; `PartModelRenderer.UpdateRenderData` uploads | `frame:render` |

All of it is main-thread render data; nothing touches physics. A postfix here is `frame:render`, not `frame:pre-render` (see the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)).

## Steps

1. **Encode into the free bits, never to zero.** Quantize sRGB to 7 bits per channel (the shader converts to linear, so sRGB steps are perceptually even), pack, shift by 11, and map zero to one because zero means "unpainted":

   ```csharp
   // humble-arteest.lib/VehiclePaint.cs L259-L267
       public static int EncodeBits(float3 srgb)
       {
           uint r = Quantize(srgb.X);
           uint g = Quantize(srgb.Y);
           uint b = Quantize(srgb.Z);
           uint packed = (r << (ChannelBits * 2)) | (g << ChannelBits) | b;
           if (packed == 0u) packed = 1u;
           return unchecked((int)(packed << PaintBitShift));
       }
   ```

   Source: [humble-arteest.lib/VehiclePaint.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaint.cs#L259-L267). gatOS uses the identical 7:7:7 layout and says so in its codec ([gatOS.Paint/PaintBits.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintBits.cs#L1-L22)), which lets the two mods interoperate.

2. **Reach the private batch types by name.** `AccessTools.Inner` finds the private nested `Batch` / `DynamicBatch`; check that `StateBitFlags` is still `int[]`, build a `FieldRefAccess` once, and resolve the writer by its exact parameter tuple. If anything is missing, return `null` and
   skip that patch with a log line instead of throwing:

   ```csharp
   // humble-arteest.lib/VehiclePaintPatches.cs L117-L125
       private static MethodBase? ResolveWriteState(string batchTypeName, string writerName, Type ownerType,
           out AccessTools.FieldRef<object, int[]>? stateFlags)
       {
           stateFlags = null;
           Type? batchType = AccessTools.Inner(typeof(PartTreeRenderData), batchTypeName);
           if (batchType == null || AccessTools.Field(batchType, "StateBitFlags")?.FieldType != typeof(int[])) return null;
           stateFlags = AccessTools.FieldRefAccess<int[]>(batchType, "StateBitFlags");
           return AccessTools.Method(typeof(PartTreeRenderData), writerName, new[] { batchType, typeof(int), ownerType });
       }
   ```

   Source: [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L117-L125).

3. **OR the bits in from postfixes.** The batch parameter is private so it binds as `object`. `WriteGlassState` is deliberately left alone, so glass stays clear:

   ```csharp
   // humble-arteest.lib/VehiclePaintPatches.cs L179-L189
       private static void WriteStatePostfix(object inBatch, int inSlot, Part inPart)
       {
           if (VehiclePaint.TryGetPaintBits(inPart, out int bits))
               _staticStateFlags!(inBatch)[inSlot] |= bits;
       }

       private static void WriteDynamicStatePostfix(object inBatch, int inSlot, PartModelDynamicModule inModule)
       {
           if (VehiclePaint.TryGetPaintBits(inModule.Parent, out int bits))
               _dynamicStateFlags!(inBatch)[inSlot] |= bits;
       }
   ```

   Source: [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L179-L189). Resolution order in `TryGetPaintBits`: explicit per-`Part` colour (reference-keyed dictionary), then per-template colour keyed by `Part.Id`, then the global colour. The dynamic postfix paints `inModule.Parent`, so all models of one part share its paint.

4. **Invalidate once per change.** Keep a version counter that every mutation bumps; a prefix on `EnsureBuilt` compares it with the last seen version per tree and calls `InvalidateStates()` once, so the writers run again:

   ```csharp
   // humble-arteest.lib/VehiclePaintPatches.cs L197-L210
       private static void EnsureBuiltPrefix(PartTreeRenderData __instance)
       {
           int version = VehiclePaint.RenderStateVersion;
           if (SeenVersions.TryGetValue(__instance, out var seen))
           {
               if (seen.Value == version) return;
               seen.Value = version;
           }
           else
           {
               SeenVersions.Add(__instance, new StrongBox<int>(version));
           }
           __instance.InvalidateStates();
       }
   ```

   Source: [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L197-L210). A tree seen for the first time is invalidated too, because its cache may predate the patches. gatOS does the same from outside the patch by calling the public `InvalidateStates()` on every loaded vehicle's tree whenever its rule set changes.

5. **Decode in the patched shader:** `uint packed = inStateFlags >> 11u; if (packed != 0u) { ... }`; see the snippet in [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md) article 1 for the full GLSL.

## Complete example

| File | Role |
|---|---|
| [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs) | Resolution, postfixes, cache invalidation |
| [humble-arteest.lib/VehiclePaint.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaint.cs) | Registries, `RenderStateVersion`, `TryGetPaintBits`, `EncodeBits` |
| [gatOS.Paint/PaintBits.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Paint/PaintBits.cs) | The compatible codec in gatOS |

## Variations and gotchas

- **The invariant to re-check on every update:** C# writers set bits 0x1, 0x2, 0x4, 0x8, 0x100, 0x200, 0x400 plus 0x40/0x80, the glass writer sets 0x1 to 0x8, `Compose*` ORs 0x10/0x20, and the shaders test bits 0 to 10; the path tracer's `raygen.rgen` tests bit 6 only.
- **Precision and range.** 128 steps per channel in sRGB; Multiply can only darken, Tint scales by luminance x 2 so it can brighten, Replace ignores the albedo texture.
- **Seams move.** Before 5482 the hooks were prefixes on `PartModelModule.UpdateRenderData` and `PartModel.AddInstance`; those stopped being reached by static raster parts. Private writers cannot be checked by the compiler, so guard every lookup.
- **Two mods packing the same bits** collide; gatOS refuses to arm when standalone humble-arteest is installed.
- **Persistence.** The unscience save participant stores activation, blend mode and global/template/per-part paint; the standalone host has no saves.

## See also

- Siblings: [temporary value swap](send-extra-data-to-stock-shaders-without-new-uniforms-1.md), [material marker](send-extra-data-to-stock-shaders-without-new-uniforms-3.md), [sentinel dispatch](send-extra-data-to-stock-shaders-without-new-uniforms-4.md), [overview](overview.md)
- [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md), [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md)
- [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
