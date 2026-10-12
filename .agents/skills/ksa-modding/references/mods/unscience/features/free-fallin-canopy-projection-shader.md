---
type: Mod Feature
title: Material-gated full-canopy projection by patching KSA's model shaders in memory
description: Prefixes on Utils.SetShaderFromMod and ShaderModuleUtils.FromFile recompile Model.vert, Model_Skinned.vert and ModelPbr.frag from patched source, adding a bind-pose projection UV that ModelPbr.frag uses for albedo only on materials whose ExtraData carries Free Fallin's marker
mod: unscience
submod: free-fallin
category: shaders
techniques: [harmony-prefix, glsl-shader, shader-patching]
hooks:
  - harmony-prefix KSA.Rendering.Utils.SetShaderFromMod
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
lifecycle: [startup:all-mods-loaded, frame:prepare, frame:render]
game_types:
  - KSA.Rendering.Utils
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - KSA.SuperMeshRenderSystem
  - KSA.CharacterRenderResources
  - KSA.ChuteCanopyBones
  - KSA.GltfPbrSystem
  - KSA.MaterialData
  - KSA.Program
  - RenderCore.Pipelines.SimplePipeline
tags: [shaders, glsl, parachutes, uv-projection, harmony, renderer-rebuild, skinning]
---

"Full canopy" mode puts one PNG across the whole deployed parachute, so adjacent gores show adjacent
parts of one image instead of the image repeating in every panel. The canopy's authored UVs still
drive the cloth normal map and PBR map, and the projection follows the skinned cloth as it inflates
and flutters.[^ff-lib-readme]

Mechanism in one line: the skinned model vertex shader gets a second UV computed from the
**bind-pose** X/Z position (scaled by the canopy's measured hem radius and rotated by a user angle),
and `ModelPbr.frag` re-samples albedo with that UV only for materials whose `ExtraData.w` equals the
marker `31415`, so every other model keeps stock shading.[^ff-projection]

## How it works

**Getting the game to compile from source.** Model pipelines in `SuperMeshRenderSystem.Rebuild` set
their shaders with `SetShaders(...)` -> `Utils.SetShaderFromMod(stages, device, modId,
useCustomOptions)`. With `useCustomOptions == false` (the static and skinned PBR pipelines) the game
binds the cached `ShaderReference` module and never calls `FromFile`. A prefix on `SetShaderFromMod`
sets `ref bool useCustomOptions = true` for exactly `ModelVert`, `ModelSkinnedVert` and
`ModelPbrFrag`, which routes them through `ShaderReference.CompileVariantWithCustomOptions` ->
`ShaderModuleUtils.FromFile`, where the second prefix substitutes the patched source (same technique
as [vehicle paint](./humble-arteest-vehicle-paint-shader-patch.md): caller's `CompileOptions`,
original path as NUL-terminated debug name, stock fallback on a compile error).[^ff-projection][^ksa-utils][^ksa-supermesh][^ksa-shaderreference]

**The patches** (exact-string anchors, all validated at `Apply`; a missing anchor disables the feature):

| Shader | Insertion |
|---|---|
| `Model.vert` | `layout (location = 3) out vec2 outFreeFallinUv;` and `outFreeFallinUv = inUv;` (pass-through so static pipelines still link with the shared fragment shader) |
| `Model_Skinned.vert` | `TextureSet.glsl` and `MaterialSet.glsl` includes with `SET_TEXTURE 1` / `SET_MATERIAL 2`; the varying; `materialData[outInstanceID.x]` -> `uv = rotate((inPos.x, -inPos.z) * extraData.x, extraData.yz) + 0.5` |
| `ModelPbr.frag` | `layout (location = 3) in vec2 inFreeFallinUv;`; after `GetMaterialData(...)`, if `abs(extraData.w - 31415) < 0.25`, `albedo = albedoColor * texture(albedo, inFreeFallinUv)` |

**Material parameters** (`CanopyMaterialController.ProjectionData`): for Full canopy only,
`ExtraData = (0.5 / hemRadius, cos(rotation), sin(rotation), 31415)`, where `hemRadius` is
`ChuteCanopyBones.MeasureBindHemRadius` of the stock `ParachuteCanopyGlb` skeleton, so the canopy hem
maps to the unit circle around UV (0.5, 0.5). `RequireAvailable` refuses to apply the mode until both
`Model_Skinned.vert` and `ModelPbr.frag` have actually been compiled through the patch.[^ff-controller][^ksa-chutecanopybones]
The instance's material index reaches the vertex shader in `inInstanceData.x` (the value
[material substitution](./free-fallin-canopy-material-substitution.md) swaps), which is why the
vertex stage can look up the canopy's own `ExtraData`.[^ksa-model-skinned-vert]

## Lifecycle and timing

| Step | Where (build 5554) | Lifecycle |
|---|---|---|
| Sources validated, both prefixes installed, `Program.RendererRebuildNeeded = true` | `FreeFallinPatches.Apply` from `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Rebuild consumed | First of `Program.PrepareFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2147), L2143), `Program.Render` (L2445, the frame is skipped) or `PostRender` (L2351) after the flag is set | `frame:prepare` or `frame:render` |
| Prefixes first run | `Program` constructor: `SuperMeshRenderSystem` is created at L1034, after `ModLibrary.LoadAll` (L942) fired `[StarMapAllModsLoaded]`, and its constructor calls `Rebuild` -> `SetShaders`, so the initial pipelines are already built from the patched sources | rest of the `Program` constructor (after `startup:all-mods-loaded`, before `startup:game-loaded`) |
| Prefixes run again | `RebuildRenderer` (L5105) -> `SuperMeshRenderSystem.Rebuild` (L5124) -> `SetShaders` -> `SetShaderFromMod`; `CharacterRenderResources.Rebuild` (L5125) also routes `ModelVert` / `ModelSkinnedVert` (kitten glass and eyes) through `SetShaderFromMod`; pipelines without `CompileOptions` keep the cached module | same |
| Projection used | Every skinned draw with a marked material; parameters only change on Apply | per frame, no mod code |

The shaders are armed at startup whether or not Full canopy is ever used, because the rebuild is the
only point where pipelines can be recompiled safely (`RebuildRenderer` waits for the device to go idle
first). Removing the patch requests another rebuild back to stock.[^ff-projection][^ksa-program][^ksa-characterrenderresources]

## Key code

| Path | Role |
|---|---|
| [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L45-L122) | Apply/Remove, both prefixes |
| [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs#L124-L221) | Validation, targets, the three source patches |
| [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L148-L161) | `ProjectionData` |
| [free-fallin.lib/FreeFallinPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/FreeFallinPatches.cs#L35-L46) | Installs the shader patch with the draw prefix |

## Snippets

Forcing three shader ids through the compile path:

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

Why the first prefix is needed: without custom options the cached module is used.

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

The skinned-vertex and fragment patches:

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

Projection parameters packed into the material:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Utils.SetShaderFromMod(SimpleShaderStages, Device, string, bool)` | [current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L636-L652) | Patched (prefix on `useCustomOptions`) |
| `ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L170) | Patched (prefix; shared seam with vehicle paint) |
| `ShaderReference.CompileVariantWithCustomOptions` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L120-L137) | Path that reaches `FromFile` |
| `SuperMeshRenderSystem.Rebuild` / `SetShaders` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L896-L974) | Model pipelines that use the three ids |
| `SuperMeshRenderSystem.SetupDefaultRenderers` external sets | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L563-L628) | Set order the shaders must match |
| `SimplePipeline.CreateLayout` | [current/decomp/RenderCore.Pipelines/SimplePipeline.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Pipelines/SimplePipeline.cs#L98-L136) | Layout is fixed from `ExternalSets`, not reflected from SPIR-V |
| `CharacterRenderResources` glass and eye shaders | [current/decomp/KSA/CharacterRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterRenderResources.cs#L72-L125) | Also compile `ModelVert` / `ModelSkinnedVert` |
| `ChuteCanopyBones.MeasureBindHemRadius(Skeleton)` | [current/decomp/KSA/ChuteCanopyBones.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteCanopyBones.cs#L65) | Projection scale |
| Anchors in `Model.vert` | [current/Content/Core/Shaders/Mesh/Model.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/Model.vert#L19-L31) | `outInstanceID` (L19), `outWorldPosV` (L31) |
| Anchors in `Model_Skinned.vert` | [current/Content/Core/Shaders/Mesh/Model_Skinned.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/Model_Skinned.vert#L1-L55) | `#version` (L1), `outInstanceID` (L22), `outWorldPosV` (L55) |
| Anchors and set defines in `ModelPbr.frag` | [current/Content/Core/Shaders/Mesh/ModelPbr.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelPbr.frag#L1-L64) | `SET_TEXTURE 5`, `SET_MATERIAL 1`, `SET_CSM 2`; anchors at L39 and L64 |
| `Material.extraData` | [current/Content/Core/Shaders/Common/MaterialSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/MaterialSet.glsl#L12-L26) | Carries scale, rotation and marker |

## Pitfalls and version notes

- **Descriptor-set renumbering in 5541 breaks the skinned-vertex patch (unverified at runtime).** The
  patch hard-codes `SET_TEXTURE 1` / `SET_MATERIAL 2`, which matched 5482's mesh layout (texture set
  1, material set 2). In 5541 and 5554, `ModelPbr.frag` declares `SET_TEXTURE 5`, `SET_MATERIAL 1`,
  `SET_CSM 2`, and `SetupDefaultRenderers` lists the material layout first and the shadow-map layout
  second in `ExternalSets`, so set 1 is the material buffer and set 2 the shadow-map set. Pipeline
  layouts are built from that fixed list, not from shader reflection, so the patched
  `Model_Skinned.vert` reads `materialData` from the shadow-map set's binding 0. The read is
  unconditional for every vertex of every skinned pipeline that uses `ModelSkinnedVert` (canopies,
  kittens), and the patch is armed at startup. Static analysis only: shaderc still compiles it, and
  the effect at runtime (wrong UVs, validation errors or worse) was not observed. The fix is to drop
  the explicit defines and include `MaterialSet.glsl` with `#define SET_MATERIAL 1` and
  `#define SET_TEXTURE 5` matching `ModelPbr.frag`, or to read the defines from the stock fragment
  source.[^ksa-modelpbr-frag][^ksa-supermesh][^ksa-simplepipeline][^ksa-materialset-glsl]
- In 5541 `MaterialSet.glsl` also started including `TextureSet.glsl` itself (5482 required the caller
  to include it first), and `TextureSet.glsl` now refuses to compile without `SET_TEXTURE`.
- **Varying location 3** must stay free in `Model.vert`, `Model_Skinned.vert` and `ModelPbr.frag`; the
  pass-through in `Model.vert` keeps static pipelines link-compatible. `ModelNormal.frag` prepass
  pipelines simply ignore the extra output.[^ksa-model-vert]
- **Marker collisions.** Any material whose `ExtraData.w` happens to be 31415 would be projected;
  the marker is checked with a 0.25 tolerance.
- The scope document's 5482 verification states the seam and anchors held; it predates the 5541 set
  renumbering.[^scope-parachutes]
- Both shader patches share the `FromFile` seam with vehicle paint; each prefix returns `true` for
  files it does not own, so they compose.[^ksa-shadermoduleutils]

## See also

- [Canopy material substitution](./free-fallin-canopy-material-substitution.md)
- [Canopy texture compositing](./free-fallin-canopy-texture-compositing.md)
- [Vehicle paint shader patch](./humble-arteest-vehicle-paint-shader-patch.md)
- [Free Fallin sub-mod](../submods/free-fallin.md) and the [mod overview](../overview.md)
- [Patch shaders in memory: Model.vert, Model_Skinned.vert and ModelPbr.frag patched for canopy projection](../../../howto/shaders/stock-shaders/patch-game-shaders-in-memory-at-compile-time/patch-game-shaders-in-memory-at-compile-time-2.md) - how-to article demonstrating this feature
- [Smuggle data into stock shaders: a marker in a material's ExtraData plus a shader-side branch](../../../howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-3.md) - how-to article demonstrating this feature

[^ff-projection]: free-fallin.lib/CanopyProjectionShaders.cs
[^ff-controller]: free-fallin.lib/CanopyMaterialController.cs
[^ff-lib-readme]: free-fallin.lib/README.md
[^scope-parachutes]: scope/parachutes.md (authors' free-fallin integration analysis)
[^ksa-utils]: KSA.Rendering.Utils (decompiled, build 2026.10.10.5554)
[^ksa-shadermoduleutils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-characterrenderresources]: KSA.CharacterRenderResources (decompiled, build 2026.10.10.5554)
[^ksa-simplepipeline]: RenderCore.Pipelines.SimplePipeline (decompiled, build 2026.10.10.5554)
[^ksa-chutecanopybones]: KSA.ChuteCanopyBones (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-model-vert]: Mesh/Model.vert (game shader, build 2026.10.10.5554)
[^ksa-model-skinned-vert]: Mesh/Model_Skinned.vert (game shader, build 2026.10.10.5554)
[^ksa-modelpbr-frag]: Mesh/ModelPbr.frag (game shader, build 2026.10.10.5554)
[^ksa-materialset-glsl]: Common/MaterialSet.glsl (game shader header, build 2026.10.10.5554)
