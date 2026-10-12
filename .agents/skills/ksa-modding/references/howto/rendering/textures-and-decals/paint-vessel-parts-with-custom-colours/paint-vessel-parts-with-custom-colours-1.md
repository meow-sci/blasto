---
type: How-To
title: 7:7:7 colour in the unused StateBitFlag high bits plus a patched MeshIndirect.frag
description: Pack a part colour into bits 11 to 31 of the per-instance state flags and compile a copy of the part fragment shader with a decode snippet, with Multiply, Tint and Replace modes
category: rendering
group: textures-and-decals
topic: paint-vessel-parts-with-custom-colours
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/humble-arteest-vehicle-paint-shader-patch
  - mods/unscience/features/humble-arteest-kitten-color
techniques: [glsl-shader, harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - harmony-postfix KSA.PartTreeRenderData.WriteDynamicState
  - harmony-prefix KSA.PartTreeRenderData.EnsureBuilt
  - starmap StarMapAfterGui
  - starmap StarMapAllModsLoaded
lifecycle: [startup:all-mods-loaded, frame:render, frame:draw-ui]
game_types: [KSA.PartTreeRenderData, KSA.ShaderReference, RenderCore.ShaderModuleUtils, KSA.PartModelRenderer]
tags: [paint, colour, shader-patch, statebitflag, mesh-indirect, kitten]
---

Outcome: recolour individual part instances, every instance of a part type, or a whole vessel, in flight and in the vehicle editor, with three blend modes (Multiply, Tint, Replace), while glass stays clear and engine heat glow, battery lights, thin film and wetness keep working. Nothing on disk is modified. Kitten fur, body, eyes and helmet are tinted by a different, simpler mechanism: writing a colour into their material records on the GPU.
Demonstrated by unscience's Humble Arteest ([Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md), [Kitten tinting by staged writes into the GPU material buffer](../../../../mods/unscience/features/humble-arteest-kitten-color.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

`PerInstanceData` is 80 bytes in every part-model variant and the std430 stride is full, so a new field is impossible; but `StateBitFlag` is an `int` of which the game uses only bits 0 to 10 (`MeshIndirect.frag` and its raytraced variant test bits 0 to 10). Since part render data is cached per tree in `PartTreeRenderData` batches, a part's flags are written only by private `WriteState(Batch, int, Part)` (static models) and
`WriteDynamicState(DynamicBatch, int, PartModelDynamicModule)` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1088) when `EnsureBuilt(PartTree, ulong)` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L444) rewrites dirty slots; `InvalidateStates()` [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L323) forces a rewrite. Part shaders are compiled straight from disk on every pipeline build: `ShaderReference.CompileVariantWithCustomOptions` [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L105) calls
`ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)`, with per-variant macro defines. Kitten and glTF materials are 80-byte `MaterialData` records in `GpuMaterialSystem.BigBuffer`, indexed by the material's handle.

## Lifecycle and timing

| Step | Hook and moment | Lifecycle |
|---|---|---|
| Patches installed | `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Enable, blend change, colour edits | ImGui panel (`[StarMapAfterGui]`); Enable sets `Program.RendererRebuildNeeded = true` | `frame:draw-ui` |
| Shader compile (`FromFile` prefix) | the flag is consumed by the first of `Program.Render` (skips this frame's recording and calls `RebuildRenderer`), `PostRender` or the next `PrepareFrame` | `frame:render` / `frame:prepare` |
| `EnsureBuilt` prefix, `WriteState` and `WriteDynamicState` postfixes | `Program.Render` -> `UpdateRenderingResources` -> `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` | `frame:render`, not `frame:pre-render` |

Colour changes never rebuild anything: they bump a version, the next `EnsureBuilt` invalidates once, and the next frame shows the new colour. All of it is main-thread render data; nothing touches physics. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Encode the colour** as 7 bits per channel (21 bits) shifted to bit 11, never encoding zero (zero means "unpainted" to the shader):

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

2. **Write the bits.** The batch types are private nested classes, so resolve them with `AccessTools.Inner`, check that the `StateBitFlags` field is `int[]`, build a `FieldRefAccess`, and add postfixes (batch parameter bound as `object`) that OR the part's paint bits into `StateBitFlags[slot]` for both `WriteState` and `WriteDynamicState`. Leave `WriteGlassState` alone. Resolution order: explicit per-`Part` colour, then per-template colour keyed by `Part.Id`, then the global colour.

   ```csharp
   // humble-arteest.lib/VehiclePaintPatches.cs L179-L210
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

   // ---- (3) Cache invalidation ----

   /// <summary>
   /// Rewrites a tree's cached states once after any paint change. A tree seen for the first time
   /// is invalidated too, because its cache may predate the patches or the latest change.
   /// </summary>
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

3. **Invalidate once per change.** A prefix on public `EnsureBuilt` compares a render-state version counter with a per-tree `ConditionalWeakTable` entry and calls `InvalidateStates()` when it changed, so the postfixes run again.

4. **Patch the shader at compile time.** A prefix on the four-argument `ShaderModuleUtils.FromFile` returns `true` (stock) unless the file is `MeshIndirect.frag` or `MeshIndirectRaytraced.frag`; otherwise it reads the on-disk source, inserts a decode snippet after the line starting `vec3 sampledColor`, compiles with `ShaderModuleUtils.FromString(device, source, stage, options, path + "\0")` (the caller's options, the original NUL-terminated path so relative `#include` resolves), sets `__result` and skips the original. On any failure log, drop the cache entry and fall back to the stock compile.

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

5. **The GLSL inserted** decodes the bits and applies Multiply, Tint or Replace to `sampledColor`, so paint flows through thin film, frost and the whole PBR evaluation:

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

6. **Request a rebuild** (`Program.RendererRebuildNeeded = true`) when enabling or changing the blend mode, because the mode is baked into the snippet and `RebuildRenderer` waits for the device before destroying pipelines.

7. **Kittens: write the material.** Discover the material system by reflection (`Program.MaterialSystem`, the protected `AssetMap`, the public `BigBuffer`, the protected `DeviceCtx`), then stage a `float4` at `handle * sizeof(MaterialData) + offsetof(AlbedoColor)`:

   ```csharp
   // humble-arteest.lib/KittenColor.cs L56-L74
   var programType = typeof(Part).Assembly.GetType("KSA.Program");
   if (programType == null) { _lastError = "KSA.Program type not found."; return false; }

   var instanceProp = programType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
   if (instanceProp == null) { _lastError = "Program.Instance not found."; return false; }

   var programInstance = instanceProp.GetValue(null);
   if (programInstance == null) { _lastError = "Program.Instance is null (game not fully loaded?)."; return false; }

   _materialSystem = GetFieldOrProp(programType, programInstance, "MaterialSystem");
   if (_materialSystem == null) { _lastError = "MaterialSystem not found on Program."; return false; }

   _assetMap = FindFieldInHierarchy(_materialSystem, "AssetMap") as IDictionary;
   if (_assetMap == null) { _lastError = "AssetMap not found in MaterialSystem hierarchy."; return false; }

   _bigBufferProp = _materialSystem.GetType().GetProperty("BigBuffer",
       BindingFlags.Public | BindingFlags.Instance);

   _deviceCtxField = FindFieldInfoInHierarchy(_materialSystem.GetType(), "DeviceCtx");
   ```

   ```csharp
   // humble-arteest.lib/KittenColor.cs L204-L222
   try
   {
       var bigBuffer = (BufferEx)_bigBufferProp.GetValue(_materialSystem)!;
       var deviceCtx = (IVulkanContext)_deviceCtxField.GetValue(_materialSystem)!;

       int albedoColorOffset = (int)Marshal.OffsetOf<MaterialData>(nameof(MaterialData.AlbedoColor));
       ByteSize targetOffset = handle * ByteSize.Of<MaterialData>() + albedoColorOffset;

       using var stagingPool = deviceCtx.Device.CreateStagingPool(deviceCtx.MainQueue, 1);
       var commandBuffer = stagingPool.NextCommandBuffer();

       float4 colorCopy = color;
       var span = new Span<float4>(ref colorCopy);

       commandBuffer.Begin();
       VkUtils.StageAndUploadToBuffer(stagingPool, bigBuffer.VkBuffer, targetOffset, MemoryMarshal.AsBytes(span), commandBuffer);
       commandBuffer.End();

       return true;
   ```

   Record the original colour and the exact asset object before the first write; restore only handles whose entry is still that same object, because renderer-owned materials can be disposed and their slot reused.

## Complete example

| File | Role |
|---|---|
| [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs) | Shader prefix, state postfixes, invalidation |
| [humble-arteest.lib/VehiclePaint.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaint.cs) | Encoding, resolution order |
| [humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs) | GLSL snippets per blend mode |
| [humble-arteest.lib/KittenColor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.cs) | Kitten material colour |

## Variations and gotchas

- **Free-bit check:** bits 11 to 31 are unused in this build; re-check on every game update (the C# writers and the shader readers touch only bits below 11).
- **Precision:** 128 steps per channel in sRGB; Multiply can only darken, Tint scales by luminance x 2, Replace ignores the albedo texture.
- **Cost:** Enable and blend-mode changes rebuild the whole renderer and drop one frame; colour edits are free. The path tracer is not patched.
- **A skipping `FromFile` prefix replaces compilation for everyone;** two such prefixes cannot compose.
- **Kitten:** "Apply to All" writes every entry in `AssetMap`, including glTF model materials and other mods' runtime materials; never persist a handle, persist the name. Glass and eyes use a translucent shader with hard-coded opacity, so a tint barely shows there.

## See also

- [Overview](overview.md)
- [Approach 2: shader transform with packed colours and runtime masters](paint-vessel-parts-with-custom-colours-2.md)
- [Approach 3: ADD_EMISSIVE_COLOR in a postfix on WriteState](paint-vessel-parts-with-custom-colours-3.md)
- [Approach 4: prefix on PartModelDynamic.AddInstance](paint-vessel-parts-with-custom-colours-4.md)
- [Per-part vehicle paint through free state-flag bits and an in-memory part shader patch](../../../../mods/unscience/features/humble-arteest-vehicle-paint-shader-patch.md), [Kitten tinting by staged writes into the GPU material buffer](../../../../mods/unscience/features/humble-arteest-kitten-color.md)
- [Project decals onto vessels, terrain and clutter](../project-decals-onto-scene-geometry/overview.md)
