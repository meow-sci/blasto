---
type: How-To
title: Restore kitten colours by material name, only where the asset object is still the one written
description: Humble Arteest records the original colour and the exact asset object before its first write to a material handle, saves colours by material name, and on reset or restore touches only handles whose AssetMap entry is still that object because material slots are reused
category: kittens
group: characters
topic: restore-kitten-customizations-after-a-load
approach: 2
difficulty: advanced
prerequisites: [starmap, reflection, scene-save-participant]
demonstrated_by: [mods/unscience/features/humble-arteest-kitten-color]
techniques: [save-sidecar, reflection]
hooks: [starmap StarMapAfterGui, save-participant humble-arteest, reflection KSA.AssetManager.AssetMap]
lifecycle: [event:save, event:load, frame:draw-ui]
game_types: [KSA.GpuObjectAssetRef, KSA.MaterialData, KSA.KittenEva]
tags: [kitten, save, load, materials, colour, identity]
---
Kitten Color writes a `float4` into the shared GPU material buffer so every kitten using a material changes colour at once ([tint article](../../appearance/tint-and-restyle-kitten-appearance/overview.md)). The buffer is process-global and survives a load, but its slots are integers that the renderer can free and hand to a different material. This article shows how the mod records what it changed, how it undoes it without clobbering someone else's material, and how it brings colours back from a save by name. Prerequisites: a save participant, and the reflection chain to the material system described in the tint article.

## Background: how the game does it

- Every character and glTF material is an 80-byte `MaterialData` record in `GpuMaterialSystem.BigBuffer`, addressed by the material asset's integer `Handle`; `AlbedoColor` is at byte 16 ([current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs), [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L45-L83)).
- Names map to assets in a protected `AssetMap` (`AssetName` to `GpuObjectAssetRef`); disposing an asset ref frees its slot, which a later `CreateObject` can reuse ([current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs)).
- A native load does not touch the buffer; kittens' `MaterialIndices` handles are rebuilt from the character data, and the character materials are the same registered assets.

## Lifecycle and timing

| Work | Where | Lifecycle |
|---|---|---|
| Every colour write | the panel (`[StarMapAfterGui]`), a blocking staging upload before the frame's command buffer | `frame:draw-ui` |
| Capture | participant capture when the native save is populated | `event:save` |
| Reset and restore | the save host's reset/restore callbacks around the native load | `event:load` |

## Steps

1. **Before the first write to a handle, remember the original colour and the exact asset object.** If the handle now maps to a different asset (the slot was reused), replace the record:

```csharp
// humble-arteest.lib/KittenColor.Persistence.cs L16-L28
    private static void TrackColor(int handle, float4 original, float4 color)
    {
        if (_assetMap == null) return;
        foreach (DictionaryEntry material in _assetMap)
        {
            string? name = material.Key?.ToString();
            if (name == null || material.Key == null || material.Value == null || ReadHandle(material.Value) != handle) continue;
            if (!OriginalColors.TryGetValue(handle, out var previous) || !ReferenceEquals(previous.Asset, material.Value))
                OriginalColors[handle] = new OriginalMaterial(material.Key, name, material.Value, original);
            AppliedColors[name] = color;
            break;
        }
    }
```

2. **Capture by name, and skip names another feature owns.** doh's `doh_NNNN` and Free Fallin's `free-fallin/` materials have process-local names; their own participants record them:

```csharp
// humble-arteest.lib/KittenColor.Persistence.cs L32-L43
    internal static Dictionary<string, float4> CaptureColors()
    {
        var result = new Dictionary<string, float4>(StringComparer.Ordinal);
        foreach (var pair in AppliedColors)
        {
            // DOH private slots have process-local names. Its participant records the actual
            // tracked color under kitten/material identity and reconstructs them itself.
            if (!pair.Key.StartsWith("doh_", StringComparison.Ordinal)
                && !pair.Key.StartsWith("free-fallin/", StringComparison.Ordinal)) result[pair.Key] = pair.Value;
        }
        return result;
    }
```

3. **Reset only what the mod still owns.** Renderer-owned assets can be disposed and their slot reused between edits, so write the original back only when the `AssetMap` entry for the name is still the same asset object and still has the same handle:

```csharp
// humble-arteest.lib/KittenColor.Persistence.cs L45-L59
    internal static void ResetOwnedColors()
    {
        foreach (var pair in OriginalColors)
        {
            var original = pair.Value;
            // Renderer-owned assets can be disposed and their integer slot reused between edits.
            if (_assetMap == null || !ReferenceEquals(_assetMap[original.Key], original.Asset)
                || ReadHandle(original.Asset) != pair.Key) continue;
            if (!WriteAlbedoColor(pair.Key, original.Color))
                throw new InvalidOperationException(LastError ?? "Could not restore a material's original color.");
            MaterialColorState.Record(pair.Key, original.Color);
        }
        OriginalColors.Clear();
        AppliedColors.Clear();
    }
```

4. **Restore by name against the live asset map.** Re-initialise the reflection state, refresh the cached material list, resolve each saved name to a handle, and warn (never throw) about a name that no longer exists:

```csharp
// humble-arteest.lib/KittenColor.Persistence.cs L61-L74
    internal static void RestoreColors(Dictionary<string, float4> colors, SaveRestoreContext context)
    {
        if (colors.Count == 0) return;
        if (!Initialize()) { context.Warn(LastError ?? "Kitten material system unavailable."); return; }
        RefreshMaterialCache();
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in GetMaterials()) materials[item.Name] = item.Handle;
        foreach (var item in colors)
        {
            if (!materials.TryGetValue(item.Key, out int handle))
            { context.Warn($"Kitten material missing: {item.Key}."); continue; }
            if (!ApplyToMaterial(handle, item.Value)) context.Warn(LastError ?? $"Kitten material upload failed: {item.Key}.");
        }
    }
```

5. **Check the result.** After a load the kittens carry the saved colours; a name that no longer exists (for example a mod material removed) appears as `Kitten material missing: <name>` in the save status. Reset on disable and unload restores the original colours only where the identity check passes.

## Complete example

- [humble-arteest.lib/KittenColor.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.Persistence.cs#L10-L75) - tracking, capture, reset and restore.
- [humble-arteest.lib/KittenColor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColor.cs#L204-L222) - the staged one-`float4` write.
- [ksa-abstractions.lib/Persistence/MaterialColorState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/MaterialColorState.cs#L1-L14) - the shared record of colours written by any mod.
- Feature page: [Kitten colour](../../../../mods/unscience/features/humble-arteest-kitten-color.md).

## Variations and gotchas

- **Never persist a handle.** Persist the material name and re-resolve it; verify the asset identity before writing back.
- **No GPU readback.** The "original" colour is whatever `MaterialColorState` recorded or white; colours written by code that bypasses the tracker are not restored.
- **"Apply to all" is not kitten-only.** It writes every `AssetMap` entry, which includes glTF model materials and other mods' runtime materials; the save skips the other mods' prefixes.
- **A recycled slot** is the reason for the identity check: writing the old colour into a slot that now belongs to another material would recolour it.
- **Alpha below 0.1** makes surfaces drawn with `ModelPbr.frag` disappear; the restore does not clamp it.

## See also

- [Restore kitten colours, clips and tuning after a save load](overview.md), [approach 1](restore-kitten-customizations-after-a-load-1.md), [approach 3](restore-kitten-customizations-after-a-load-3.md)
- [Tint kitten fur, body and eyes, and hide the visor, helmet or MMU](../../appearance/tint-and-restyle-kitten-appearance/overview.md)
