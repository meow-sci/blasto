---
type: Mod Feature
title: Invisible shadow casters
description: Caster meshes that are never drawn are built as engine StaticMeshRenderables and pushed into the SuperMesh shadow pool each frame, so the sun and the lamps are blocked by a building whose static object casts no shadow
mod: ksastructures
category: rendering
techniques: [harmony-prefix, harmony-postfix, reflection, glb-model, xml-asset]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.LocationReference.UpdateStaticObjectRenderData
  - harmony-prefix KSA.SuperMeshShadowCasterProvider.PrepareShadowCasters
  - harmony-prefix KSA.SuperMeshRenderSystem.AddPoolToShadowBucket
  - reflection KSA.StaticMeshRenderable.MaterialIndices
  - reflection KSA.SuperMeshRenderSystem.GltfSystem
  - reflection KSA.SuperMeshRenderSystem.MaterialSystem
lifecycle: [startup:all-mods-loaded, frame:render, render:shadows]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.SuperMeshShadowCasterProvider
  - KSA.StaticMeshRenderable
  - KSA.ShadowRenderable
  - KSA.InstanceData
  - KSA.GltfPbrAssetRef
  - KSA.GpuObjectAssetRef
  - KSA.LoadedAssetRef
  - Core.AssetName
  - KSA.StaticObjectRenderer
  - KSA.Rendering.Lighting.LightSystemShadowMap
  - KSA.GameSettings
tags: [shadows, cascaded-shadow-maps, light-shadows, static-objects, supermesh, gltf]
---

Without this, daylight would reach the floor of a garage dug under the ground, and the ceiling lamps
would shine through the garage's own pillars. KSA's static objects are drawn and lit but never cast a
shadow. The mod ships two caster meshes that sit inside the roof, walls and pillars (one for the
garage, one generated for the deep ramp). It wraps them in the engine's own `StaticMeshRenderable`
and adds their depth instances to the shadow pool that both the sun's cascades and the lamps' shadow
atlas read. They never enter a colour pass, so they are never seen.

## How it works

**Why static objects cast nothing.** `StaticObjectRenderer` only records colour commands
(opaque, terrain-blended and blended buckets) and a depth pre-pass
([current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L295-L370)).[^ksa-staticobjectrenderer] The cascade loop in `RenderGame` draws
the terrain, the SuperMesh shadow pool, part models and splines, and no static objects
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4744-L4769)). Static objects do *receive* shadows: the main
view binds the cascade set ([current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L319-L332)) and
`StaticObject.frag` multiplies sunlight by the terrain shadow map, the cascades, cloud and celestial
shadows, then adds the light pre-pass result
([current/Content/Core/Shaders/Mesh/StaticObject.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/StaticObject.frag#L309-L332)).[^ksa-staticobject-frag] A caster that reaches the
cascade or the light atlas therefore darkens the garage.

**Assets.** Two `<GltfFile>` assets are declared and never referenced by any static object or part
([src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L27-L36)). KSA maps `<GltfFile>` to
`Gltf2Reference` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L52)); Core declares its kitten meshes the
same way ([current/Content/Core/CharacterAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CharacterAssets.xml#L3-L11)). The meshes are
[src/KSAStructures/Meshes/KSAOffice_ShadowCaster.glb](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Meshes/KSAOffice_ShadowCaster.glb) and the generated
[src/KSAStructures/Meshes/KSAOffice_DeepCaster.glb](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Meshes/KSAOffice_DeepCaster.glb).

**Building the renderables (once).** `Underground.Casters()` runs lazily from the main viewport's
lamp call, outside the editor ([src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L237-L287)):

1. The asset-name type is read off `LoadedAssetRef.Id`'s property type (`Core.AssetName`) and built
   with `Activator.CreateInstance(type, "KSAOffice_GarageMaterial")`.
2. `SuperMeshRenderSystem.MaterialSystem` and `.GltfSystem` are public fields, fetched by name, and
   their `GetOrLoad(AssetName)` is invoked by reflection ([current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs#L49)).
3. Every slot of the loaded `GltfPbrAssetRef.Materials` is overwritten with the garage material,
   because the `StaticMeshRenderable` constructor reads
   `GltfAssetRef.Materials[GltfAssetRef.MaterialIndices[i]].Handle` for each mesh
   ([current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L31-L54)).
4. `new StaticMeshRenderable(system.MeshRendererStaticPbr, gltfName, system.MeshRendererStaticPrePass, true)`
   via `Activator`. The constructor registers each mesh with the pass bucket system twice: once for
   colour and once for `ShadowPassRendererStatic`, which covers both `MeshPass.ShadowCsm` and
   `MeshPass.ShadowAtlas` ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L359-L362)).
5. The protected `MaterialIndices` array is read through `AccessTools.FieldRefAccess`.

**Every frame.** In the main viewport's `Underground.Light` call the building's axes and Ego origin
become a model matrix and `_fresh` is set
([src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L194-L202)). One prefix, `BeforeShadowBucket`, is
patched onto both `SuperMeshShadowCasterProvider.PrepareShadowCasters` and
`SuperMeshRenderSystem.AddPoolToShadowBucket`
([src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L110-L129)). Whichever runs first that frame clears
`_fresh` and appends one `ShadowRenderable` per caster mesh to `ShadowRenderablePool`, with the
instance layout `StaticMeshRenderable.Draw` itself uses: `data = (materialIndex, 0, 1, 0)`,
`model` = the building matrix, `Center` = its translation, `Radius` = 260 m
([current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L56-L90)).[^ksa-staticmeshrenderable]

## Lifecycle and timing

| When | Where | What happens to the pool |
|---|---|---|
| `frame:render` (before command recording) | `Program.Render` -> `UpdateRenderingResources` -> location render-data postfix, main viewport | Casters built once; model matrix computed; `_fresh = true` |
| `render:*` (secondary viewports) | `RenderViewport` -> `ClearBuckets` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4481-L4482)) | Emptied per camera window; no shadow pass there. Secondary viewports are recorded before the main one ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4683)) |
| main view start | `ClearBuckets`, then kittens, parachutes and seated crew draw ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4689-L4703)) | Emptied, then refilled with this view's skinned meshes |
| `render:shadows` (lamps) | `LightSystem.RenderShadowPass` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4721-L4728)) -> `LightSystemShadowMap.Render` -> `PrepareShadowCasters` | **Prefix fires here first**; entries within `light.Range + Radius` of each casting light go into that light's atlas bucket |
| `render:shadows` (sun) | Each cascade in `CascadesToRenderThisFrame`, if `GameSettings.ShowVesselInCascadeShadows()` -> `AddPoolToShadowBucket` | Prefix fires again but `_fresh` is false; pool entries are culled against the cascade's side planes |
| `frame:post-render` | `SuperMeshRenderSystem.OnFrameEnd` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2366-L2367)) | Emptied |

- The SuperMesh provider does not prepare in the update phase (`PreparesInUpdatePhase` defaults to
  false), so `PrepareShadowCasters` runs inside the shadow render, after the pool has this frame's
  contents ([current/decomp/KSA.Rendering.Lighting/IShadowCasterProvider.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/IShadowCasterProvider.cs#L18),
  [current/decomp/KSA.Rendering.Lighting/LightSystemShadowMap.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightSystemShadowMap.cs#L343-L367)).[^ksa-shadowmap][^ksa-supermesh-shadowprovider]
  `RenderGame` draws the lamps' atlas before the sun's cascades, and both read the same pool. That
  is why the mod patches both entry points and latches on whichever comes first.
- With light shadows turned off, `ClusteredLightSystem.RenderShadowPass` returns before preparing any
  caster ([current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L1215-L1228)), and the
  first cascade's `AddPoolToShadowBucket` adds the casters instead.
- Not every cascade is redrawn every frame, but the pool is only emptied at the next `ClearBuckets` or
  `OnFrameEnd`, so entries added at the first entry point are there for every later cascade that frame.[^ksa-supermesh]
- The renderables are built in the render-data phase on the main thread; the mod's comment gives the
  reason as keeping construction out of any pass that is recording.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L204-L235) | `BeforeShadowBucket`: latched push into `ShadowRenderablePool` |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L237-L287) | `Casters()` and `Load()`: reflection-built `StaticMeshRenderable`s |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L110-L129) | Both prefixes installed |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L194-L202) | Model matrix and `_fresh` latch, main viewport only |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L27-L36) | The two never-drawn `<GltfFile>` assets |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L4471-L4475) | `KSAOffice_GarageMaterial`, reused as the casters' material |

## Snippets

```csharp
// src/KSAStructures/Ksa/OfficeUnderground.cs L204-L235
    // Before the first cascade drawn this frame, which is whichever comes first: not every cascade
    // is redrawn every frame, and the pool they all read is emptied at the frame's end.
    private static void BeforeShadowBucket()
    {
        if (!_fresh) return;
        _fresh = false;

        try
        {
            if (_casters is not { } casters || _materialIndices is null) return;

            List<ShadowRenderable> pool = Program.Instance.SuperMeshRenderSystem.ShadowRenderablePool;
            foreach (StaticMeshRenderable caster in casters)
            {
                int[] materials = _materialIndices(caster);
                for (int i = 0; i < caster.ShadowDepthMeshBucketHandles.Length && i < materials.Length; i++)
                {
                    pool.Add(new ShadowRenderable
                    {
                        Center = _model.Translation,
                        Radius = 260f,
                        BucketHandle = caster.ShadowDepthMeshBucketHandles[i],
                        InstanceData = new InstanceData { model = _model, data = new float4(materials[i], 0f, 1f, 0f) },
                    });
                }
            }
        }
        catch
        {
            // A sunlit garage is the only failure allowed inside the engine's frame.
        }
    }
```

```csharp
// src/KSAStructures/Ksa/OfficeUnderground.cs L249-L271
            SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
            object materialName = Activator.CreateInstance(AssetNameType!, CasterMaterial)!;
            if (Load(system, "MaterialSystem", materialName) is not GpuObjectAssetRef material)
            {
                throw new InvalidOperationException("the casters' material did not load");
            }

            var built = new List<StaticMeshRenderable>();
            foreach (string id in CasterGltfs)
            {
                object gltfName = Activator.CreateInstance(AssetNameType!, id)!;
                if (Load(system, "GltfSystem", gltfName) is not GltfPbrAssetRef gltf) throw new InvalidOperationException($"'{id}' did not load");

                // The renderable reads a material handle per mesh as it is built, so every slot is filled first.
                for (int i = 0; i < gltf.Materials.Length; i++) gltf.Materials[i] = material;

                built.Add((StaticMeshRenderable)Activator.CreateInstance(
                    typeof(StaticMeshRenderable), system.MeshRendererStaticPbr, gltfName, system.MeshRendererStaticPrePass, true)!);
            }

            _materialIndices = AccessTools.FieldRefAccess<StaticMeshRenderable, int[]>("MaterialIndices");
            _casters = [.. built];
            Log.Info($"office: {_casters.Length} shadow casters built");
```

```csharp
// current/decomp/KSA/Program.cs L4744-L4767
		Profiler.MainThread.Begin(Profiler.Tag.RecordCascadedShadows);
		using (commandBuffer2.TagRegion(Profiler.GpuTag.CascadedShadows))
		{
			IReadOnlyList<int> cascadesToRenderThisFrame = _cascadedShadowSystem.CascadesToRenderThisFrame;
			for (int k = 0; k < cascadesToRenderThisFrame.Count; k++)
			{
				int num = cascadesToRenderThisFrame[k];
				using (commandBuffer2.TagRegion(Profiler.GpuTag.ShadowCascade[num]))
				{
					_cascadedShadowSystem.BeginCascade(commandBuffer2, num);
					if (GameSettings.ShowTerrainShadows())
					{
						_planetRenderer.RenderShadow(commandBuffer2, RenderedViewport, resourceFrameIndex, num);
					}
					if (GameSettings.ShowVesselInCascadeShadows())
					{
						SuperMeshRenderSystem.AddPoolToShadowBucket(num, in _cascadedShadowSystem.CascadeData[num].ViewProj, _cascadedShadowSystem.CurrentSettings.MaxPenumbraWorld);
						SuperMeshRenderSystem.RenderShadowPass(float4x4.Identity, float4x4.Identity, commandBuffer2, num);
						PartModelRenderer.DepthData.AddCommandsCSM(commandBuffer2, RenderedViewport, resourceFrameIndex, num);
						SplineRenderer.DepthData.AddCommandsCSM(commandBuffer2, RenderedViewport, resourceFrameIndex, num);
					}
					_cascadedShadowSystem.EndCascade(commandBuffer2, num);
				}
			}
```

```csharp
// current/decomp/KSA/SuperMeshShadowCasterProvider.cs L139-L157
	public bool PrepareShadowCasters(ReadOnlySpan<Light> inCastingLights, int inFrameIndex)
	{
		TimerPrepare.Start();
		int length = inCastingLights.Length;
		for (int i = 0; i < length; i++)
		{
			_renderSystem.PassBucketSystem.Clear(_buckets[i].View, MeshPass.ShadowAtlas);
		}
		foreach (ShadowRenderable item in _renderSystem.ShadowRenderablePool)
		{
			ShadowRenderable renderable = item;
			for (int j = 0; j < length; j++)
			{
				if (IsVisibleToLight(in renderable, in inCastingLights[j]))
				{
					_renderSystem.PassBucketSystem.DrawMeshInstance(in renderable.BucketHandle, renderable.InstanceData, _buckets[j].View, MeshPass.ShadowAtlas);
				}
			}
		}
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `SuperMeshRenderSystem.ShadowRenderablePool` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L54) | The list both shadow passes read |
| `SuperMeshRenderSystem.AddPoolToShadowBucket` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L524-L539) | Patched; per-cascade cull and bucket fill |
| `SuperMeshRenderSystem.ClearBuckets` / `OnFrameEnd` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L561) | Where the pool is emptied |
| `SuperMeshRenderSystem.MaterialSystem`, `.GltfSystem`, `.MeshRendererStaticPbr`, `.MeshRendererStaticPrePass` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L42-L84) | Asset managers and renderers passed to the constructor |
| `SuperMeshShadowCasterProvider.PrepareShadowCasters` | [current/decomp/KSA/SuperMeshShadowCasterProvider.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshShadowCasterProvider.cs#L131-L179) | Patched; per-light range test and atlas bucket fill |
| `StaticMeshRenderable` (constructor, `ShadowDepthMeshBucketHandles`, `MaterialIndices`) | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L9-L54) | Registers the caster meshes for the depth passes |
| `ShadowRenderable`, `InstanceData` | [current/decomp/KSA/ShadowRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShadowRenderable.cs), [current/decomp/KSA/InstanceData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstanceData.cs) | Pool entry and per-instance data |
| `GltfPbrAssetRef.Materials`, `.MaterialIndices` | [current/decomp/KSA/GltfPbrAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GltfPbrAssetRef.cs#L7-L13) | Filled before construction |
| `LoadedAssetRef.Id` / `Core.AssetName` | [current/decomp/KSA/LoadedAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LoadedAssetRef.cs#L11), [current/decomp/Core/AssetName.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/AssetName.cs#L7-L21) | Asset-name type discovered by reflection |
| `GameSettings.ShowVesselInCascadeShadows` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3298-L3301) | Gates the whole cascade path (default on) |

## Pitfalls and version notes

- **"Vessel shadows" off means daylight in the garage.** With the graphics setting
  `VesselCascadeShadows` off (default true, [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L238)), the cascade
  loop never calls `AddPoolToShadowBucket`. The casters then only reach the lamp atlas, and the mod
  logs this once.
- **Main view only.** The latch is set from the main viewport, and only the main view has sun cascades
  and a light atlas. Camera windows render their static objects with the unshadowed cascade set
  ([current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L323-L330)), so they show the garage sunlit.
- **Radius 260 m** is a fixed bounding sphere for the whole site; per-light culling
  (`IsVisibleToLight`) and cascade-plane culling do little for it.
- **One failure disables it for the session.** `_casterFailed` latches if the material or a mesh does
  not load; the dark pass still runs, but the sun reaches the floor.
- **Reflection surface.** `MaterialIndices` is protected and the manager fields are found by string;
  a rename turns the feature off with a log line, not a crash.
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554, and the decompiled diff between the two touches none of these members.

## See also

- [Garage spot lights](./garage-spot-lights.md): the lamps whose shadows these casters block, and where `_fresh` is set
- [Underground dark compute pass](./underground-dark-compute-pass.md): removes the sky ambient that shadows cannot
- [Office landmarks as static objects](./office-landmarks-static-objects.md): the static objects that cast nothing
- [Generated deep ramp](./generated-deep-ramp.md): generates `KSAOffice_DeepCaster.glb`
- [Asset and mesh validation tools](./asset-and-mesh-validation-tools.md)
- [Add invisible depth-only shadow casters to the SuperMesh shadow pool](../../../howto/rendering/meshes/add-invisible-shadow-casters/add-invisible-shadow-casters.md) - how-to article demonstrating this feature

[^ksa-staticobjectrenderer]: KSA.StaticObjectRenderer (decompiled)
[^ksa-staticobject-frag]: Core shader Mesh/StaticObject.frag
[^ksa-staticmeshrenderable]: KSA.StaticMeshRenderable (decompiled)
[^ksa-shadowmap]: KSA.Rendering.Lighting.LightSystemShadowMap (decompiled)
[^ksa-supermesh-shadowprovider]: KSA.SuperMeshShadowCasterProvider (decompiled)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled)
