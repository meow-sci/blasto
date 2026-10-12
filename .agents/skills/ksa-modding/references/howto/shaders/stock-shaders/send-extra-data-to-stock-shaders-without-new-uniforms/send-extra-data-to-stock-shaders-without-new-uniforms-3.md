---
type: How-To
title: "Smuggle data into stock shaders: a marker in a material's ExtraData plus a shader-side branch"
description: Create a custom material whose ExtraData carries parameters and a marker value, then branch on the marker in a patched model shader so only that material changes
category: shaders
group: stock-shaders
topic: send-extra-data-to-stock-shaders-without-new-uniforms
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/free-fallin-canopy-projection-shader]
techniques: [glsl-shader, harmony-prefix]
hooks:
  - harmony-prefix RenderCore.ShaderModuleUtils.FromFile
  - harmony-prefix KSA.Rendering.Utils.SetShaderFromMod
lifecycle: [startup:all-mods-loaded, frame:render]
game_types: [KSA.MaterialData, KSA.SuperMeshRenderSystem, KSA.Program]
tags: [material, extradata, marker, canopy, side-channel, stock-shaders, glsl, unscience]
---
A material is the cheapest per-object carrier the model shaders already read: the stock `Material` struct has a `vec4 extraData` that the PBR model path ignores for ordinary meshes. Free Fallin puts four numbers there for one parachute canopy
material, including a marker value, and its patched shaders project a texture only when they see the marker. Every other model keeps stock shading. This article is the data half; the compile-path half (forcing the shaders to be patched) is
[Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md) article 2.

## Background: how the game does it

- **The material record.** `MaterialData` is a packed struct: texture handles, `AlbedoColor`, `RoughnessMetalScale`, `float4 ExtraData`, an emissive handle and padding ([current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs)). The shaders see it as `Material` in `MaterialSet.glsl`, indexed by
  the instance's material id, with a `vec4 extraData` ([current/Content/Core/Shaders/Common/MaterialSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/MaterialSet.glsl#L12-L26)).
- **The material buffer is device-local,** so never edit a shared material; create a new one with `renderSystem.MaterialSystem.CreateObject(name, material)` and point the draw at its handle (gatOS does the same for EVA paint with material clones).
- **Where the id reaches the shaders.** The instance's material index arrives in `inInstanceID.x` in the fragment shader (`int materialID = inInstanceID.x`, [current/Content/Core/Shaders/Mesh/ModelPbr.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelPbr.frag#L56)) and in `inInstanceData.x` in the vertex shader, which is why the vertex stage can look up the material too.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Shader patches installed, renderer rebuild requested | `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Material created / replaced | When the player applies the mode; the draw prefix points the canopy at the new handle | `frame:draw-ui` -> `frame:render` |
| Branch evaluated | Every skinned draw, on the GPU; no mod code per frame | `frame:render` |

See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick a marker that no real value takes.** `31415f` in `ExtraData.w`; the shader compares with a 0.25 tolerance so float round trips do not matter. Keep the other three components for parameters (here scale, cos, sin of the rotation).

2. **Build the parameters on the CPU and put them in `ExtraData`.** For the Full canopy mode only, `ExtraData = (0.5 / hemRadius, cos(rotation), sin(rotation), 31415)`, where `hemRadius` is measured from the stock canopy skeleton so the hem maps to the unit circle around UV (0.5, 0.5):

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

   Source: [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L148-L161). Other modes return `float4.Zero`, so the marker is absent and the stock path runs.

3. **Create the material and keep its handle.** Fill a `MaterialData`, allocate it by name, read back the handle, and swap the canopy's material index to it:

   ```csharp
   // free-fallin.lib/CanopyMaterialController.cs L70-L85
           var material = new MaterialData
           {
               AlbedoTexture = albedo.Handle,
               Sampler = albedo.Sampler,
               AlbedoColor = tint,
               NormalTexture = stock.NormalReference?.Get().BindlessHandle
                               ?? renderSystem.GltfSystemSkinned.BlankNormalTexture.BindlessHandle,
               RoughMetallicAOTexture = pbrHandle,
               RoughnessMetalScale = pbrScale,
               ExtraData = projectionData,
               EmissiveTexture = stock.EmissiveMap?.Get().BindlessHandle ?? textures.DefaultBlackTexture.BindlessHandle
           };

           AssetName materialName = $"free-fallin/material/{++_revision}";
           if (!renderSystem.MaterialSystem.CreateObject(materialName, material))
               throw new InvalidOperationException("Could not allocate a custom canopy material.");
   ```

   Source: [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L70-L85). Release superseded materials when the settings change.

4. **Read it in the patched shaders.** The skinned vertex shader reads the canopy's own material (`materialData[outInstanceID.x]`) and computes the second UV from the bind-pose X/Z; the fragment shader re-samples albedo only when the marker matches:

   ```glsl
   // adapted from the string in free-fallin.lib/CanopyProjectionShaders.cs L203-L210 (the inserted fragment block)
   if (abs(materialData[materialID].extraData.w - 31415.0) < 0.25)
   {
       Material ffMaterial = materialData[materialID];
       albedo = ffMaterial.albedoColor * texture(
           sampler2D(globalTextures[ffMaterial.albedoIndex], samplers[ffMaterial.samplerIndex]),
           inFreeFallinUv);
   }
   ```

   The full C# insertion code (both stages, with their anchors) is quoted in [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md) article 2. A pass-through varying in the static `Model.vert` keeps static pipelines link-compatible with the shared fragment shader.

## Complete example

| File | Role |
|---|---|
| [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs) | `ProjectionData`, material creation and release |
| [free-fallin.lib/CanopyProjectionShaders.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyProjectionShaders.cs) | The three source patches and the marker constant (`MaterialMarker`) |

## Variations and gotchas

- **Marker collisions.** Any material whose `ExtraData.w` happens to be 31415 would be projected.
- **Check what else uses `extraData`.** `CharacterRenderResources` fills `ExtraData` of the kitten fur material with texture handles ([current/decomp/KSA/CharacterRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterRenderResources.cs#L60)); a marker scheme must not hit those materials.
- **Descriptor sets in the patch.** The skinned-vertex patch hard-codes `SET_TEXTURE 1` and `SET_MATERIAL 2`, which does not match the 5554 fragment shader's `SET_TEXTURE 5` and `SET_MATERIAL 1`; it is flagged as an unverified runtime hazard in article 2 of the compile-time topic.
- **Per object, not per frame.** Anything that changes every frame belongs in a push constant or a uniform you own.

## See also

- Siblings: [temporary value swap](send-extra-data-to-stock-shaders-without-new-uniforms-1.md), [state-flag bits](send-extra-data-to-stock-shaders-without-new-uniforms-2.md), [sentinel dispatch](send-extra-data-to-stock-shaders-without-new-uniforms-4.md), [overview](overview.md)
- [Material-gated full-canopy projection by patching KSA's model shaders in memory](../../../../mods/unscience/features/free-fallin-canopy-projection-shader.md)
- [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md), [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md)
