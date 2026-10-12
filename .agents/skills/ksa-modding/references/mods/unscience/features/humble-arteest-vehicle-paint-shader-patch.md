---
type: Mod Feature
title: Per-part vehicle paint through free state-flag bits and an in-memory part shader patch
description: Vehicle Paint packs a 7:7:7 colour into the unused high bits of each part instance's StateBitFlag via postfixes on PartTreeRenderData's private state writers, and compiles a patched MeshIndirect fragment shader in memory by prefixing ShaderModuleUtils.FromFile during a requested renderer rebuild
mod: unscience
submod: humble-arteest
category: shaders
techniques: [harmony-prefix, harmony-postfix, reflection, glsl-shader, shader-patching]
hooks:
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-postfix KSA.PartTreeRenderData.WriteState
  - harmony-postfix KSA.PartTreeRenderData.WriteDynamicState
  - harmony-prefix KSA.PartTreeRenderData.EnsureBuilt
  - reflection KSA.PartTreeRenderData.Batch / DynamicBatch (private nested) .StateBitFlags
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Enable, blend mode, colour edits)
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - KSA.PartTreeRenderData
  - KSA.PartTree
  - KSA.PartModel
  - KSA.PartModelDynamic
  - KSA.PartModelDynamicModule
  - KSA.Part
  - KSA.Program
  - KSA.PartModelRenderer
  - KSA.ModLibrary
tags: [paint, shaders, glsl, harmony, part-models, instancing, renderer-rebuild]
---

Vehicle Paint recolours individual part instances, every instance of a part type, or the whole
vessel, in flight and in the vehicle editor, with three blend modes (Multiply, Tint, Replace). Glass
stays clear, and engine heat glow, battery lights, thin film and wetness keep working on painted
parts. Nothing on disk is modified.[^humble-readme]

Mechanism in one line: the colour rides in bits 11..31 of the per-instance `StateBitFlag` that every
part fragment shader already receives as `inStateFlags`, and a copy of `MeshIndirect.frag` /
`MeshIndirectRaytraced.frag` with a ten-line decode snippet is compiled in place of the file
whenever the game compiles those shaders.

## How it works

**Transport: the free bits.** `PerInstanceData` is 80 bytes in every part-model variant and the
std430 stride is full, so a new field is impossible. `StateBitFlag` (offset 64) is an `int` of which
the game uses bits 0..10. `VehiclePaint.EncodeBits` quantizes the sRGB colour to 7 bits per channel
(21 bits), shifts it to bit 11, and never encodes zero because zero means "unpainted" to the
shader.[^vehicle-paint][^ksa-partmodel]

**Writing the bits (KSA 5482+).** Part state flags are cached per tree slot in private
`PartTreeRenderData` batches and written only by private `WriteState(Batch, int, Part)` (static
models) and `WriteDynamicState(DynamicBatch, int, PartModelDynamicModule)` (dynamic models).
`ResolveWriteState` gets the private nested batch type with `AccessTools.Inner`, checks its
`StateBitFlags` field is `int[]`, builds a `FieldRefAccess`, and resolves the writer by its exact
parameter tuple. Postfixes (batch parameter bound as `object`) OR the part's paint bits into
`StateBitFlags[slot]`. `WriteGlassState` is deliberately left alone.[^paint-patches][^ksa-parttreerenderdata]

**Invalidating the cache.** The cache is only rewritten when dirty, so a prefix on public
`EnsureBuilt(PartTree, ulong)` compares `VehiclePaint.RenderStateVersion` (a counter bumped by every
paint mutation, shifted left, with the "shaders installed" bit in bit 0) against a
`ConditionalWeakTable<PartTreeRenderData, StrongBox<int>>`; a new tree or a changed version calls
`InvalidateStates()` once, so the next rewrite runs the postfixes again.[^paint-patches]

**Resolution order** in `TryGetPaintBits`: explicit per-`Part` colour (reference-keyed dictionary),
then per-template colour keyed by `Part.Id`, then the global colour if enabled. The dynamic postfix
paints `inModule.Parent`, so all models of one part share its paint.[^vehicle-paint]
`PaintTargets.Enumerate` lists flight vehicles or, in the editor, `Program.Editor.EditingSpace.Parts`
plus unattached trees, mirroring what `UpdateRenderingResources` walks.[^paint-targets]

**Shader patch.** KSA compiles part shaders straight from disk on every pipeline build:
`PartModelRenderer.BuildPipelineModel` / `BuildPipelineDynamic` and the ray-tracing raster pipeline
call `ShaderReference.CompileVariantWithCustomOptions`, which calls the four-argument
`ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)` with
per-variant macro defines (`ENABLE_EMISSIVE`, `ENABLE_TEMPERATURE`, ...).[^ksa-partmodelrenderer][^ksa-shaderreference]
The prefix on that overload:

- returns `true` (stock) unless the shaders are installed and the file name is `MeshIndirect.frag`
  or `MeshIndirectRaytraced.frag`;
- reads the current on-disk source, finds the first line that starts with `vec3 sampledColor` and
  ends with `;`, requires an `inStateFlags` declaration, and inserts the snippet right after it, so
  paint flows through thin film, frost and the whole PBR evaluation;
- compiles with `ShaderModuleUtils.FromString(device, source, stage, options, path + "\0")`, passing
  the caller's `CompileOptions` through (every variant keeps its defines) and the original path as
  the debug name (relative `#include`s resolve exactly as stock);
- sets `__result` and `shaderStage` and skips the original; any failure logs, drops the cache entry
  and falls back to the stock compile.[^paint-patches][^paint-shaders][^ksa-shadermoduleutils]

The blend mode is baked into the snippet, so changing it clears the source cache and requests
another rebuild. `Install` pre-flights `ModLibrary.Get<ShaderReference>("MeshIndirectFrag")` (required)
and `"MeshIndirectRaytracedFrag"` (optional) so a changed shader fails at Enable, not
silently.[^paint-shaders]

## Lifecycle and timing

| Step | Hook and moment (build 5554) | Lifecycle |
|---|---|---|
| Patches installed | `[StarMapAllModsLoaded]` -> unscience `Patcher.Patch()` | `startup:all-mods-loaded` |
| Enable / blend change / colour edits | ImGui panel (`RenderContent`, from `[StarMapAfterGui]`); Enable sets `Program.RendererRebuildNeeded = true` | `frame:draw-ui` |
| Shader compile (`FromFile` prefix) | The flag is consumed by the first of `Program.Render` (L2445: skips this frame's recording and calls `RebuildRenderer`), `PostRender` (L2351) or the next `PrepareFrame` (L2143). From a UI click it is `Render` of the same frame. `RebuildRenderer` (L5105) -> `RaytracingRenderer?.Rebuild()` (L5167) and `PartModelRenderer.Rebuild()` (L5170) recompile the part pipelines | `frame:render` (or `frame:prepare`) |
| `EnsureBuilt` prefix, `WriteState` / `WriteDynamicState` postfixes | `Program.Render` -> `UpdateRenderingResources` (L2468) -> `Vehicle.UpdateRenderData` (L4355) -> `PartTree.UpdateRenderData` -> `RenderData.EnsureBuilt` (`PartTree.cs` L1162) -> state rewrite | `frame:render` |
| Paint reaches the GPU | `Compose`/`ComposeDynamic` copy cached flags into `PerInstanceData`; `PartModelRenderer.UpdateRenderData` uploads | `frame:render` |

`RendererRebuildNeeded` is the game's own deferred-rebuild flag (graphics-setting changes use it):
`RebuildRenderer` waits for the device to go idle before destroying pipelines, so no frame in flight
uses a module that is being replaced.[^ksa-program] Colour changes never rebuild anything: they bump the
version, the next `EnsureBuilt` invalidates once, and the next frame shows the new colour. All of it
is main-thread render data; nothing touches physics. In ray-traced IVA, `Compose` appends through
`AddInstance`, and the ray-tracing renderer copies the same flag word into `RayTraceInstance.StateFlags`,
so painted parts stay painted there too.[^ksa-parttree][^ksa-raytracingrenderer] `EnsureBuilt` runs once per tree per frame from
[current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1171).

## Key code

| Path | Role |
|---|---|
| [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L47-L125) | Apply/Remove, exact-signature `FromFile` lookup, private nested batch resolution |
| [humble-arteest.lib/VehiclePaintPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintPatches.cs#L136-L210) | `FromFilePrefix`, state postfixes, `EnsureBuiltPrefix` |
| [humble-arteest.lib/VehiclePaintShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaintShaders.cs) | Install/rebuild request, target detection, anchor-based injection, GLSL snippet |
| [humble-arteest.lib/VehiclePaint.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/VehiclePaint.cs) | Registries, blend modes, `RenderStateVersion`, `TryGetPaintBits`, `EncodeBits` |
| [humble-arteest.lib/PaintTargets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/PaintTargets.cs#L67-L80) | Flight and editor target enumeration |
| [ksa-upgrade.tests/VehiclePaintChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-upgrade.tests/VehiclePaintChecks.cs) | Managed check against a fixture of the private batch writers |

## Snippets

Compiling a patched source instead of the file:

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

Writing paint into the cached slot flags, and invalidating once per change:

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

The 7:7:7 encoding:

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

The GLSL inserted after `vec3 sampledColor = ...;`:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L170) | Patched (prefix); the two-argument overload at L225 is not |
| `ShaderModuleUtils.FromString(..., ReadOnlySpan<byte> debugName)`, `ShaderStageFromFileExtension` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79-L115) | Compile the patched string (stage helper at L198) |
| `ShaderReference.CompileVariantWithCustomOptions(CompileOptions)` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L120-L137) | The game path that reaches `FromFile` with variant defines |
| `PartModelRenderer.BuildPipelineModel` / `BuildPipelineDynamic` | [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L129-L260) | Compile `MeshIndirectFrag` per variant |
| `PartTreeRenderData.WriteState` / `WriteDynamicState` (private) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1088-L1256) | Patched (postfix); the only writers of cached state flags |
| `PartTreeRenderData.Batch` / `DynamicBatch` `.StateBitFlags` (private nested) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L23-L111) | Field refs |
| `PartTreeRenderData.StateBit` (private constants) | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L196-L219) | Game bits stop at 1024 (bit 10) |
| `PartTreeRenderData.EnsureBuilt(PartTree, ulong)`, `InvalidateStates()` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L323-L498) | Patched (prefix); full state rewrite when `_allStatesDirty` |
| `PartModel.PerInstanceData` | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L383-L394) | `StateBitFlag` at offset 64 |
| `Program.RendererRebuildNeeded`, `Render`, `RebuildRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2451) | Deferred rebuild (flag L430, rebuild L5105) |
| `RayTraceInstance.StateFlags` copy | [current/decomp/KSA.Rendering.Raytracing/RaytracingRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Raytracing/RaytracingRenderer.cs#L1085-L1092) | Paint bits also reach ray-traced IVA instances |
| `outStateFlags = uint(instanceData.Highlighted)` | [current/Content/Core/Shaders/Mesh/MeshIndirect.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.vert#L47-L91) | The whole 32-bit word is forwarded to the fragment shader |
| `vec3 sampledColor`, `inStateFlags` bit tests | [current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L108) | Anchor (L108); stock tests cover bits 0..10 only (L306-L347) |

## Pitfalls and version notes

- **Free-bit check for 5554 (verified).** C# writers: `WriteState` sets 0x1, 0x2, 0x4, 0x8, 0x100,
  0x200, 0x400 plus the per-full-part dynamic state 0x40/0x80; `WriteDynamicState` the same set with
  0x40; `WriteGlassState` 0x1..0x8; `Compose*` OR 0x10/0x20; thumbnails write 0. Shader readers:
  `MeshIndirect.frag` and `MeshIndirectRaytraced.frag` test bits 0..10, and the path tracer
  ([current/Content/Core/Shaders/Raytracing/raygen.rgen](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Raytracing/raygen.rgen#L177)) tests bit 6 only. Bits 11..31 are unused. This is the invariant to
  re-check on every game update.[^ksa-parttreerenderdata][^ksa-meshindirect-frag][^ksa-meshindirectrt-frag][^ksa-raygen]
- **Anchor moved, still valid.** In 5554 the `vec3 sampledColor` line is at `MeshIndirect.frag` L108
  (the authors' scope cites L114 for 5482) and
  [current/Content/Core/Shaders/Mesh/MeshIndirectRaytraced.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirectRaytraced.frag#L156) L156; the vertex shader
  still forwards the full flag word.[^ksa-meshindirect-vert][^scope-materials]
- **5482 seam move.** Before 5482 the hooks were prefixes on `PartModelModule.UpdateRenderData` and
  `PartModel.AddInstance`; those were removed or stopped being reached by static raster parts, which
  is why paint now lives in the cached flags and needs the `EnsureBuilt` invalidation.[^scope-materials]
- **Precision and range.** 128 steps per channel in sRGB; Multiply can only darken, Tint scales by
  luminance x 2 so it can brighten, Replace ignores the albedo texture.
- **Cost.** Enable and blend-mode changes rebuild the whole renderer and drop one frame; colour edits
  are free. The path tracer does not apply paint (it is not patched).
- **Persistence.** The humble-arteest README says paint is session-only, but the unscience save
  participant (`HumbleArteestSubmod.Persistence.cs`) saves activation, blend mode and global,
  template and per-part paint; the standalone host has no saves.
- The managed check exercises the real postfixes against a fixture of `PartTreeRenderData`, not the
  game; native visual acceptance was still pending at the pinned ref.[^paint-checks]

## See also

- [Part render filter](./lib-part-render-filter.md) (also hooks `PartTreeRenderData` compose)
- [Engine emissive override](./humble-arteest-engine-emissive.md)
- [Kitten colour](./humble-arteest-kitten-color.md)
- [Free Fallin canopy projection shader](./free-fallin-canopy-projection-shader.md) (same `FromFile` seam)
- [Humble Arteest sub-mod](../submods/humble-arteest.md) and the [mod overview](../overview.md)
- [7:7:7 colour in the unused StateBitFlag high bits plus a patched MeshIndirect.frag](../../../howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-1.md) - how-to article demonstrating this feature
- [Patch shaders in memory: a MeshIndirect.frag patch for vehicle paint, recompiled on a requested rebuild](../../../howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-1.md) - how-to article demonstrating this feature
- [Patch shaders in memory: a compile-time transform with packed colours, a conflict check and runtime masters](../../../howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-3.md) - how-to article demonstrating this feature
- [Smuggle data into stock shaders: spare high bits of the part state flags](../../../howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-2.md) - how-to article demonstrating this feature

[^paint-patches]: humble-arteest.lib/VehiclePaintPatches.cs
[^paint-shaders]: humble-arteest.lib/VehiclePaintShaders.cs
[^vehicle-paint]: humble-arteest.lib/VehiclePaint.cs
[^paint-targets]: humble-arteest.lib/PaintTargets.cs
[^paint-checks]: ksa-upgrade.tests/VehiclePaintChecks.cs
[^humble-readme]: humble-arteest.lib/README.md (authors' design notes)
[^scope-materials]: scope/character-and-materials.md (authors' integration analysis)
[^ksa-shadermoduleutils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-partmodelrenderer]: KSA.PartModelRenderer (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-partmodel]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-raytracingrenderer]: KSA.Rendering.Raytracing.RaytracingRenderer (decompiled, build 2026.10.10.5554)
[^ksa-meshindirect-frag]: Mesh/MeshIndirect.frag (game shader, build 2026.10.10.5554)
[^ksa-meshindirect-vert]: Mesh/MeshIndirect.vert (game shader, build 2026.10.10.5554)
[^ksa-meshindirectrt-frag]: Mesh/MeshIndirectRaytraced.frag (game shader, build 2026.10.10.5554)
[^ksa-raygen]: Raytracing/raygen.rgen (game shader, build 2026.10.10.5554)
