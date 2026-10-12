---
type: How-To
title: Rebind cloned kitten material sets to the restored kittens by vehicle id
description: doh keeps its detached GPU material sets in a cache across a native load, never despawns during reset, then claims one set per restored KittenEva by vehicle id and character, releases the unclaimed ones and rejects duplicate ids before the world is reset
category: kittens
group: characters
topic: restore-kitten-customizations-after-a-load
approach: 1
difficulty: advanced
prerequisites: [starmap, scene-save-participant]
demonstrated_by: [mods/unscience/features/doh-runtime-gpu-materials]
techniques: [save-sidecar]
hooks: [starmap StarMapAfterGui, save-participant doh]
lifecycle: [event:save, event:load, frame:prepare]
game_types: [KSA.KittenEva, KSA.GpuObjectAssetRef, KSA.Universe]
tags: [kitten, save, load, materials, rebind, gpu]
---
doh gives each spawned kitten its own cloned materials ([approach 3 of the avatar topic](../reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-3.md), [tinting](../../appearance/tint-and-restyle-kitten-appearance/overview.md)). After a native load the kittens are new objects, but the cloned materials are still in the GPU pool, which the load does not clear. This article shows how doh keeps those sets, rebinds them to the restored kittens, and frees whatever nobody claims. Prerequisites: a save participant (a `Capture`/`Reset`/`Restore` triple registered with a host that calls them around the native load, as unscience's scene-save coordinator does).

## Background: how the game does it

- The load destroys the old kittens and creates new ones from the saved `VehicleData`, keeping each kitten's vehicle id ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2480), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L696-L709)). A mod's identity for a kitten must be that id (plus the character id), never a reference, a list index, a GPU handle or a `doh_NNNN` asset name.
- The material pool has a fixed number of slots; a mod that clones sets and leaks them across loads fills it ([pool budgeting](../../appearance/tint-and-restyle-kitten-appearance/overview.md)).
- Native saves already contain the kittens, so the participant must **never spawn replacements**.

## Lifecycle and timing

| Phase | Participant method | Lifecycle |
|---|---|---|
| Native save populates | `CaptureDoh` records each tracked kitten's id, character, tint, material group and per-material colours (colours read from the shared `MaterialColorState`, i.e. what is actually on the GPU) | `event:save` |
| Before the native load destroys the world | `ValidateDoh` runs first (during load preparation); `ResetDoh` moves live sets to a detached cache | `event:load` (reset), `frame:prepare` handoff |
| After the native load rebuilt the kittens | `RestoreDoh` finds each kitten by id, claims a set, reapplies colours | `event:load`, `frame:prepare` |

Reset and restore run at the `PrepareFrame` handoff, where the vehicle solver is idle ([coordinator feature](../../../../mods/unscience/features/lib-native-save-hooks.md)). The restore calls `ApplyClonedMaterials` (the same path as a fresh spawn), whose `MaterialIndices` swap is safe because those arrays are read later in the frame, when each viewport draws.

## Steps

1. **Validate before anything is destroyed.** Reject a record that would be ambiguous after the load: blank ids, duplicate kitten ids, duplicate (source, material) names, absurd counts:

```csharp
// doh.lib/DohSubmod.Persistence.cs L21-L34
    private static void ValidateDoh(SavedDoh saved)
    {
        if (saved.SpawnCount is < 1 or > 10000 || saved.Kittens == null || saved.Kittens.Count > 10000
            || saved.Kittens.Any(k => k == null || string.IsNullOrWhiteSpace(k.VehicleId) || string.IsNullOrWhiteSpace(k.CharacterId)
                || (k.MaterialGroup != null && string.IsNullOrWhiteSpace(k.MaterialGroup))
                || k.Materials == null || k.Materials.Count > 10000
                || k.Materials.Any(m => m == null || string.IsNullOrWhiteSpace(m.Name) || m.Source == null)))
            throw new InvalidOperationException("Invalid DOH kitten/material records.");
        if (saved.Kittens.Select(k => k.VehicleId).Distinct(StringComparer.Ordinal).Count() != saved.Kittens.Count)
            throw new InvalidOperationException("Duplicate saved DOH kitten identifiers.");
        foreach (var kitten in saved.Kittens)
            if (kitten.Materials.Select(m => (m.Source, m.Name)).Distinct().Count() != kitten.Materials.Count)
                throw new InvalidOperationException("Duplicate saved DOH material identities.");
    }
```

2. **Capture by identity.** Record the vehicle id, character, tint, the material group id (kittens that shared a set share a group) and the colour per material, keyed by its source renderable and name; skip disposed kittens:

```csharp
// doh.lib/DohSubmod.Persistence.cs L46-L56
        foreach (var entry in _registry.GetAll())
        {
            if ((entry.Vehicle ?? VehicleProvider.FindVehicle(entry.KittenId)) is not KittenEva kitten || kitten.IsDisposed) continue;
            saved.Kittens.Add(new()
            {
                VehicleId = kitten.Id, CharacterId = entry.CharacterId, Tint = entry.MaterialSet?.TintColor,
                MaterialGroup = entry.MaterialSet?.Id,
                Materials = entry.MaterialSet?.Materials.Select(m => new SavedMaterial
                    { Name = m.Name, Source = m.Source, Color = MaterialColorState.GetOrDefault(m.Handle, m.Color) }).ToList() ?? new()
            });
        }
```

3. **Reset without despawning.** The native load destroys the kittens; doh keeps only the detached GPU sets, indexed by vehicle id and character, and clears its own registry and UI state. It never calls `DespawnAll` here:

```csharp
// doh.lib/DohSubmod.Persistence.cs L60-L75
    private void ResetDoh()
    {
        // Native load owns kitten destruction. Keep only detached GPU recipes/slots, and
        // never call DespawnAll here or create replacement kittens during restore.
        if (_registry != null)
            foreach (var entry in _registry.GetAll())
                if (entry.MaterialSet != null)
                    _savedMaterialCache[CacheKey(entry.Vehicle?.Id ?? entry.KittenId, entry.CharacterId)] = entry.MaterialSet;
        _registry?.Clear();
        _selectedVehicle = null;
        _offset = new float3(0, 0, 10);
        _spawnCount = 1;
        _useCustomColor = _uniquePerKitten = false;
        _tintColor = new float4(1, 1, 1, 1);
        _selectedCharacterIndex = -1;
    }
```

4. **Restore by finding the new object by id.** For each saved kitten, `VehicleProvider.FindVehicle(id)` returns the natively recreated `KittenEva`; a missing one is a warning. Pick a reusable set (the kitten's group set, else its own detached set, each claimed at most once), run the normal clone-or-reuse path, reapply each saved colour by (source, name), and register the kitten:

```csharp
// doh.lib/DohSubmod.Persistence.cs L84-L111
        var groupSets = new Dictionary<string, KittenMaterialSet>(StringComparer.Ordinal);
        var claimed = new HashSet<KittenMaterialSet>();
        foreach (var item in saved.Kittens)
        {
            if (VehicleProvider.FindVehicle(item.VehicleId) is not KittenEva kitten || kitten.IsDisposed)
            { context.Warn($"DOH kitten missing: {item.VehicleId}."); continue; }
            KittenMaterialSet? set = null;
            if (item.Tint.HasValue)
            {
                var reusable = FindReusableSet(item, groupSets, claimed);
                set = _spawner?.ApplyClonedMaterials(kitten, item.Tint.Value, item.CharacterId, reusable);
                if (set == null) context.Warn($"DOH materials unavailable for {item.VehicleId}.");
                else
                {
                    foreach (var material in item.Materials)
                    {
                        var matches = set.Materials.Where(m => m.Name == material.Name && m.Source == material.Source).ToArray();
                        if (matches.Length != 1) { context.Warn($"DOH material unresolved: {item.VehicleId}/{material.Source}/{material.Name}."); continue; }
                        matches[0].Color = material.Color;
                        if (!matches[0].ApplyColor()) context.Warn($"DOH material upload failed: {material.Name}.");
                    }
                    claimed.Add(set);
                    if (item.MaterialGroup != null) groupSets[item.MaterialGroup] = set;
                }
            }
            _registry?.Register(kitten.Id, item.CharacterId, set);
        }
        ReleaseStaleSavedMaterials();
```

5. **Never merge kittens that were unique in the save.** A detached set is claimed at most once, and a released set is never reused (its slots may belong to other materials by now):

```csharp
// doh.lib/DohSubmod.Persistence.cs L121-L128
    private KittenMaterialSet? FindReusableSet(SavedKitten item,
        Dictionary<string, KittenMaterialSet> groupSets, HashSet<KittenMaterialSet> claimed)
    {
        if (item.MaterialGroup != null && groupSets.TryGetValue(item.MaterialGroup, out var shared))
            return shared;
        return _savedMaterialCache.TryGetValue(CacheKey(item.VehicleId, item.CharacterId), out var cached)
            && !cached.IsReleased && !claimed.Contains(cached) ? cached : null;
    }
```

6. **Release what nobody claimed** back to the fixed-size pool, after the restore and also from the per-frame update (a load with no doh record at all never calls restore):

```csharp
// doh.lib/DohSubmod.Persistence.cs L135-L144
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

7. **Check the result.** The scene-save coordinator reports warnings (`DOH kitten missing`, `DOH material unresolved`) in its status; a load with the mod's tinted kittens present should show them tinted again with the pool slot count unchanged.

## Complete example

- [doh.lib/DohSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.Persistence.cs#L12-L176) - participant, record classes, reset, restore, reuse and release.
- [doh.lib/Spawning/KittenSpawner.Materials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Materials.cs#L20-L164) - `ApplyClonedMaterials`, including the `reusable` parameter.
- Feature page: [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md).

## Variations and gotchas

- **Handles are not identities.** A GPU handle or a `doh_NNNN` name is meaningless in the next process and may be reused in this one. Persist (vehicle id, character, source renderable, material name, colour).
- **Do not spawn on restore.** The native load recreates the kittens; restoring is rebinding.
- **Cached sets that nobody claimed leak slots** unless released (step 6).
- **Saves from older builds** may lack the optional `MaterialGroup`; each kitten then restores with its own set.
- **Another mod's colours.** `MaterialColorState` is shared, so colours written by [Humble Arteest](restore-kitten-customizations-after-a-load-2.md) on doh's slots are what gets captured; that mod skips `doh_*` names in its own record.
- **Pool exhaustion** during restore turns into a warning for that kitten, not an exception.

## See also

- [Restore kitten colours, clips and tuning after a save load](overview.md), [approach 2](restore-kitten-customizations-after-a-load-2.md), [approach 3](restore-kitten-customizations-after-a-load-3.md)
- [Walk the avatar's struct fields down to the live MaterialIndices arrays](../reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-3.md)
- [Spawn an EVA kitten from code](../spawn-an-eva-kitten-from-code/overview.md)
