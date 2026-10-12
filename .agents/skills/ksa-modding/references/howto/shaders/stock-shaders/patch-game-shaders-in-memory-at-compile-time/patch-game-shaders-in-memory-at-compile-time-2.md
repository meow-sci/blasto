---
type: How-To
title: "Patch shaders in memory: Model.vert, Model_Skinned.vert and ModelPbr.frag patched for canopy projection"
description: Force three model shaders through the compile path with a SetShaderFromMod prefix, patch their source in a FromFile prefix and key the new behaviour on a material marker
category: shaders
group: stock-shaders
topic: patch-game-shaders-in-memory-at-compile-time
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/free-fallin-canopy-projection-shader]
techniques: [harmony-prefix, glsl-shader]
hooks:
  - harmony-prefix KSA.Rendering.Utils.SetShaderFromMod
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - starmap StarMapAllModsLoaded
lifecycle: [startup:all-mods-loaded, frame:prepare, frame:render]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - KSA.Rendering.Utils
  - RenderCore.Pipelines.SimplePipeline
  - KSA.Program
tags: [glsl, shader-patching, in-memory, canopy, skinned-mesh, material-marker, descriptor-sets, unscience]
---
Free Fallin's "full canopy" mode stretches one PNG across a whole parachute canopy. Doing that needs a second UV computed in the skinned vertex shader and a re-sample in
`ModelPbr.frag`, applied only to the canopy material. This article shows the part that differs from [Patch vehicle paint and canopy shaders in memory at compile time](overview.md) article 1: model pipelines
do not reach `FromFile` by default, so a second prefix forces them there. It also carries a warning about hard-coded descriptor set numbers. Needs StarMap and Harmony.

## Background: how the game does it

Model pipelines call `Utils.SetShaderFromMod(stages, device, modId, useCustomOptions)`. With `useCustomOptions == false` (the static and skinned PBR pipelines) the game binds the cached
`ShaderReference` module and never compiles; only when it is true does it call `CompileVariantWithCustomOptions`, which ends in `ShaderModuleUtils.FromFile`:

```csharp
// current/decomp/KSA.Rendering/Utils.cs L636-L652
	public static void SetShaderFromMod(this SimpleShaderStages stages, Device device, string modId, bool useCustomOptions = false)
	{
		ShaderReference shaderReference = ModLibrary.Get<ShaderReference>(modId);
		CompileOptions? compileOptions = stages.CompileOptions;
		if (compileOptions.HasValue && useCustomOptions)
		{
			VkShaderModule? vkShaderModule = shaderReference.CompileVariantWithCustomOptions(stages.CompileOptions.Value);
			if (vkShaderModule.HasValue)
			{
				stages.ExternalShaders = false;
				stages.SetShader(device, vkShaderModule.Value, shaderReference.Stage);
				return;
			}
		}
		stages.ExternalShaders = true;
		stages.SetShader(device, shaderReference, shaderReference.Stage);
	}
```

Source: [current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L636-L652). `SuperMeshRenderSystem.Rebuild` calls `SetShaders` for these pipelines, and `CharacterRenderResources.Rebuild` also routes `ModelVert` and
`ModelSkinnedVert` (kitten glass and eyes) through the same method.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Sources validated, both prefixes installed, rebuild requested | `FreeFallinPatches.Apply` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| First compile through the prefixes | In the `Program` constructor, `SuperMeshRenderSystem` is created after `ModLibrary.LoadAll` and its constructor calls `Rebuild` -> `SetShaders` | between `startup:all-mods-loaded` and `startup:game-loaded` |
| Later compiles | `RebuildRenderer` -> `SuperMeshRenderSystem.Rebuild`, requested through `Program.RendererRebuildNeeded` | `frame:prepare` / `frame:render` |
| Projection used | Every skinned draw of a marked material; no mod code runs per frame | `frame:render` |

The shaders are armed at startup whether or not Full canopy is ever used, because rebuild is the only point where pipelines can safely be recompiled. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Force the three ids onto the compile path.** A prefix on `SetShaderFromMod` sets `useCustomOptions = true` for exactly `ModelVert`, `ModelSkinnedVert` and `ModelPbrFrag`:

   ```csharp
   // free-fallin.lib/CanopyProjectionShaders.cs L89-L98
       private static void SetShaderFromModPrefix(string modId, ref bool useCustomOptions)
       {
           if (!Available) return;
           foreach (string shaderId in ShaderIds)
           {
               if (!shaderId.Equals(modId, StringComparison.OrdinalIgnoreCase)) continue;
               useCustomOptions = true;
               return;
           }
       }
   ```

   Source: [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L89-L98). Resolve the target with `AccessTools.Method(typeof(Utils), nameof(Utils.SetShaderFromMod), new[] { typeof(SimpleShaderStages), typeof(Device), typeof(string), typeof(bool) })`
   ([free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L30-L36)).

2. **Patch the source in the `FromFile` prefix**, exactly as in article 1: match by file name, call `FromString` with the caller's options and a NUL-terminated path, return `false`; on any
   failure log, return `true` and compile stock:

   ```csharp
   // free-fallin.lib/CanopyProjectionShaders.cs L100-L122
       private static bool FromFilePrefix(Device device, string filePath, ref VkShaderStageFlags shaderStage,
           CompileOptions? options, ref VkShaderModule __result)
       {
           if (!Available || !IsTarget(filePath)) return true;
           try
           {
               byte[] source = GetPatchedSource(filePath);
               VkShaderStageFlags stage = ShaderModuleUtils.ShaderStageFromFileExtension(filePath);
               __result = ShaderModuleUtils.FromString(device, source, stage, options, NullTerminated(filePath));
               shaderStage = stage;
               string name = Path.GetFileName(filePath);
               if (name.Equals(SkinnedVertex, StringComparison.OrdinalIgnoreCase)) _skinnedVertexCompiled = true;
               if (name.Equals(PbrFragment, StringComparison.OrdinalIgnoreCase)) _pbrFragmentCompiled = true;
               Console.WriteLine($"free-fallin: compiled full-canopy shader {name}");
               return false;
           }
           catch (Exception ex)
           {
               LastError = $"Patched {Path.GetFileName(filePath)} failed to compile: {ex.Message}";
               Console.WriteLine($"free-fallin: {LastError}; using the stock shader");
               return true;
           }
       }
   ```

   Source: [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L100-L122). Validate every source at `Apply` time (`ValidateSources` patches all three eagerly); a missing anchor disables the feature instead of failing at draw time.

3. **Insert with exact-string anchors.** Static vertex: a pass-through varying so static pipelines still link with the shared fragment shader. Skinned vertex: the projection UV from the bind-pose X/Z. Fragment: re-sample
   albedo only for the marked material:

   ```csharp
   // free-fallin.lib/CanopyProjectionShaders.cs L183-L213
       private static string PatchSkinnedVertex(string source)
       {
           source = InsertAfter(source, "#version 450",
               "\n#define SET_TEXTURE 1\n#include \"../Common/TextureSet.glsl\"\n\n#define SET_MATERIAL 2\n#include \"../Common/MaterialSet.glsl\"");
           source = InsertAfter(source,
               "layout (location = 2) out flat ivec4 outInstanceID;",
               "layout (location = 3) out vec2 outFreeFallinUv;");
           return InsertAfter(source,
               "outWorldPosV = vec4(worldPos.xyz, inUv.y);",
               "    Material ffMaterial = materialData[outInstanceID.x];\n" +
               "    vec2 ffSource = vec2(inPos.x, -inPos.z) * ffMaterial.extraData.x;\n" +
               "    outFreeFallinUv = vec2(\n" +
               "        ffMaterial.extraData.y * ffSource.x - ffMaterial.extraData.z * ffSource.y,\n" +
               "        ffMaterial.extraData.z * ffSource.x + ffMaterial.extraData.y * ffSource.y) + vec2(0.5);");
       }

       private static string PatchFragment(string source)
       {
           source = InsertAfter(source,
               "layout (location = 2) in flat ivec4 inInstanceID;",
               "layout (location = 3) in vec2 inFreeFallinUv;");
           return InsertAfter(source,
               "GetMaterialData(materialID, uv, albedo, normalMap, roughness, metallic, ao, emissive);",
               "    if (abs(materialData[materialID].extraData.w - 31415.0) < 0.25)\n" +
               "    {\n" +
               "        Material ffMaterial = materialData[materialID];\n" +
               "        albedo = ffMaterial.albedoColor * texture(\n" +
               "            sampler2D(globalTextures[ffMaterial.albedoIndex], samplers[ffMaterial.samplerIndex]),\n" +
               "            inFreeFallinUv);\n" +
               "    }");
       }
   ```

   Source: [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L183-L213). The anchors are lines of the stock files: `outInstanceID` and `outWorldPosV` in
   [current/Content/Core/Shaders/Mesh/Model.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/Model.vert#L19-L31), and `inInstanceID` and the `GetMaterialData` call in [current/Content/Core/Shaders/Mesh/ModelPbr.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelPbr.frag#L39-L64).

4. **Key the behaviour on a material marker.** Only the canopy's material carries `ExtraData.w == 31415`; every other model keeps stock shading. The mod packs `(0.5 / hemRadius, cos, sin, 31415)` into
   the material:

   ```csharp
   // free-fallin.lib/CanopyMaterialController.cs L148-L161
       private static float4 ProjectionData(CanopyMaterialSettings settings, SuperMeshRenderSystem renderSystem)
       {
           if (settings.TextureMode != CanopyTextureMode.FullCanopy) return float4.Zero;
           CanopyProjectionShaders.RequireAvailable();

           SkeletonAssetRef? skeleton = renderSystem.GltfSystemSkinned.GetOrLoad(CanopyGlbId).Skeleton;
           float radius = skeleton == null ? 0f : ChuteCanopyBones.MeasureBindHemRadius(skeleton.SkeletonData);
           if (!(radius > 0f) || !float.IsFinite(radius))
               throw new InvalidOperationException("Could not measure the stock canopy bind-pose radius for full-canopy projection.");

           float radians = settings.FullCanopyRotationDegrees * (MathF.PI / 180f);
           return new float4(0.5f / radius, MathF.Cos(radians), MathF.Sin(radians),
               CanopyProjectionShaders.MaterialMarker);
       }
   ```

   Source: [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L148-L161). `RequireAvailable` refuses to apply the mode until both `Model_Skinned.vert` and `ModelPbr.frag` have actually been compiled through the patch.
   How data rides in a material is the subject of [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md).

5. **Install and remove both prefixes together.** `Apply` patches `FromFile` and `SetShaderFromMod` and sets `Program.RendererRebuildNeeded = true`; `Remove` unpatches both and requests another rebuild back to stock
   ([free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L50-L76)).

## Complete example

| File | Role |
|---|---|
| [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs) | Apply/Remove, both prefixes, validation, the three source patches |
| [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs) | `ProjectionData` |
| [free-fallin.lib/FreeFallinPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/FreeFallinPatches.cs) | Installs the shader patch with the draw prefix |

## Variations and gotchas

- **Descriptor-set renumbering in 5541 breaks the skinned-vertex patch (unverified at runtime).** The patch hard-codes `SET_TEXTURE 1` and `SET_MATERIAL 2`, which matched build 5482's mesh layout.
  `ModelPbr.frag` in 5554 declares `SET_TEXTURE 5`, `SET_MATERIAL 1`, `SET_CSM 2` ([current/Content/Core/Shaders/Mesh/ModelPbr.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelPbr.frag#L4-L16)), and `SuperMeshRenderSystem` builds pipeline layouts from a fixed
  `ExternalSets` list with the material layout first and the shadow-map layout second ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L583-L590)), not from shader reflection. So set 2 in the patched vertex shader is the shadow-map set.
  The read of `materialData` is unconditional for every vertex of every skinned pipeline using `ModelSkinnedVert` (canopies, kittens). This comes from static analysis: shaderc still compiles it, and the runtime effect was not observed.
  A safer patch includes `MaterialSet.glsl` with the defines the stock fragment shader uses, read from the stock source.
- **Varying location 3** must stay free in `Model.vert`, `Model_Skinned.vert` and `ModelPbr.frag`.
- **Marker collisions.** Any material whose `ExtraData.w` is 31415 would be projected; the check has a 0.25 tolerance.
- **Compose with other `FromFile` prefixes.** Each prefix returns `true` for files it does not own, so this one and the paint patch coexist.
- **Rebuild costs a frame.** Apply and Remove each request a full renderer rebuild.

## See also

- Siblings: [vehicle paint](patch-game-shaders-in-memory-at-compile-time-1.md), [gatOS transform](patch-game-shaders-in-memory-at-compile-time-3.md), [overview](overview.md)
- [Material-gated full-canopy projection by patching KSA's model shaders in memory](../../../../mods/unscience/features/free-fallin-canopy-projection-shader.md)
- [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
