---
type: Mod Feature
title: Private clutter material buffer and source-color shader variant scoped to owned construction
description: Pebbles redirects the game's ground clutter material buffer lookups to a private per-body buffer and swaps in a source-color ClutterSolidFrag variant, only while a thread-static owned-construction context is active
mod: unscience
submod: pebbles
category: rendering
techniques: [harmony-transpiler, harmony-prefix, harmony-finalizer, glsl-shader, vulkan-buffer, vulkan-pipeline]
hooks:
  - harmony-transpiler KSA.ClutterEcotypeRenderData.SortMaterialIds
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateColorRenderer
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateDepthPrePassRenderer
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateShadowDepthRenderer
  - harmony-prefix KSA.ShaderReference.CompileVariantWithCustomOptions
  - harmony-prefix KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-finalizer KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-prefix constructors of KSA.GroundClutterPlacementData, KSA.ClutterEcotypeRenderData, KSA.ClutterEcotypePhysicalData, KSA.ClutterCubeCellGrid, KSA.ClutterViewResources, RenderCore.Mesh.SimpleVkMeshAtlas
lifecycle: [frame:dispatch-solvers, event:renderer-rebuild]
game_types:
  - KSA.GroundClutterRenderer
  - KSA.GroundClutterRenderer.GroundClutterGpuMaterial
  - KSA.ClutterEcotypeRenderData
  - KSA.GroundClutterMaterialReference
  - KSA.ShaderReference
  - KSA.GroundClutterPlacementData
  - KSA.ClutterEcotypePhysicalData
  - KSA.ClutterCubeCellGrid
  - KSA.ClutterViewResources
  - RenderCore.Mesh.SimpleVkMeshAtlas
  - RenderCore.ShaderModuleUtils
  - KSA.TexturePowerReference
tags: [ground-clutter, materials, shader-patching, transpiler, scoped-context, vulkan, glsl, bindless]
---

Stock KSA ground clutter shares one material table per renderer: `GroundClutterRenderer` builds a
deduplicated `GroundClutterGpuMaterial` buffer and a hash-to-index map, and every
`ClutterEcotypeRenderData` binds that buffer into its pipelines. A mod that wants new materials on
one planet cannot simply add rows (the buffer is sized at startup) and must not overwrite stock
rows that other planets use.[^pebbles-source-map] Pebbles solves this by giving each replaced body a
private material buffer and index table, and by redirecting the game's own material lookups to it
only while Pebbles is constructing or rebuilding its own render objects. The same scope also swaps
the clutter color fragment shader for a variant that can show imported textures with their real
colors instead of terrain-tinted ones.[^clutter-hooks]

## How it works

**Private buffer.** Inside `ClutterResources.Build` Pebbles allocates a device-local storage buffer
named "Pebbles materials" with one 24-byte `GroundClutterGpuMaterial` per private material, fills a
staging buffer with `GroundClutterMaterialReference.ToGpuMaterial()` for each, records a copy and a
transfer-write to fragment-shader-read barrier, and fills `MaterialIndices[material.Hash]`. For
"source color" materials it sets two flag bits the stock shader does not use: bit 31 (use the
source texture color) and bit 30 (the diffuse texture format is already sRGB, so do not decode it a
second time).[^clutter-resources][^ksa-clutter-common-glsl] The materials themselves are fresh
`GroundClutterMaterialReference` objects with a `Pebbles/<guid>` id, a computed hash and
`ColorPipelineFlags`; the normal map is wrapped in a small `TexturePowerReference` subclass that
borrows the texture rather than registering a new one
([pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L122-L137)).

**Scoped redirection.** `ClutterHooks.Enter(resources)` sets a `[ThreadStatic]` `_context` and
returns a disposable scope. `Build` holds it for the whole native construction loop. A transpiler
on `SortMaterialIds`, `CreateColorRenderer`, `CreateDepthPrePassRenderer` and
`CreateShadowDepthRenderer` rewrites the single call in each method to the instance getter
`GroundClutterRenderer.MaterialBuffer` or the instance method `GetMaterialIndex` into a static call
with the same stack shape (the renderer instance becomes the first argument). The replacements
return the private buffer or private index only when `_context.Owner` is that renderer, and fall
through to the stock member otherwise, so stock ecotypes and other planets are unaffected. The
transpiler throws unless exactly one call was replaced, which aborts the whole `pebbles` patch set
on an incompatible build.

Why the callers and not the getter: the code comment says "Patch callers rather than tiny getters,
which the JIT may inline." `MaterialBuffer` is a one-line expression-bodied property.[^ksa-ground-clutter-renderer]

**Shader variant.** The stock clutter color shader (`ClutterSolidFrag`, `Solid.frag`) treats diffuse
alpha as a linear/sRGB selector and multiplies the color by a terrain-sampled ground color
(`diffuseSample.rgb *= (groundColor / averageLuminosity);`, Solid.frag L338).[^ksa-solid-frag] That
makes arbitrary imported artwork look terrain-tinted. A prefix on
`ShaderReference.CompileVariantWithCustomOptions` checks for an active context with at least one
source-color material and the shader id `ClutterSolidFrag`, reads the shader file from
`ShaderReference.ModPath`, requires the marker line to occur exactly once, appends one GLSL line that
re-samples the diffuse texture for flagged materials (gamma 2.2 decode unless bit 30 is set), and
compiles the result with `ShaderModuleUtils.FromString`, passing the game's include callback and
the original NUL-terminated path as the debug name so `#include` resolution still works. It returns
`false` to skip the stock compile. Depth, shadow and other variants stay native.

**Rebuilds.** Descriptors capture the buffer at pipeline creation, so drawing never needs the
context. But `ClutterEcotypeRenderData.RebuildFrameResources` (graphics settings change) recompiles
`ClutterSolidFrag` for each pipeline; a prefix looks the instance up in the `Owned` dictionary
(filled by `ClutterHooks.Track` on commit) and re-enters the context, and a finalizer always exits it
and rethrows the original exception.

**Construction capture.** A prefix on every public constructor of the six native ownership types
adds `__instance` to `_context.Constructed`. If a build throws halfway, `ClutterRetirement` uses
that list to free whatever was already allocated
([pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L25-L58)).[^clutter-retirement]

## Lifecycle and timing

| Moment | Hook | State |
|---|---|---|
| Commit in the `Universe.ExecuteNextClothSolvers` prefix (`Program.cs` L2217) | Context entered in `ClutterResources.Build`; transpiled methods and the shader prefix fire during the native constructors and `BuildRenderResources` | Main thread, solvers and GPU idle (see [ground clutter replacement](./pebbles-ground-clutter-replacement.md)) |
| Graphics rebuild (`Program.RebuildRenderer` -> `PlanetRenderer.RebuildFrameResources` -> `GroundClutterRenderer.RebuildFrameResources`) | Prefix/finalizer on `ClutterEcotypeRenderData.RebuildFrameResources` | Main thread, inside the renderer rebuild |
| Every other time | Transpiled calls see `_context == null` and behave exactly like stock | Stock renderer and other mods unaffected |

`[ThreadStatic]` matters because `SortMaterialIds` and pipeline creation can in principle be reached
from game code on other threads; a context opened on the main thread never leaks to them. The
`Owned` map is keyed by the exact `ClutterEcotypeRenderData` instance, so the stock render data of
the same body (retained for restore) is rebuilt with stock bindings.[^clutter-controller]

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs) | Context, transpiler, redirect helpers, shader prefix, rebuild scope, constructor capture |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L39-L77) | Private material buffer upload and context scope around native construction |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L122-L137) | Private `GroundClutterMaterialReference` construction |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs) | Consumer of the constructor capture list |

## Snippets

The scoped context and the call-site transpiler with its stock fall-through:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L23-L82
    [ThreadStatic] private static ClutterResources? _context;
    public static IDisposable Enter(ClutterResources resources)
    { var old = _context; _context = resources; return new Scope(() => _context = old); }
    private sealed class Scope(Action leave) : IDisposable { public void Dispose() => leave(); }
// ...
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

The source-color shader substitution:

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

Material rows with the two private flag bits:

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

The stock line the marker anchors on (build 5554):

```glsl
// current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag L330-L338
    vec4 diffuseSample = texture(sampler2D(globalTextures[materialData.diffuseTextureId], textureSampler), inUv);
    vec3 diffuseSrgb = pow(diffuseSample.rgb, vec3(2.2));
    diffuseSample.rgb = mix(diffuseSrgb, diffuseSample.rgb, diffuseSample.a);
    diffuseSample.rgb = diffuseSample.rgb * 2 - 1;
    diffuseSample.rgb += 1;
    
    float averageLuminosity = uboPlanet.meanDiffuseLuminosity;
    vec3 groundColor = mix(vec3(averageLuminosity), instanceColor, diffuseSample.a);
    diffuseSample.rgb *= (groundColor / averageLuminosity);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GroundClutterRenderer.MaterialBuffer`, `GetMaterialIndex(GroundClutterMaterialReference)` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L320) | Redirected calls (getter L320, method L591) |
| `GroundClutterRenderer.GroundClutterGpuMaterial` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L147-L161) | 24-byte row layout of the private buffer |
| `ClutterEcotypeRenderData.SortMaterialIds`, `CreateColorRenderer`, `CreateDepthPrePassRenderer`, `CreateShadowDepthRenderer` | [current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs#L310-L561) | One call each at L335, L459, L508, L561 |
| `ClutterEcotypeRenderData.RebuildFrameResources(DeviceEx)` | [current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs#L932-L960) | Recompiles `ClutterSolidFrag` per pipeline flag set |
| `ShaderReference.CompileVariantWithCustomOptions(CompileOptions)` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L120-L138) | Prefix target; stock compiles `ModPath` with an include callback |
| `Utils.SetShaderFromMod(..., useCustomOptions: true)` | [current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L636-L650) | The path clutter pipelines use to reach `CompileVariantWithCustomOptions` |
| `GroundClutterMaterialReference.ToGpuMaterial`, `CreatePipelineFlags`, `ColorPipelineFlags` | [current/decomp/KSA/GroundClutterMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterMaterialReference.cs#L139-L190) | Row data and pipeline grouping for private materials |
| `ShaderModuleUtils.FromString` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79) | Compile the patched GLSL string |
| Shader id `ClutterSolidFrag` | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L159) | Maps to `Shaders/Planet/GroundClutter/Solid.frag` |

## Pitfalls and version notes

- **Exact-count transpiler.** Each of the four methods must contain exactly one matching call. A game
  update that adds a second `MaterialBuffer` read (or moves the read into a helper) makes Pebbles
  fail to patch, which `TryApply` logs and the feature becomes unavailable; it does not fall back to
  mutating the shared buffer. All four counts are one in build 5554.
- **Shader marker.** The patch keys on the literal
  `diffuseSample.rgb *= (groundColor / averageLuminosity);`, present exactly once at Solid.frag L338
  in 5554. A reworded line throws "Clutter color shader changed".
- **Flag bits 30 and 31.** The stock `GroundClutterMaterialData.flags` comment documents bits 0-6;
  Pebbles assumes the high bits stay free.[^ksa-clutter-common-glsl]
- **sRGB detection** uses `Texture.Format.ToString().Contains("Srgb")`, a name heuristic; imported
  GLB textures are uploaded as `R8G8B8A8UNorm`, so they always take the gamma 2.2 decode path.
- **Shader prefix leaves `ShaderReference.Stage` untouched**; `SetShaderFromMod` reads it after
  compilation, so it relies on the stage already being fragment from an earlier stock compile
  (unverified for a first-ever compile).[^ksa-rendering-utils]
- **Partial cleanup only.** The constructor capture cannot see allocations a native constructor holds
  in locals; see the authors' failure-limits section.[^scope-ground-clutter]
- **Draws.** The private buffer is bound only through descriptors created inside the context; there
  is no draw-time hook, so frame cost is unchanged.[^ksa-clutter-render-data][^ksa-shader-reference][^ksa-clutter-material-reference][^ksa-default-assets]

## See also

- [Per-celestial ground clutter replacement](./pebbles-ground-clutter-replacement.md)
- [GLB material conversion](./pebbles-glb-material-conversion.md) (produces the source-color materials)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Prefix on ExecuteNextClothSolvers builds private clutter bundles with originals kept for exact restore](../../../howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-2.md) - how-to article demonstrating this feature
- [Thread-static construction context scoping a transpiler and a prefix](../../../howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md) - how-to article demonstrating this feature
- [Thread-static construction context scopes transpiler and prefix redirection](../../../howto/rendering/textures-and-decals/replace-a-game-texture-at-runtime/replace-a-game-texture-at-runtime-3.md) - how-to article demonstrating this feature
- [Re-create the private material pipeline on the renderer-rebuild event](../../../howto/rendering/vulkan-passes/handle-renderer-rebuilds/handle-renderer-rebuilds-3.md) - how-to article demonstrating this feature

[^pebbles-source-map]: plans/PEBBLES_SOURCE_MAP.md (authors' native source investigation, build 5402)
[^clutter-hooks]: pebbles.lib/Runtime/ClutterHooks.cs
[^clutter-resources]: pebbles.lib/Runtime/ClutterResources.cs
[^ksa-clutter-common-glsl]: Core/Shaders/Planet/GroundClutter/Common/GroundClutterCommon.glsl (game content, build 2026.10.10.5554)
[^ksa-ground-clutter-renderer]: KSA.GroundClutterRenderer (decompiled, build 2026.10.10.5554)
[^ksa-solid-frag]: Core/Shaders/Planet/GroundClutter/Solid.frag (game content, build 2026.10.10.5554)
[^clutter-retirement]: pebbles.lib/Runtime/ClutterRetirement.cs
[^clutter-controller]: pebbles.lib/Runtime/ClutterController.cs
[^ksa-rendering-utils]: KSA.Rendering.Utils (decompiled, build 2026.10.10.5554)
[^scope-ground-clutter]: scope/ground-clutter.md (authors' integration notes)
[^ksa-clutter-render-data]: KSA.ClutterEcotypeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-shader-reference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-clutter-material-reference]: KSA.GroundClutterMaterialReference (decompiled, build 2026.10.10.5554)
[^ksa-default-assets]: Core/DefaultAssets.xml (game content, build 2026.10.10.5554)
