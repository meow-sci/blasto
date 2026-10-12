---
type: How-To
title: Clone the materials per kitten, swap the MaterialIndices handles and stay inside the 512-slot pool
description: Create new MaterialData records through GpuObjectSystem.CreateObject, overwrite the protected MaterialIndices arrays of one kitten, and budget, reserve and free slots because the pool is fixed
category: kittens
group: appearance
topic: tint-and-restyle-kitten-appearance
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/doh-runtime-gpu-materials]
techniques: [reflection, vulkan-buffer]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapBeforeGui
  - reflection KSA.GpuObjectSystem.CreateObject
  - reflection KSA.AnimatedRenderable.MaterialIndices (protected)
lifecycle: [frame:draw-ui, event:load, frame:prepare, frame:render]
game_types:
  - KSA.GpuObjectSystem
  - KSA.MaterialData
  - KSA.CharacterRenderResources
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.CatFurRenderable
  - KSA.AssetManager
  - KSA.GpuObjectAssetRef
  - KSA.PbrMaterialReference
  - Brutal.Collections.FreeListIndexPool
  - KSA.KittenEva
tags: [kitten, material, gpu-pool, clone, budget, doh]
---
To give each kitten its own colour, the shared material records must not be edited. Instead create new `MaterialData` records (same textures, new `AlbedoColor`) through `GpuObjectSystem.CreateObject` and point one kitten's renderables at them by overwriting the entries of their protected `MaterialIndices` arrays. The catch is that the material pool is fixed at 512 records and never grows: this article therefore also covers the budget (capacity, used slots, a reserve for the game, preflight) and how to return slots by name when a kitten goes away. Prerequisites: StarMap, reflection into the kitten avatar ([reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)), and a way to run code from a UI callback. Demonstrated by Unscience's doh sub-mod: [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md). If you only need one colour for all kittens, use [approach 1](tint-and-restyle-kitten-appearance-1.md); if you want clones bound without a pool-accounting framework, see [approach 3](tint-and-restyle-kitten-appearance-3.md).

## Background: how the game does it

`CreateObject` allocates a slot and uploads before it knows whether the name is new, and the allocator cannot grow (`allowResize: false`). A full pool makes every allocation throw, including the game's own per-kitten fur material in the `KittenEva` constructor:

```csharp
// current/decomp/KSA/GpuObjectSystem.cs L45-L83
	public bool CreateObject(AssetName id, T element)
	{
		int handle = SendToBuffer(element);
		GpuObjectAssetRef gpuObjectAssetRef = new GpuObjectAssetRef(id, this, handle);
		bool flag = TryAdd(gpuObjectAssetRef);
		if (!flag)
		{
			gpuObjectAssetRef.Dispose();
		}
		return flag;
	}

	protected int SendToBuffer(T element)
	{
		int index;
		lock (BigBufferAllocator)
		{
			if (!BigBufferAllocator.TryAdd(out index))
			{
				throw new InvalidOperationException("Failed to allocate handle for GPU object.");
			}
		}
		using StagingPool stagingPool = DeviceCtx.CreateStagingPool();
		CommandBuffer commandBuffer = stagingPool.NextCommandBuffer();
		T reference = element;
		Span<T> span = new Span<T>(ref reference);
		commandBuffer.Begin();
		VkUtils.StageAndUploadToBuffer(stagingPool, BigBuffer.VkBuffer, index * ByteSize.Of<T>(), span.AsBytes(), commandBuffer);
		commandBuffer.End();
		return index;
	}

	public void Free(int handle)
	{
		lock (BigBufferAllocator)
		{
			BigBufferAllocator.Remove(handle);
		}
	}
```

Each renderable draws with integer handles read from its protected `MaterialIndices` array (filled once from the cached glTF material slots), so overwriting array entries re-binds the next draw:

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L104-L119
		MaterialIndices = new int[GltfAssetRef.Meshes.Length];
		GpuObjectAssetRef[] array = customMaterials ?? GltfAssetRef.Materials;
		for (int i = 0; i < ColourMeshBucketHandles.Length; i++)
		{
			IMeshAsset meshAsset = GltfAssetRef.Meshes[i];
			if (meshAsset != null)
			{
				ColourMeshBucketHandles[i] = passBucketSystem.RegisterMesh(meshAsset, colourPassRenderer);
				if (meshPassRenderer != null)
				{
					RaytracedMeshBucketHandles[i] = passBucketSystem.RegisterMesh(meshAsset, meshPassRenderer);
				}
				DepthMeshBucketHandles[i] = passBucketSystem.RegisterMesh(meshAsset, passRenderer);
				MaterialIndices[i] = array[GltfAssetRef.MaterialIndices[i]].Handle;
			}
		}
```

```csharp
// current/decomp/KSA/CatFurRenderable.cs L64
			MaterialIndices[i] = material?.Handle ?? GltfAssetRef.Materials[GltfAssetRef.MaterialIndices[subMeshTargets[i]]].Handle;
```

The fur material is not a plain PBR record: `CharacterRenderResources.CreateFurMaterial` fills `ExtraData` with the fur texture, sampler and mask handles, and a clone must copy that recipe:

```csharp
// current/decomp/KSA/CharacterRenderResources.cs L54-L71
	public GpuObjectAssetRef CreateFurMaterial(PbrMaterialReference? headMaterial, GpuTextureAssetRef furMaskTexture)
	{
		CatFurMaskTexture = furMaskTexture;
		AssetName assetName = $"{headMaterial.Get().DiffuseReference.Get().Id},{headMaterial.Get().NormalReference.Get().Id},{CatFurMaskTexture.Id}_FurMaterial";
		MeshRenderSystem.MaterialSystem.CreateObject(assetName, new MaterialData
		{
			ExtraData = new float4(FurTexture.BindlessHandle, FurSampler.BindlessIndex, CatFurMaskTexture.BindlessHandle, 0f),
			NormalTexture = headMaterial.Get().NormalReference.Get().BindlessHandle,
			RoughnessMetalScale = float4.One,
			AlbedoTexture = headMaterial.Get().DiffuseReference.Get().BindlessHandle,
			RoughMetallicAOTexture = MeshRenderSystem.GltfSystem.BlankMaterialTexture.BindlessHandle,
			AlbedoColor = float4.One,
			Sampler = MeshRenderSystem.TextureSystem.SamplerRepeatHandle,
			EmissiveTexture = MeshRenderSystem.TextureSystem.DefaultBlackTexture.BindlessHandle
		});
		return MeshRenderSystem.MaterialSystem.GetOrLoad(assetName);
	}
```

## Lifecycle and timing

| Work | Hook | Lifecycle |
|---|---|---|
| Clone on spawn, live tint, per-material colour, release on despawn | ImGui callback from `[StarMapAfterGui]` | `frame:draw-ui` |
| Prune sets of kittens the game destroyed | `[StarMapBeforeGui]` update | `frame:draw-ui` |
| Rebind after a load | replayed at the `PrepareFrame` handoff | `event:load` |
| Release everything | `[StarMapUnload]` | unload |

Each `CreateObject` and `WriteAlbedoColor` is a blocking staging transfer on the main queue, done before this frame's command recording, so the record is in the buffer before this frame's passes read it. Swapping `MaterialIndices` entries is safe from the UI pass because those arrays are read when the kitten records its draws in `RenderGame`, later in the same frame. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Reach the avatar and its `MaterialIndices` arrays.** `KittenEva._renderable` then `KittenRenderable._characterAvatar` (both private), then the five renderables: `Core.CharacterModel`, `Fur.CatFurRenderable`, `Attachments.Helmet.HelmetMesh`, `Attachments.Helmet.VisorMesh`, `Attachments.Mmu.MmuMesh`. `Core`, `Fur`, `Attachments`, `Helmet` and `Mmu` are structs, so each reflection read boxes a copy, but the final `int[]` is a reference to the live renderable's array, so writes into it reach the game:

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L167-L199
    private static object? GetCharacterAvatar(KittenEva kittenEva)
    {
        var renderableField = typeof(KittenEva).GetField("_renderable",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (renderableField == null) return null;

        var renderable = renderableField.GetValue(kittenEva);
        if (renderable == null) return null;

        var avatarField = renderable.GetType().GetField("_characterAvatar",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return avatarField?.GetValue(renderable);
    }

    /// <summary>
    /// Navigates a chain of fields on an object and extracts MaterialIndices from the final renderable.
    /// E.g., GetMaterialIndicesFromPath(avatar, "Core", "CharacterModel") navigates
    /// avatar.Core.CharacterModel.MaterialIndices.
    /// </summary>
    private static int[]? GetMaterialIndicesFromPath(object root, params string[] fieldPath)
    {
        object? current = root;
        foreach (string fieldName in fieldPath)
        {
            if (current == null) return null;
            current = GetFieldValue(current, fieldName);
        }

        if (current == null) return null;

        // Get MaterialIndices from the final renderable
        var matField = FindFieldInHierarchy(current.GetType(), "MaterialIndices");
        return matField?.GetValue(current) as int[];
```

2. **Collect the arrays and separate the fur.** Fur needs its shader's `ExtraData`, so its handles are cloned separately:

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L38-L74
            // 1. CharacterModel (body/head/eyes)
            var charModelIndices = GetMaterialIndicesFromPath(avatar, "Core", "CharacterModel");
            if (charModelIndices != null)
                renderables.Add(("CharacterModel", charModelIndices));

            // 2. Fur — collected but cloned separately (needs special ExtraData for fur shader)
            var furIndices = GetMaterialIndicesFromPath(avatar, "Fur", "CatFurRenderable");
            if (furIndices != null)
                renderables.Add(("Fur", furIndices));

            // 3. Helmet
            var helmetIndices = GetMaterialIndicesFromPath(avatar, "Attachments", "Helmet", "HelmetMesh");
            if (helmetIndices != null)
                renderables.Add(("Helmet", helmetIndices));

            // 4. Visor
            var visorIndices = GetMaterialIndicesFromPath(avatar, "Attachments", "Helmet", "VisorMesh");
            if (visorIndices != null)
                renderables.Add(("Visor", visorIndices));

            // 5. MMU
            var mmuIndices = GetMaterialIndicesFromPath(avatar, "Attachments", "Mmu", "MmuMesh");
            if (mmuIndices != null)
                renderables.Add(("MMU", mmuIndices));

            if (renderables.Count == 0)
            {
                Console.WriteLine("doh: No MaterialIndices found on any renderable.");
                return null;
            }

            // Separate fur handles — fur shader needs ExtraData with fur texture handles
            var furHandleSet = furIndices != null ? new HashSet<int>(furIndices) : new HashSet<int>();
            var nonFurHandles = renderables
                .Where(r => r.name != "Fur")
                .SelectMany(r => r.indices)
                .ToArray();
```

3. **Reflect the pool.** Read `Program.Instance.MaterialSystem`, its protected `AssetMap`, `DeviceCtx` and `BigBufferAllocator`, and the `CreateObject` / `GetOrLoad` methods (`CreateObject` is public; doh reaches it by reflection because the `AssetName` argument type lives in an assembly the mod does not reference). The pattern is the one in [approach 1](tint-and-restyle-kitten-appearance-1.md), step 1; see [doh.lib/Materials/MaterialSystemAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.cs#L46).

4. **Budget before cloning.** Capacity is `BigBufferAllocator.Capacity` (fallback 512). Used slots are the distinct valid handles in `AssetMap`. Never allocate into a reserve the game needs, and refuse a spawn whose estimated sets would not fit:

```csharp
// doh.lib/Materials/MaterialSystemAccessor.Pool.cs L23-L26
    public const int ReservedSlots = 64;

    /// <summary>Planning estimate for one kitten's cloned set (stock kittens: 8 non-fur + 1 fur).</summary>
    public const int EstimatedSlotsPerSet = 10;
```

```csharp
// doh.lib/Materials/MaterialSystemAccessor.Pool.cs L54-L74
    public static int GetFreeSlotCount()
    {
        int capacity = GetCapacity();
        if (capacity < 0 || _assetMap == null) return -1;

        try
        {
            var used = new HashSet<int>();
            foreach (DictionaryEntry entry in _assetMap)
            {
                if (entry.Value is GpuObjectAssetRef asset && asset.IsValid && asset.Handle >= 0)
                    used.Add(asset.Handle);
            }
            return Math.Max(0, capacity - used.Count);
        }
        catch (Exception ex)
        {
            _lastError = $"GetFreeSlotCount error: {ex.Message}";
            return -1;
        }
    }
```

```csharp
// doh.lib/Spawning/KittenSpawner.cs L273-L285
    private static string? CheckMaterialBudget(int setsNeeded)
    {
        if (!MaterialSystemAccessor.IsInitialized && !MaterialSystemAccessor.Initialize())
            return setsNeeded > 0 ? $"MaterialSystem unavailable: {MaterialSystemAccessor.LastError}" : null;

        int available = MaterialSystemAccessor.GetAvailableSlots();
        int needed = setsNeeded * MaterialSystemAccessor.EstimatedSlotsPerSet;
        if (available < 0 || available >= needed) return null;

        return $"Not enough GPU material slots: ~{needed} needed for {setsNeeded} tinted set(s), {available} available " +
            $"({MaterialSystemAccessor.ReservedSlots} kept for the game). Despawn tinted kittens, turn off " +
            "'Unique Each', or spawn fewer.";
    }
```

5. **Clone through `CreateObject`.** Build a new `MaterialData` from the source `PbrMaterialReference` (textures resolved to bindless handles), with `AlbedoColor` = the tint and `RoughnessMetalScale = 1`, `ExtraData = 0`. Gate the call on the free count:

```csharp
// doh.lib/Materials/MaterialFactory.cs L241-L264
    private int CreateMaterialFromPbrRef(string name, PbrMaterialReference pbrRef, float4 tintColor,
        int samplerHandle, int defaultWhiteHandle, int defaultBlackHandle)
    {
        int albedoTex = GetTextureBindlessHandle(pbrRef.DiffuseReference, defaultWhiteHandle);
        int normalTex = GetTextureBindlessHandle(pbrRef.NormalReference, defaultWhiteHandle);
        int pbrTex = GetTextureBindlessHandle(pbrRef.PBRMap, defaultWhiteHandle);
        int emissiveTex = GetTextureBindlessHandle(pbrRef.EmissiveMap, defaultBlackHandle);

        var matData = new MaterialData
        {
            AlbedoTexture = albedoTex,
            NormalTexture = normalTex,
            RoughMetallicAOTexture = pbrTex,
            Sampler = samplerHandle,
            AlbedoColor = tintColor,
            RoughnessMetalScale = float4.One,
            EmissiveTexture = emissiveTex,
            ExtraData = float4.Zero
        };

        if (!MaterialSystemAccessor.CreateMaterial(name, matData))
            return -1;
        return MaterialSystemAccessor.GetMaterialHandle(name);
    }
```

```csharp
// doh.lib/Materials/MaterialSystemAccessor.cs L117-L152
    public static bool CreateMaterial(string assetName, MaterialData data)
    {
        if (_materialSystem == null || _createObjectMethod == null)
        {
            _lastError = "MaterialSystem or CreateObject not available.";
            return false;
        }

        // Never let doh consume the slots the game itself needs. The pool is fixed-size and an
        // exhausted pool makes the game's own allocations (e.g. every KittenEva's fur material) throw.
        int freeSlots = GetFreeSlotCount();
        if (freeSlots >= 0 && freeSlots <= ReservedSlots)
        {
            _lastError = $"GPU material pool nearly full ({freeSlots} of {GetCapacity()} slots free, " +
                $"{ReservedSlots} reserved for the game); not creating '{assetName}'.";
            Console.WriteLine($"doh: {_lastError}");
            return false;
        }

        try
        {
            var result = _createObjectMethod.Invoke(_materialSystem, new object[] { (AssetName)assetName, data });
            bool success = result is bool b && b;
            if (success)
                MeowSci.KsaAbstractions.Persistence.MaterialColorState.Record(GetExistingMaterialHandle(assetName), data.AlbedoColor);
            if (!success)
                _lastError = $"CreateObject returned false for '{assetName}' — name may already exist.";
            return success;
        }
        catch (Exception ex)
        {
            _lastError = $"CreateMaterial error for '{assetName}': {ex.InnerException?.Message ?? ex.Message}";
            Console.WriteLine($"doh: {_lastError}");
            return false;
        }
    }
```

6. **Clone a whole kitten.** Build a handle-to-name map from `AssetMap`, clone each distinct handle as `<prefix>_m<idx>`, and record the old-to-new mapping. A material that is not a `PbrMaterialReference` (a glTF inline material) falls back to a white-texture material that only carries the tint:

```csharp
// doh.lib/Materials/MaterialFactory.cs L143-L167
            var uniqueHandles = materialIndices.Distinct().ToArray();
            var handleMap = new Dictionary<int, int>(); // old handle → new handle
            var allNewHandles = new List<int>();
            var assetNames = new List<string>();

            for (int idx = 0; idx < uniqueHandles.Length; idx++)
            {
                int oldHandle = uniqueHandles[idx];
                if (oldHandle < 0) continue;

                string cloneName = $"{prefix}_m{idx}";
                int newHandle = CloneSingleMaterial(oldHandle, cloneName, tintColor,
                    handleToName, samplerHandle, defaultWhiteHandle, defaultBlackHandle);

                if (newHandle >= 0)
                {
                    handleMap[oldHandle] = newHandle;
                    allNewHandles.Add(newHandle);
                    assetNames.Add(cloneName);
                }
                else
                {
                    Console.WriteLine($"doh: Failed to clone handle {oldHandle}, keeping shared.");
                }
            }
```

7. **Swap the live handles.** Reuse a set if its source handles still match, otherwise clone; then overwrite every `MaterialIndices` entry that appears in the map:

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L83-L88
            bool canReuse = reusable != null && !reusable.IsReleased
                && nonFurHandles.Where(h => h >= 0).All(reusable.HandleMap.ContainsKey);
            var matSet = canReuse ? reusable : _materialFactory.CloneAllMaterials(nonFurHandles, tintColor);
            if (matSet == null) return null;
            if (canReuse) matSet.UpdateTint(tintColor);
            else createdSet = matSet;
```

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L119-L136
            // Replace entries in each renderable's MaterialIndices using HandleMap
            indicesSwapped = true;
            int totalReplacements = 0;
            foreach (var (name, indices) in renderables)
            {
                int replaced = 0;
                for (int i = 0; i < indices.Length; i++)
                {
                    if (matSet.HandleMap.TryGetValue(indices[i], out int newHandle))
                    {
                        indices[i] = newHandle;
                        replaced++;
                    }
                }
                if (replaced > 0)
                    Console.WriteLine($"doh:   {name}: {replaced}/{indices.Length} slots replaced");
                totalReplacements += replaced;
            }
```

8. **Recolour live.** `UpdateTint` and per-material colour calls reuse the staged `float4` write from approach 1, recorded in the shared `MaterialColorState` so saves capture the colour actually on the GPU (see [doh.lib/Materials/MaterialSystemAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.cs#L289)).

9. **Return slots by name.** Remove the name from `AssetMap` first (so nothing can resolve it to a slot about to be reused), forget its recorded colour, then dispose the asset ref, which calls `GpuObjectSystem.Free(handle)`. Skip set prefixes whose names still exist (left by an earlier mod instance) because `CreateObject` rejects a taken name:

```csharp
// doh.lib/Materials/MaterialSystemAccessor.Pool.cs L87-L111
    public static bool DestroyMaterial(string assetName)
    {
        if (_assetMap == null) return false;

        try
        {
            var key = (AssetName)assetName;
            if (!_assetMap.Contains(key)) return false;

            var asset = _assetMap[key] as GpuObjectAssetRef;
            // Unmap first so nothing can resolve the name to a slot that is about to be reused.
            _assetMap.Remove(key);
            if (asset == null) return false;

            MaterialColorState.Forget(asset.Handle);
            asset.Dispose(); // GpuObjectAssetRef.Destroy -> GpuObjectSystem.Free(handle)
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"DestroyMaterial error for '{assetName}': {ex.Message}";
            Console.WriteLine($"doh: {_lastError}");
            return false;
        }
    }
```

```csharp
// doh.lib/Materials/MaterialFactory.Lifetime.cs L48-L57
    private string NextPrefix()
    {
        string prefix;
        do
        {
            prefix = $"doh_{_nextMaterialId++:D4}";
        } while (MaterialSystemAccessor.GetExistingMaterialHandle($"{prefix}_m0") >= 0
            || MaterialSystemAccessor.GetExistingMaterialHandle(FurAssetName(prefix)) >= 0);
        return prefix;
    }
```

10. **Check the result.** Spawn two kittens with different tints (the doh panel shows how many of the 512 slots are used); each wears its own colour, despawning one returns its slots, and the log reports `Cloned N/M materials` and `Released material set`.

## Complete example

- [doh.lib/Materials/MaterialSystemAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.cs), [doh.lib/Materials/MaterialSystemAccessor.Pool.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.Pool.cs) - pool accounting, `CreateMaterial`, `DestroyMaterial`.
- [doh.lib/Materials/MaterialFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialFactory.cs) - `CloneAllMaterials`, fur clone, PBR reference resolution.
- [doh.lib/Spawning/KittenSpawner.Materials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Materials.cs) - avatar walk and `MaterialIndices` swap; [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs) - budget preflight.
- Feature page: [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md).

## Variations and gotchas

- **The pool is shared and fixed.** `Program` still creates `GpuMaterialSystem` with 512 slots. Before the budget code, roughly 50 tinted spawns filled it and the next `KittenEva` constructor threw inside `CreateFurMaterial`, crashing the game. A mod that creates materials at runtime must budget and free them.
- **Even a duplicate name costs a slot momentarily.** `CreateObject` uploads before `TryAdd`, so with zero free slots the game's own fur-material call for an existing name still throws; doh wants two free slots per kitten rather than one.
- **The free count is an estimate.** It assumes every live allocation is a named `AssetMap` entry; the 64-slot reserve absorbs drift.
- **Clones are reconstructions, not copies.** `RoughnessMetalScale` is forced to 1 and `ExtraData` to 0, and glTF inline materials lose their textures. Cosmetic attachments are not collected, so they keep shared materials.
- **Fur mask source (observation).** `CharacterRenderResources.CatFurMaskTexture` is a single field that every `CreateFurMaterial` call overwrites; after a load that rebinds several characters, a fur clone may pick up the last-built character's mask (static reading, not checked in game).
- **A released set is never reused,** even if its handles look right; its slots may belong to other materials by now.
- **Never persist handles or clone names.** Slots are recycled after `Free`. After a load, see [restore kitten customizations after a load](../../characters/restore-kitten-customizations-after-a-load/overview.md).
- **Cross-effects.** Approach 1's "Apply to All" writes every `AssetMap` entry, including doh clones.

## See also

- [Topic overview](overview.md); siblings: [approach 1](tint-and-restyle-kitten-appearance-1.md), [approach 3](tint-and-restyle-kitten-appearance-3.md), [approach 4](tint-and-restyle-kitten-appearance-4.md), [approach 5](tint-and-restyle-kitten-appearance-5.md)
- Mod features: [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md), [Kitten Color](../../../../mods/unscience/features/humble-arteest-kitten-color.md)
- [Restore kitten customizations after a load](../../characters/restore-kitten-customizations-after-a-load/overview.md), [Spawn an EVA kitten from code](../../characters/spawn-an-eva-kitten-from-code/overview.md), [Grow game GPU buffers for runtime content](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
