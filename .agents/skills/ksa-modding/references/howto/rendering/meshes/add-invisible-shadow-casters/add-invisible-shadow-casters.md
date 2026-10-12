---
type: How-To
title: Add invisible depth-only shadow casters to the SuperMesh shadow pool
description: Make static objects or other geometry block sunlight and lamp light by appending ShadowRenderables for a never-drawn caster mesh from a prefix on PrepareShadowCasters and AddPoolToShadowBucket
category: rendering
group: meshes
topic: add-invisible-shadow-casters
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/ksastructures/features/invisible-shadow-casters]
techniques: [harmony-prefix, reflection, glb-model]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.LocationReference.UpdateStaticObjectRenderData
  - harmony-prefix KSA.SuperMeshShadowCasterProvider.PrepareShadowCasters
  - harmony-prefix KSA.SuperMeshRenderSystem.AddPoolToShadowBucket
  - reflection KSA.StaticMeshRenderable.MaterialIndices
lifecycle: [startup:all-mods-loaded, frame:render, render:shadows]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.SuperMeshShadowCasterProvider
  - KSA.StaticMeshRenderable
  - KSA.ShadowRenderable
  - KSA.InstanceData
  - KSA.GltfPbrAssetRef
tags: [shadows, shadow-caster, static-object, sun-cascades, shadow-atlas, glb]
---
Outcome: a building, roof or pillar that is a static object blocks the sun's cascaded shadows and the shadow atlas of nearby lamps, even though static objects never enter a shadow pass. You ship a simple "caster" mesh that sits inside the real geometry, wrap it in the engine's own `StaticMeshRenderable` (which registers it for the depth passes) and append its instances to the shadow pool. The caster is never put in a colour pass, so it is never seen.

Demonstrated by KSAStructures' underground garage ([Invisible shadow casters](../../../../mods/ksastructures/features/invisible-shadow-casters.md)). Prerequisites: StarMap, Harmony, reflection into three members, and a `<GltfFile>` asset for the caster.

## Background: how the game does it

Static objects are drawn and they receive shadows (the main view binds the cascade set and `StaticObject.frag` multiplies sunlight by it), but `StaticObjectRenderer` only records colour commands and a depth prepass ([current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L295-L370)). The cascade loop in `RenderGame` draws terrain, the SuperMesh shadow pool, part models and splines, and no static objects:

```csharp
// current/decomp/KSA/Program.cs L4758-L4764
if (GameSettings.ShowVesselInCascadeShadows())
{
	SuperMeshRenderSystem.AddPoolToShadowBucket(num, in _cascadedShadowSystem.CascadeData[num].ViewProj, _cascadedShadowSystem.CurrentSettings.MaxPenumbraWorld);
	SuperMeshRenderSystem.RenderShadowPass(float4x4.Identity, float4x4.Identity, commandBuffer2, num);
	PartModelRenderer.DepthData.AddCommandsCSM(commandBuffer2, RenderedViewport, resourceFrameIndex, num);
	SplineRenderer.DepthData.AddCommandsCSM(commandBuffer2, RenderedViewport, resourceFrameIndex, num);
}
```

Everything a mod can add reaches two consumers through one list, `SuperMeshRenderSystem.ShadowRenderablePool` (a `List<ShadowRenderable>` of bucket handle, centre, radius and `InstanceData`):

- the **sun cascades**: `AddPoolToShadowBucket` culls each entry against the cascade's side planes and draws it into the cascade's `MeshPass.ShadowCsm` bucket;
- the **lamp shadow atlas**: `SuperMeshShadowCasterProvider.PrepareShadowCasters` tests each entry against every casting light's range and draws it into that light's `MeshPass.ShadowAtlas` bucket.

```csharp
// current/decomp/KSA/SuperMeshRenderSystem.cs L524-L539
public void AddPoolToShadowBucket(int cascadeIndex, in float4x4 cascadeViewProj, float casterPadding)
{
	ViewHandle view = ViewForCascade(cascadeIndex);
	PassBucketSystem.Clear(view, MeshPass.ShadowCsm);
	Span<floatPlane> span = stackalloc floatPlane[4];
	ShadowUtils.ComputeCascadeSidePlanes(in cascadeViewProj, span);
	Span<ShadowRenderable> span2 = ShadowRenderablePool.AsSpan();
	for (int i = 0; i < span2.Length; i++)
	{
		ref ShadowRenderable reference = ref span2[i];
		if (!ShadowUtils.IsSphereOutsidePlanes(span, reference.Center, reference.Radius + casterPadding))
		{
			PassBucketSystem.DrawMeshInstance(in reference.BucketHandle, reference.InstanceData, view, MeshPass.ShadowCsm);
		}
	}
}
```

```csharp
// current/decomp/KSA/SuperMeshShadowCasterProvider.cs L131-L157
private static bool IsVisibleToLight(in ShadowRenderable renderable, in Light light)
{
	double3 double5 = double3.Unpack(in renderable.Center);
	float num = light.Range + renderable.Radius + 0.1f;
	double num2 = (light.Position - double5).LengthSquared();
	return num2 <= (double)num * (double)num;
}

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

`StaticMeshRenderable`'s constructor registers every mesh of a glTF asset twice, once for colour and once for `ShadowPassRendererStatic`, which covers both `ShadowCsm` and `ShadowAtlas` ([current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L31-L54), [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L361)). `StaticMeshRenderable.Draw` is what normally fills the pool; here you do it yourself, with the same instance layout, and skip the colour buckets.

The pool is emptied by `ClearBuckets` at the start of every viewport's frame and by `OnFrameEnd` ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L561)), so entries must be added after the main view's clear and before the first consumer.

## Lifecycle and timing

| When | Where | What happens to the pool |
|---|---|---|
| `frame:render`, before recording | `Program.UpdateRenderingResources` reaches `LocationReference.UpdateStaticObjectRenderData` for the main viewport | Casters built once; model matrix computed; a `_fresh` latch is set. Nothing is added yet |
| Start of each viewport | `RenderViewport` / `RenderGame` call `ClearBuckets` | Emptied (secondary viewports first, main view last) |
| `render:shadows` (lamps) | `LightSystem.RenderShadowPass` (RenderGame row 4) then `LightSystemShadowMap.Render` then `PrepareShadowCasters` | **Prefix fires here first** and fills the pool |
| `render:shadows` (sun) | Each cascade in `CascadesToRenderThisFrame`, if `GameSettings.ShowVesselInCascadeShadows()` | Prefix fires again for `AddPoolToShadowBucket`; the latch is already clear |
| `frame:post-render` | `SuperMeshRenderSystem.OnFrameEnd` | Emptied |

See the RenderGame table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence): rows 1a (ClearBuckets), 4 (local-light shadows), 6 (cascades).

- `SuperMeshShadowCasterProvider` does not prepare in the update phase (`IShadowCasterProvider.PreparesInUpdatePhase` defaults to false), so `PrepareShadowCasters` runs inside the shadow render, after the pool is cleared for the main view ([current/decomp/KSA.Rendering.Lighting/IShadowCasterProvider.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/IShadowCasterProvider.cs#L18), [current/decomp/KSA.Rendering.Lighting/LightSystemShadowMap.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightSystemShadowMap.cs#L343-L367)).
- The lamps' atlas is drawn before the sun's cascades and both read the same pool, which is why one prefix is patched onto **both** entry points and a latch makes whichever runs first the one that adds.
- With light shadows off, `ClusteredLightSystem.RenderShadowPass` returns before preparing any caster, so the first cascade's `AddPoolToShadowBucket` adds them instead.
- The renderables are built in the render-data phase on the main thread, never while a pass is recording.

## Steps

1. **Author a caster mesh and declare it.** A low-poly GLB that sits just inside the roof, walls and pillars of the real geometry. Declare it as a `<GltfFile>` that no part or static object references, plus any `<PbrMaterial>` (the renderable reads a material handle per mesh, so one must exist):

   ```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L27-L30 -->
<!-- Never drawn: put in the sun's shadow map so the garage under the ground is dark. -->
<GltfFile Id="KSAOffice_ShadowCaster_Glb">
  <Source Path="Meshes/KSAOffice_ShadowCaster.glb" />
</GltfFile>
```

   ```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L4471-L4475 -->
<PbrMaterial Id="KSAOffice_GarageMaterial">
  <Diffuse Path="Textures/KSAOffice_Garage_Diffuse.png" Category="Vessel" />
  <Normal Path="Textures/KSAOffice_Garage_Normal.png" Category="Vessel" />
  <AoRoughMetal Path="Textures/KSAOffice_Garage_AoRoughMetal.png" Category="Vessel" />
</PbrMaterial>
```

   List the XML in `mod.toml` `assets`.

2. **Build `StaticMeshRenderable`s once**, from the main viewport's render-data hook and outside the editor. The asset-name type is discovered from `LoadedAssetRef.Id` (`Core.AssetName`), the managers are public fields fetched by name, every material slot is overwritten with the caster material before construction, and the protected `MaterialIndices` array is read through `AccessTools.FieldRefAccess`:

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
// src/KSAStructures/Ksa/OfficeUnderground.cs L282-L287
private static object? Load(SuperMeshRenderSystem system, string managerField, object name)
{
    object? manager = system.GetType().GetField(managerField, BindingFlags.Instance | BindingFlags.Public)?.GetValue(system);
    MethodInfo? load = manager?.GetType().GetMethod("GetOrLoad", [AssetNameType!]);
    return load?.Invoke(manager, [name]);
}
```

3. **Compute the model matrix and latch it every frame** from the main viewport only. The building's axes and ego origin become the model matrix; `_fresh` tells the prefix to add once this frame:

   ```csharp
// src/KSAStructures/Ksa/OfficeUnderground.cs L194-L202
    if (!main) return;

    // Built here, where the engine builds its own render data, never while a pass is recording.
    if (Program.Editor is not null || Casters() is null) return;

    _model = float4x4.Pack(new double4x4(up.X, up.Y, up.Z, 0.0, east.X, east.Y, east.Z, 0.0,
                                         north.X, north.Y, north.Z, 0.0, origin.X, origin.Y, origin.Z, 1.0));
    _fresh = true;
}
```

   This call sits in a postfix on `LocationReference.UpdateStaticObjectRenderData` that the mod installs when it places its landmarks ([src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86)).

4. **Install one prefix on both entry points.**

   ```csharp
// src/KSAStructures/Ksa/OfficeUnderground.cs L110-L129
public static void Install(Harmony harmony)
{
    try
    {
        MethodInfo? target = AccessTools.Method(typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.AddPoolToShadowBucket));
        MethodInfo prefix = typeof(Underground).GetMethod(nameof(BeforeShadowBucket), BindingFlags.NonPublic | BindingFlags.Static)!;
        if (target is null) Log.Warn("office: no shadow hook, the sun shines into the garage");
        else harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        // The lamps' shadow maps are drawn before the sun's cascades and read the same pool, so the
        // caster goes in at whichever of the two the frame reaches first.
        MethodInfo? lamps = AccessTools.Method(typeof(SuperMeshShadowCasterProvider), nameof(SuperMeshShadowCasterProvider.PrepareShadowCasters));
        if (lamps is null) Log.Warn("office: no lamp shadow hook, the garage's lamps shine through its pillars");
        else harmony.Patch(lamps, prefix: new HarmonyMethod(prefix));
    }
    catch (Exception e)
    {
        Log.Warn($"office: could not hook the shadow pass ({e.GetBaseException().Message})");
    }
}
```

5. **Append the `ShadowRenderable`s**, latched so they are added once per frame by whichever consumer runs first. The instance layout mirrors `StaticMeshRenderable.Draw`: `data = (materialIndex, 0, 1, 0)`, `model` the caster's matrix, `Center` its translation and `Radius` a bounding sphere (here a fixed 260 m for the whole site):

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

6. **Check the log** for `office: 2 shadow casters built`, and in the game that a sunlit surface under the caster goes dark. If the log says "vessel shadows are off", the player's `VesselCascadeShadows` graphics setting is off and the sun path is disabled.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L204-L287) | `BeforeShadowBucket`, `Casters`, `Load` |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L110-L129) | Both prefixes installed |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L194-L202) | Model matrix and latch |
| [src/KSAStructures/Ksa/Office.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Office.cs#L75-L86) | Render-data postfix and installs |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L27-L30) | Never-drawn `<GltfFile>` asset |

## Variations and gotchas

- **Vessel shadows off.** With `GameSettings.ShowVesselInCascadeShadows()` false ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3298-L3301)) the cascade loop never calls `AddPoolToShadowBucket`; casters then reach only the lamp atlas.
- **Main view only.** Only the main view has sun cascades and a light atlas. Camera windows render static objects with the unshadowed cascade set, so they show the interior sunlit.
- **Fixed radius.** A 260 m sphere for the whole site means per-light culling and cascade-plane culling do little. Give each caster its own centre and radius for large sites.
- **Failure disables it for the session.** The mod latches `_casterFailed` if the material or a mesh fails to load; the sun then reaches the floor. The shadow code swallows every exception, because a sunlit interior is the only failure acceptable inside the engine's frame.
- **Reflection surface.** `MaterialIndices` is protected and the manager fields are found by string; a rename turns the feature off with a log line rather than crashing.
- **Depth only.** This path draws depth through `ShadowPassRendererStatic`; there is no alpha test or colour. KSAStructures' casters are meshes that sit inside the roof, walls and pillars of the real geometry.
- **`StaticMeshRenderable.CastShadows`** already adds a mesh you also draw to the pool (see [draw your own meshes](../draw-mod-meshes-with-the-engine-renderers/overview.md), approach 1); use the approach here only for geometry that is drawn some other way, such as static objects.

## See also

- [Overview](overview.md)
- [Invisible shadow casters](../../../../mods/ksastructures/features/invisible-shadow-casters.md)
- [Draw your own meshes through the engine's mesh renderers](../draw-mod-meshes-with-the-engine-renderers/overview.md)
- Add dynamic spot and point lights to the world from a mod (lights-and-particles group): [overview](../../lights-and-particles/add-dynamic-lights-to-the-world/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
