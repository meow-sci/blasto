---
type: Mod Feature
title: Per-kitten tints by cloning GPU materials inside the fixed 512-slot material pool
description: doh clones each kitten's materials into new GpuMaterialSystem slots with a custom AlbedoColor, swaps MaterialIndices to the clones, and budgets and frees slots in the fixed 512-slot pool
mod: unscience
submod: doh
category: kittens
techniques: [reflection, vulkan-buffer, save-sidecar, imgui-window]
hooks:
  - starmap StarMapAfterGui (spawn, recolour and despawn from DohSubmod.RenderContent)
  - starmap StarMapBeforeGui (DohSubmod.Update prunes kittens and releases stale sets)
  - reflection KSA.Program.MaterialSystem, KSA.Program.SuperMeshRenderSystem, KSA.Program.CharacterRenderSystem
  - reflection KSA.AssetManager.AssetMap (protected), GetOrLoad
  - reflection KSA.GpuObjectSystem.CreateObject, DeviceCtx (protected), BigBufferAllocator (protected)
  - reflection KSA.KittenEva._renderable -> KSA.KittenRenderable._characterAvatar (private)
  - reflection KSA.AnimatedRenderable.MaterialIndices / StaticMeshRenderable.MaterialIndices / CatFurRenderable.MaterialIndices (protected)
  - reflection KSA.CharacterRenderSystem._resources
  - unscience save participant doh (order 60) via the shared native save hooks
lifecycle: [frame:draw-ui, event:save, event:load, frame:prepare]
game_types:
  - KSA.Program
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
  - KSA.AssetManager
  - KSA.GpuObjectAssetRef
  - KSA.LoadedAssetRef
  - KSA.MaterialData
  - Brutal.Collections.FreeListIndexPool
  - KSA.GpuTextureSystem
  - KSA.GltfPbrSystem
  - KSA.SuperMeshRenderSystem
  - KSA.CharacterRenderSystem
  - KSA.CharacterRenderResources
  - KSA.CharacterAvatar
  - KSA.CharacterReference
  - KSA.CharacterTexturesReference
  - KSA.PbrMaterialReference
  - KSA.ModLibrary
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.StaticMeshRenderable
  - KSA.CatFurRenderable
  - RenderCore.VkUtils
  - Brutal.VulkanApi.Abstractions.StagingPool
tags: [kittens, materials, gpu, vulkan, bindless, material-pool, tint, reflection, scene-saves]
---

doh can give each spawned kitten its own colour: body, head, fur, helmet, visor and MMU are tinted
together, or each material separately from the kitten list, live. A tinted batch shares one
material set unless "Unique Each" is on, and "I'm Feeling Lucky" gives every kitten a different
named XKCD colour. Despawning a kitten gives its GPU slots back; the panel shows how many of the 512
material slots are used and how many more tinted sets fit.[^doh-lib-readme][^doh-submod]

Mechanism in one line: every kitten renderable draws with integer material handles stored in its
protected `MaterialIndices` array, so doh creates new `MaterialData` records (same textures, new
`AlbedoColor`) through `GpuObjectSystem.CreateObject` (public in build 5554; doh reaches it by reflection only because its `AssetName` parameter type lives in an assembly the mod does not reference), then overwrites the array entries
with the new handles; because the pool is fixed-size and never grows, it keeps a reserve for the game
and frees its clones by name when no kitten uses them.[^kitten-spawner-materials][^ksa-gpuobjectsystem]

## How it works

**Reflection bridge** ([doh.lib/Materials/MaterialSystemAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.cs#L46-L111)).
On the first spawn (the budget check calls it) or the "Init Now" button, `Initialize` reads `Program.Instance`, then
`MaterialSystem`, walks the type hierarchy for the protected `AssetMap`, `DeviceCtx` and
`BigBufferAllocator` and the `CreateObject` / `GetOrLoad` methods, and finds
`SuperMeshRenderSystem.TextureSystem` for texture lookups. The pattern (and the staged colour write)
is the one documented for [Kitten Color](./humble-arteest-kitten-color.md).[^material-accessor]

**Cloning a kitten** ([doh.lib/Spawning/KittenSpawner.Materials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Materials.cs#L20-L164)).

1. Reach the avatar: `KittenEva._renderable` then `KittenRenderable._characterAvatar` (both
   private fields).
2. Collect the `MaterialIndices` arrays of five renderables by walking field names:
   `Core.CharacterModel`, `Fur.CatFurRenderable`, `Attachments.Helmet.HelmetMesh`,
   `Attachments.Helmet.VisorMesh`, `Attachments.Mmu.MmuMesh`. `Core`, `Fur`, `Attachments`, `Helmet`
   and `Mmu` are structs, so each reflection read boxes a copy, but the final `int[]` is a reference
   to the live renderable's array, so writes into it reach the game.[^ksa-characteravatar][^ksa-animatedrenderable]
3. Non-fur handles go to `MaterialFactory.CloneAllMaterials`: build a handle-to-name map from
   `AssetMap`, then for each distinct handle look the name up as a `PbrMaterialReference` in
   `ModLibrary`, resolve its `DiffuseReference`, `NormalReference`, `PBRMap` and `EmissiveMap` texture
   references to bindless handles, and create `doh_NNNN_mK` with those textures, `AlbedoColor = tint`,
   `RoughnessMetalScale = 1` and `ExtraData = 0`. Names that are not `PbrMaterialReference`s (glTF
   inline materials) get a white-texture material that only carries the tint.[^material-factory]
4. Fur needs the fur shader's `ExtraData`: `CreateClonedFurMaterial` reads `FurTexture`,
   `FurSampler` and `CatFurMaskTexture` from `CharacterRenderSystem._resources` and the character's
   head textures, mirroring `CharacterRenderResources.CreateFurMaterial`, and names it
   `doh_NNNN_fur`.[^material-factory][^ksa-characterrenderresources]
5. Every entry of every collected array that appears in `HandleMap` is replaced in place. Material
   entries are tagged with their source renderable for the per-material colour table.

**Live recolour.** `KittenMaterialSet.UpdateTint` and `MaterialEntry.ApplyColor` call
`WriteAlbedoColor`, a one-`float4` staged upload at `handle * sizeof(MaterialData) + 16` into
`BigBuffer`. Each successful create or write is recorded in the shared `MaterialColorState`, so
saves capture the colour actually on the GPU, including edits Humble Arteest made to doh's
slots.[^material-accessor][^kitten-material-set][^material-color-state]

**Pool budget** ([doh.lib/Materials/MaterialSystemAccessor.Pool.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.Pool.cs)).

| Rule | Where |
|---|---|
| Capacity = `BigBufferAllocator.Capacity` (fallback 512) | `GetCapacity` |
| Used slots = distinct valid handles in `AssetMap`; free = capacity minus used | `GetFreeSlotCount` |
| `ReservedSlots = 64` are never taken by doh; `CreateMaterial` refuses at or below it | `CreateMaterial` |
| Preflight: `sets needed * EstimatedSlotsPerSet (10)` must fit above the reserve, where a non-unique batch needs one set | `KittenSpawner.CheckMaterialBudget` |
| At least 2 free slots before each `new KittenEva` (its avatar allocates a fur material) | `KittenSpawner.SpawnInternal` |
| A set already cloned for this batch, or a detached set after a load, is reused when its source handles still match; a released set never is | `ApplyClonedMaterials` |
| Release: remove the name from `AssetMap` first, forget its colour, then `Dispose` the asset ref, which calls `GpuObjectSystem.Free(handle)` | `DestroyMaterial`, `MaterialFactory.Release` |
| A set is released when its last kitten is despawned or pruned, on unload, and for detached sets no restored kitten claimed | `ReleaseIfUnused`, `Dispose`, `ReleaseStaleSavedMaterials` |
| A set created for a kitten whose cloning then failed is released before any index was swapped | `ApplyClonedMaterials` catch |

New set prefixes skip names still present in the pool (left by an earlier mod instance), because
`CreateObject` rejects a taken name.[^material-factory-lifetime]

**Scene saves** ([doh.lib/DohSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.Persistence.cs)).
Participant `doh`, order 60. The record holds the spawn controls and, per live tracked kitten, the
native vehicle id, character, tint, an optional `MaterialGroup` (the set id it rendered with) and
per-material colours keyed by source renderable and source material name. Handles and the
`doh_NNNN` asset names are never used as identities.[^doh-persistence]

- **Reset** keeps every live set in a cache keyed by kitten id and character, clears the registry and
  the controls, and never despawns: the native load destroys the old kittens.
- **Restore** finds each natively recreated `KittenEva` by id, picks a reusable set (its save group's
  set first, otherwise its own cached set if not released and not already claimed), runs the normal
  clone-or-reuse path, reapplies each saved per-material colour, and registers the kitten. Kittens
  that were unique in the save are never merged; saves without `MaterialGroup` (older builds) get one
  set per kitten. Missing kittens or materials become warnings.
- Cached sets nobody claimed are released after restore, or on the next `Update` when a load had no
  doh record at all.
- Validation rejects blank ids, duplicate kitten ids, duplicate (source, name) materials and counts
  above 10000 before the world is reset.

## Lifecycle and timing

| Work | Hook | Phase |
|---|---|---|
| Clone on spawn, `UpdateTint`, per-material colour, despawn release | `DohSubmod.RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Prune released sets of kittens the game destroyed; release stale detached sets | `DohSubmod.Update` from `[StarMapBeforeGui]` (or the hidden-HUD replay) | `frame:draw-ui` |
| Capture | Native save capture, synchronous where the save is made | `event:save` |
| Reset and restore | Native load, replayed at the `PrepareFrame` handoff after KSA rebuilt the world | `event:load`, `frame:prepare` |
| Release everything | `ISubmod.Dispose` on `[StarMapUnload]` | unload; at game exit this runs after `Program.Dispose` has disposed the material system and the Vulkan device, so the release only matters for development hosts |

Each `CreateObject` and `WriteAlbedoColor` is a blocking staging transfer on the main queue
(`StagingPool.Dispose` submits and waits), done on the main thread before this frame's command
recording in `Program.Render`, so the new record is in the buffer before this frame's passes read
it; frames already in flight may still show the old colour.[^ksa-gpuobjectsystem][^ksa-vkutils] Swapping
`MaterialIndices` entries is safe from the UI pass because those arrays are read when the kitten
records its draws in `RenderGame`, later in the same frame.

## Key code

| Path | Role |
|---|---|
| [doh.lib/Materials/MaterialSystemAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.cs#L117-L152) | `CreateMaterial` with the reserve gate |
| [doh.lib/Materials/MaterialSystemAccessor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.cs#L289-L325) | `WriteAlbedoColor` staged upload |
| [doh.lib/Materials/MaterialSystemAccessor.Pool.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialSystemAccessor.Pool.cs#L23-L111) | Reserve, estimate, capacity, free count, `DestroyMaterial` |
| [doh.lib/Materials/MaterialFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialFactory.cs#L122-L264) | `CloneAllMaterials`, `CloneSingleMaterial`, `CreateMaterialFromPbrRef` |
| [doh.lib/Materials/MaterialFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialFactory.cs#L345-L533) | Fur clone, character texture and fur resource resolution |
| [doh.lib/Materials/MaterialFactory.Lifetime.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/MaterialFactory.Lifetime.cs) | `Release`, `Cleanup`, collision-free prefixes |
| [doh.lib/Materials/KittenMaterialSet.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Materials/KittenMaterialSet.cs) | Handle map, owned asset names, `UpdateTint`, `IsReleased` |
| [doh.lib/Spawning/KittenSpawner.Materials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Materials.cs) | Avatar walk, reuse decision, index swap |
| [doh.lib/Spawning/KittenSpawner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.cs#L273-L291) | Budget preflight and `ReleaseIfUnused` |
| [doh.lib/DohSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.cs#L126-L140) | Pool status line |
| [doh.lib/DohSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.Persistence.cs) | Save participant, detached-set cache and rebinding |
| [ksa-abstractions.lib/Persistence/MaterialColorState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/MaterialColorState.cs) | Shared record of mod-written albedo colours |
| [doh.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.tests/README.md) | Managed checks for groups, reuse, release and legacy records |

## Snippets

Returning a slot: unmap the name first, then let the asset ref free the handle:

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

Reuse or clone, then swap the live handles:

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L83-L136
            bool canReuse = reusable != null && !reusable.IsReleased
                && nonFurHandles.Where(h => h >= 0).All(reusable.HandleMap.ContainsKey);
            var matSet = canReuse ? reusable : _materialFactory.CloneAllMaterials(nonFurHandles, tintColor);
            if (matSet == null) return null;
            if (canReuse) matSet.UpdateTint(tintColor);
            else createdSet = matSet;
            // ...
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

Why a full pool breaks the game itself: `CreateObject` allocates a slot before it knows whether the
name is new, and the allocator cannot grow:

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

After a load: shared group first, then the kitten's own detached set, then release the rest:

```csharp
// doh.lib/DohSubmod.Persistence.cs L121-L144
    private KittenMaterialSet? FindReusableSet(SavedKitten item,
        Dictionary<string, KittenMaterialSet> groupSets, HashSet<KittenMaterialSet> claimed)
    {
        if (item.MaterialGroup != null && groupSets.TryGetValue(item.MaterialGroup, out var shared))
            return shared;
        return _savedMaterialCache.TryGetValue(CacheKey(item.VehicleId, item.CharacterId), out var cached)
            && !cached.IsReleased && !claimed.Contains(cached) ? cached : null;
    }

    /// <summary>
    /// Returns detached material sets that no restored kitten rebound to the fixed-size GPU pool.
    /// Runs after restore, and from Update to cover loads/new scenes with no DOH record (reset
    /// without restore). Their kittens were destroyed by the native load, so nothing renders them.
    /// </summary>
    private void ReleaseStaleSavedMaterials()
    {
        if (_savedMaterialCache.Count == 0) return;
        foreach (var set in _savedMaterialCache.Values.Distinct().ToList())
        {
            if (_registry == null || !_registry.IsMaterialSetInUse(set))
                _materialFactory?.Release(set);
        }
        _savedMaterialCache.Clear();
    }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Program.Instance`, `MaterialSystem`, `SuperMeshRenderSystem`, `CharacterRenderSystem` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1002) | Material system created with capacity 512 (field at L119) |
| `GpuObjectSystem<T>.CreateObject`, `SendToBuffer`, `Free`, `BigBuffer`, `DeviceCtx`, `BigBufferAllocator` | [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L12-L83) | Allocation, upload, release; `allowResize: false` at L24 |
| `FreeListIndexPool.Capacity`, `TryAdd` | [current/decomp/Brutal.Collections/FreeListIndexPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Collections/FreeListIndexPool.cs#L17-L79) | Pool size and the full-pool failure |
| `AssetManager<T>.AssetMap` (protected), `GetOrLoad` | [current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs#L11-L51) | Name to asset map; handle lookup |
| `GpuObjectAssetRef.Handle`, `Destroy` | [current/decomp/KSA/GpuObjectAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectAssetRef.cs#L7-L24) | Slot index; frees on dispose |
| `LoadedAssetRef.Dispose`, `IsValid` | [current/decomp/KSA/LoadedAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LoadedAssetRef.cs#L13-L28) | Idempotent dispose; validity in the free count |
| `MaterialData` | [current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs#L6-L30) | 80-byte record, `AlbedoColor` at offset 16 |
| `CharacterRenderResources.CreateFurMaterial`, `FurTexture`, `FurSampler`, `CatFurMaskTexture` | [current/decomp/KSA/CharacterRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterRenderResources.cs#L30-L70) | Fur `ExtraData` recipe being copied |
| `CharacterAvatar` `Core`, `Fur`, `Attachments` and the fur material call | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L394) | Field path to the renderables; per-kitten fur allocation |
| `AnimatedRenderable.MaterialIndices` (protected) | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L32) | Handles swapped in place (filled at L104-L117) |
| `CatFurRenderable.MaterialIndices` (protected) | [current/decomp/KSA/CatFurRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatFurRenderable.cs#L22) | Fur handle swap |
| `PbrMaterialReference.DiffuseReference`, `NormalReference`, `PBRMap`, `EmissiveMap` | [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L13-L22) | Textures of the source material |
| `CharacterTexturesReference.CharacterBodyMaterial`, `CharacterHeadMaterial`, `CharacterEyeMaterial` | [current/decomp/KSA/CharacterTexturesReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterTexturesReference.cs#L8-L15) | Head textures for the fur clone |
| `GpuTextureSystem.SamplerRepeatHandle`, `DefaultWhiteTexture`, `DefaultBlackTexture` | [current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L26-L34) | Fallback bindless handles |
| `VkUtils.StageAndUploadToBuffer` | [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L82-L100) | Colour write into `BigBuffer` |

## Pitfalls and version notes

- **The pool is shared and fixed.** In 5554 `Program` still creates `GpuMaterialSystem` with 512
  slots and `allowResize: false`. Before the budget code, roughly 50 tinted spawns filled it; the next
  `KittenEva` constructor then threw inside `CreateFurMaterial` with the kitten already registered and
  the game crashed. A mod that creates materials at runtime must budget and free them.[^doh-lib-readme][^ksa-program]
- **Even a duplicate name costs a slot momentarily.** `CreateObject` uploads before `TryAdd`, so with
  zero free slots the game's own fur-material call for an existing name still throws. That is why doh
  wants two free slots per kitten rather than one.[^ksa-gpuobjectsystem]
- **The free count is an estimate.** It assumes every live allocation is a named `AssetMap` entry; an
  unnamed allocation path would make it optimistic, and the 64-slot reserve absorbs small
  drift.[^scope-character]
- **Clones are reconstructions, not copies.** `RoughnessMetalScale` is forced to 1 and `ExtraData`
  to 0, and glTF inline materials lose their textures (white plus tint). Cosmetic attachments
  (`Attachments.CosmeticAttachments`) are not collected, so they keep their shared materials.
- **Fur mask source (observation).** `CharacterRenderResources.CatFurMaskTexture` is a single field
  that every `CreateFurMaterial` call overwrites. At spawn time it holds the mask of the kitten just
  built; when a load rebinds several characters, the fur clone may pick up the last-built
  character's mask instead (static reading, not checked in game).[^ksa-characterrenderresources]
- **Never persist handles or clone names.** Slots are recycled after `Free`; the save keys materials
  by (source renderable, source material name) and groups by a set id that only links kittens within
  one record. `MaterialColorState.Forget` runs before a slot can be reused.[^material-color-state]
- **A released set is never reused**, even if its handles look right, because its slots may belong to
  other materials by now. The managed tests cover this and idempotent release.[^doh-tests-readme]
- **Other consumers of the same records.** Kitten Color's "Apply to All" writes every `AssetMap`
  entry, including doh clones; its save skips `doh_*` names because doh persists its own colours.
- No `CommunityToolkit.HighPerformance` reference: the span-to-bytes conversion uses
  `MemoryMarshal.AsBytes`, because that DLL is not in the assemblies tree the solution builds against.[^doh-lib-readme]
- 5472 changed the BRDF lookup in `ModelPbr.frag`; tinted kittens shade slightly differently than on
  older builds (cosmetic).[^scope-character]

## See also

- [doh kitten spawning](./doh-kitten-spawning.md)
- [Kitten Color GPU material buffer writes](./humble-arteest-kitten-color.md) (edits existing records instead of creating them)
- [Free Fallin canopy material substitution](./free-fallin-canopy-material-substitution.md) (another runtime `MaterialData` creation)
- [Scene save coordinator](./lib-scene-save-coordinator.md) and [native save hooks](./lib-native-save-hooks.md)
- [Named XKCD colour catalog](./lib-xkcd-color-catalog.md)
- [doh sub-mod](../submods/doh.md) and the [mod overview](../overview.md)
- [Clone the materials per kitten, swap the MaterialIndices handles and stay inside the 512-slot pool](../../../howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-2.md) - how-to article demonstrating this feature
- [Walk the avatar's struct fields down to the live MaterialIndices arrays](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-3.md) - how-to article demonstrating this feature
- [Rebind cloned kitten material sets to the restored kittens by vehicle id](../../../howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-1.md) - how-to article demonstrating this feature

[^material-accessor]: doh.lib/Materials/MaterialSystemAccessor.cs
[^material-factory]: doh.lib/Materials/MaterialFactory.cs
[^material-factory-lifetime]: doh.lib/Materials/MaterialFactory.Lifetime.cs
[^kitten-material-set]: doh.lib/Materials/KittenMaterialSet.cs
[^kitten-spawner-materials]: doh.lib/Spawning/KittenSpawner.Materials.cs
[^doh-submod]: doh.lib/DohSubmod.cs
[^doh-persistence]: doh.lib/DohSubmod.Persistence.cs
[^material-color-state]: ksa-abstractions.lib/Persistence/MaterialColorState.cs
[^doh-lib-readme]: doh.lib/README.md
[^doh-tests-readme]: doh.tests/README.md
[^scope-character]: scope/character-and-materials.md
[^ksa-gpuobjectsystem]: KSA.GpuObjectSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-characterrenderresources]: KSA.CharacterRenderResources (decompiled, build 2026.10.10.5554)
[^ksa-characteravatar]: KSA.CharacterAvatar (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-vkutils]: RenderCore.VkUtils (decompiled, build 2026.10.10.5554)
