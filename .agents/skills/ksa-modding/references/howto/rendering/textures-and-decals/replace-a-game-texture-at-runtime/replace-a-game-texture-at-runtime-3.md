---
type: How-To
title: Thread-static construction context scopes transpiler and prefix redirection
description: Give mod-built render objects a private material buffer by redirecting the game's own material lookups only while the mod constructs or rebuilds them, with a shader variant for source colours
category: rendering
group: textures-and-decals
topic: replace-a-game-texture-at-runtime
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/pebbles-private-clutter-materials]
techniques: [harmony-transpiler, harmony-prefix]
hooks:
  - harmony-transpiler KSA.ClutterEcotypeRenderData.SortMaterialIds
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateColorRenderer
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateDepthPrePassRenderer
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateShadowDepthRenderer
  - harmony-prefix KSA.ShaderReference.CompileVariantWithCustomOptions
  - harmony-prefix KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
lifecycle: [frame:dispatch-solvers]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - KSA.ClutterRenderer
  - KSA.Program
tags: [texture, material, clutter, transpiler, thread-static, private-buffer, shader-variant]
---

Outcome: new materials on one planet's ground clutter without altering stock content. Stock clutter shares one material table per renderer, sized at startup; a mod cannot add rows and must not overwrite rows other planets use. Pebbles gives each replaced body a private material buffer and index table, and redirects the game's own material lookups to it only while Pebbles
is constructing or rebuilding its own render objects. The same scope swaps the clutter colour fragment shader for a variant that shows imported textures in their real colours. This is the private-material-buffer sub-step. Demonstrated by unscience Pebbles ([Private clutter material buffer and source-color shader variant scoped to owned construction](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

`GroundClutterRenderer` [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L22) builds a deduplicated `GroundClutterGpuMaterial` buffer (`MaterialBuffer` [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L320), built by `BuildMaterialBuffer` [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L546)) and a hash-to-index map (`GetMaterialIndex`); every `ClutterEcotypeRenderData` binds that buffer into its pipelines when it creates its renderers.
Descriptors capture the buffer at pipeline creation, so drawing never needs any hook. The stock colour shader (`ClutterSolidFrag`) treats diffuse alpha as a linear/sRGB selector and multiplies by a terrain-sampled ground colour, which makes arbitrary imported artwork look terrain-tinted.

## Lifecycle and timing

| Moment | Hook | State |
|---|---|---|
| Commit in the `Universe.ExecuteNextClothSolvers` prefix | Context entered in `ClutterResources.Build`; the transpiled methods and the shader prefix fire during the native constructors | Main thread, solvers and GPU idle (`frame:dispatch-solvers`) |
| Graphics rebuild | prefix and finalizer on `ClutterEcotypeRenderData.RebuildFrameResources` | Inside `Program.RebuildRenderer` (see [handle renderer rebuilds](../../vulkan-passes/handle-renderer-rebuilds/overview.md)) |
| Every other time | transpiled calls see no context and behave exactly like stock | other mods and planets unaffected |

The context is `[ThreadStatic]`, so a context opened on the main thread never leaks to other threads. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Allocate a private device-local storage buffer** ("Pebbles materials") with one 24-byte material row per private material, fill a staging buffer, copy it, add a transfer-write to fragment-shader-read barrier and fill `MaterialIndices[material.Hash]`. For source-colour materials set two flag bits the stock shader does not use: bit 31 (use the source texture colour) and bit 30 (the diffuse format is already sRGB).

   ```csharp
   // pebbles.lib/Runtime/ClutterResources.cs L43-L64
   var length = Math.Max(Graph.Materials.Count, 1);
   MaterialBuffer = renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo { Name = "Pebbles materials", BufferSize = ByteSize.Of<GroundClutterRenderer.GroundClutterGpuMaterial>(length),
       BufferUsage = VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit });
   var staging = pool.AddStagingBuffer(ByteSize.Of<GroundClutterRenderer.GroundClutterGpuMaterial>(length));
   using (var memory = staging.Map())
   {
       var data = memory.AsSpan<GroundClutterRenderer.GroundClutterGpuMaterial>(); data.Clear();
       for (var i = 0; i < Graph.Materials.Count; i++)
       {
           var material = Graph.Materials[i]; MaterialIndices.Add(material.Hash, (uint)i);
           data[i] = material.ToGpuMaterial();
           if (Graph.SourceColorMaterials.Contains(material.Hash))
           {
               data[i].Flags |= 0x80000000u;
               if (material.DiffuseReference!.Get().Texture.Format.ToString().Contains("Srgb", StringComparison.OrdinalIgnoreCase)) data[i].Flags |= 0x40000000u;
           }
       }
   }
   command.CopyBuffer(staging.VkBuffer, MaterialBuffer.Value.VkBuffer, new VkBufferCopy { Size = ByteSize.Of<GroundClutterRenderer.GroundClutterGpuMaterial>(length) });
   var barrier = Utils.CreateBarrier(MaterialBuffer.Value.VkBuffer, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, VK.WHOLE_SIZE, ByteSize.Zero);
   command.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, null, new ReadOnlySpan<VkBufferMemoryBarrier>(in barrier), null);
   using var context = ClutterHooks.Enter(this);
   ```

2. **Open a scoped context** (`[ThreadStatic]`, restored on dispose) for the whole native construction loop, and **transpile the callers, not the getters** (a one-line expression-bodied property can be inlined by the JIT). The replacement returns the private buffer or index only when the context's owner is that renderer and falls through to the stock member otherwise. Throw unless exactly one call was replaced, which aborts the patch set on an incompatible build.

   ```csharp
   // pebbles.lib/Runtime/ClutterHooks.cs L23-L28
   [ThreadStatic] private static ClutterResources? _context;
   public static IDisposable Enter(ClutterResources resources)
   { var old = _context; _context = resources; return new Scope(() => _context = old); }
   private sealed class Scope(Action leave) : IDisposable { public void Dispose() => leave(); }
   public static void Track(ClutterResources resources) { foreach (var render in resources.Render) Owned.Add(render, resources); }
   public static void Forget(ClutterResources resources) { foreach (var render in resources.Render) Owned.Remove(render); }
   ```

   ```csharp
   // pebbles.lib/Runtime/ClutterHooks.cs L65-L82
   private static IEnumerable<CodeInstruction> MaterialCalls(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
   {
       var buffer = AccessTools.PropertyGetter(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.MaterialBuffer));
       var index = Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.GetMaterialIndex));
       var replaced = 0;
       foreach (var instruction in input)
       {
           if (instruction.Calls(buffer)) { instruction.opcode = OpCodes.Call; instruction.operand = Required(typeof(ClutterHooks), nameof(Buffer)); replaced++; }
           else if (instruction.Calls(index)) { instruction.opcode = OpCodes.Call; instruction.operand = Required(typeof(ClutterHooks), nameof(Index)); replaced++; }
           yield return instruction;
       }
       if (replaced != 1) throw new InvalidOperationException($"Pebbles expected one material binding in {__originalMethod.Name}, found {replaced}. Game build is incompatible.");
   }
   private static BufferEx Buffer(GroundClutterRenderer renderer)
       => ReferenceEquals(_context?.Owner, renderer) ? _context.MaterialBuffer!.Value : renderer.MaterialBuffer;
   private static uint Index(GroundClutterRenderer renderer, GroundClutterMaterialReference material)
       => ReferenceEquals(_context?.Owner, renderer) ? _context.MaterialIndices[material.Hash] : renderer.GetMaterialIndex(material);
   private static void BeforeConstruct(object __instance) => _context?.Constructed.Add(__instance);
   ```

3. **Substitute the shader variant** with a prefix on `ShaderReference.CompileVariantWithCustomOptions`: only when a context with source-colour materials is active and the shader id is `ClutterSolidFrag`; read the shader file from `ModPath`, require the marker line to occur exactly once, append one GLSL line that re-samples the diffuse texture for flagged materials, and compile with `ShaderModuleUtils.FromString`, passing the game's include callback and the original NUL-terminated path as the debug name so `#include` still resolves. Return `false` to skip the stock compile.

   ```csharp
   // pebbles.lib/Runtime/ClutterHooks.cs L93-L103
   private static bool CompileColor(ShaderReference __instance, CompileOptions __0, ref VkShaderModule? __result)
   {
       if (_context == null || _context.Graph.SourceColorMaterials.Count == 0 || __instance.Id != "ClutterSolidFrag") return true;
       var source = File.ReadAllText(__instance.ModPath);
       const string marker = "diffuseSample.rgb *= (groundColor / averageLuminosity);";
       if (source.Split(marker).Length != 2) throw new InvalidOperationException("Clutter color shader changed; source-color adaptation needs review.");
       source = source.Replace(marker, marker + "\n    if ((materialData.flags & 0x80000000u) != 0u) diffuseSample.rgb = pow(texture(sampler2D(globalTextures[materialData.diffuseTextureId], textureSampler), inUv).rgb, vec3((materialData.flags & 0x40000000u) != 0u ? 1.0 : 2.2));", StringComparison.Ordinal);
       ShaderCompilerResolve.SetIncludeCallback(__0);
       __result = ShaderModuleUtils.FromString(Program.GetRenderer().Device, Encoding.UTF8.GetBytes(source), VkShaderStageFlags.FragmentBit, __0, Encoding.UTF8.GetBytes(__instance.ModPath + "\0"));
       return false;
   }
   ```

4. **Track construction.** A prefix on every public constructor of the six native ownership types adds the instance to the context's list, so a build that throws halfway can free whatever was already allocated.

5. **Re-enter the scope on game rebuilds** (prefix and finalizer on `RebuildFrameResources`, see [handle renderer rebuilds](../../vulkan-passes/handle-renderer-rebuilds/overview.md)).

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs) | Scoped context, transpiler, shader prefix, rebuild hooks |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs) | Private buffer and indices |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L122-L137) | Fresh material references with computed hashes |

## Variations and gotchas

- **Exact-count transpiler:** each of the four methods must contain exactly one matching call. A game update that adds a second read makes the patch fail; the feature becomes unavailable rather than mutating the shared buffer.
- **Shader marker:** the patch keys on a literal line occurring exactly once in the stock shader; a reworded line throws "Clutter color shader changed".
- **Flag bits 30 and 31** assume the high bits of the stock flags stay free; the sRGB detection is a name heuristic.
- **Draw cost is unchanged:** no draw-time hook, the private buffer is bound through descriptors created inside the context.
- **Partial cleanup only:** the constructor capture cannot see allocations a native constructor holds in locals.

## See also

- [Overview](overview.md)
- [Approach 1: re-point the stock bindless slot](replace-a-game-texture-at-runtime-1.md)
- [Approach 2: PNG override and UV remap](replace-a-game-texture-at-runtime-2.md)
- [Private clutter material buffer and source-color shader variant scoped to owned construction](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)
- [Recreate mod render targets when KSA rebuilds its renderer](../../vulkan-passes/handle-renderer-rebuilds/overview.md), [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md)
