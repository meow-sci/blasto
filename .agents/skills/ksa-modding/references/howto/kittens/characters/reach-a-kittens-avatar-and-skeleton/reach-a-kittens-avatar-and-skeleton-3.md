---
type: How-To
title: Walk the avatar's struct fields down to the live MaterialIndices arrays
description: doh follows a path of field names from KittenEva through KittenRenderable._characterAvatar and the boxed Core, Fur, Attachments, Helmet and Mmu structs to the protected int[] MaterialIndices of each kitten renderable, which stays a reference to the live array
category: kittens
group: characters
topic: reach-a-kittens-avatar-and-skeleton
approach: 3
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/doh-runtime-gpu-materials]
techniques: [reflection]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.KittenEva._renderable
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection KSA.AnimatedRenderable.MaterialIndices
lifecycle: [frame:draw-ui]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.CatFurRenderable
  - KSA.StaticMeshRenderable
tags: [kitten, materials, reflection, structs, avatar]
---
Every renderable that draws part of a kitten (the skinned body, the fur shells, the helmet, the visor, the MMU) draws with integer material handles stored in a protected `int[] MaterialIndices`. A mod that wants those arrays has to cross three obstacles: the avatar is private, the parts it holds are structs (so reflection hands back copies), and the array field is `protected` and declared on a base class. unscience's doh sub-mod solves all three with one tiny path walker. This article shows the walker and what it can and cannot do; what doh does with the arrays (cloning materials, tinting) is in [Tint kitten fur, body and eyes](../../appearance/tint-and-restyle-kitten-appearance/overview.md). Prerequisites: StarMap and reflection.

## Background: how the game does it

- `AnimatedRenderable`, `CatFurRenderable` and `StaticMeshRenderable` each declare `protected readonly int[] MaterialIndices`, allocated once in the constructor with one entry per mesh (or fur sub-mesh) and filled with `Material.Handle` values ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L32), [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L104-L118), [current/decomp/KSA/CatFurRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatFurRenderable.cs#L22), [current/decomp/KSA/CatFurRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CatFurRenderable.cs#L49-L64), [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L27), [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L43-L51)).
- `Draw` reads them every time it enqueues an instance: `instanceData.data.X = MaterialIndices[i]` ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L224-L228), [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L65-L71)). The array is per renderable, so a change affects that one kitten; the material list of the shared glTF asset (`gltf.Materials`) is cached and shared, so do not write there.
- `CharacterAvatar` keeps `Core`, `Fur`, `Attachments` (with `Helmet`, `Mmu` inside) as structs in public fields; the renderables inside them (`Core.CharacterModel`, `Fur.CatFurRenderable`, `Helmet.HelmetMesh`, `Helmet.VisorMesh`, `Mmu.MmuMesh`) are class references ([current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L30-L141), [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L209-L219)).
- Reading a struct-typed field by reflection boxes a copy of the struct. The copy's class-typed members still point at the live renderables, so the chain keeps working as long as you only read through it. Writing to a boxed copy changes nothing, which is why the array contents (a reference) are the thing to write to.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Walk the path and swap handles (on spawn, on tint change) | `DohSubmod.RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| The engine reads `MaterialIndices` | Inside `KittenRenderable.UpdateRenderData` while recording each viewport in `RenderGame` | `frame:render` |

Swapping entries from the UI pass is safe because the arrays are read later in the same frame, when each viewport's kitten draws. Nothing here runs off the main thread. See [approach 4](reach-a-kittens-avatar-and-skeleton-4.md) for the draw order and the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for the render sequence.

## Steps

1. **Reach the avatar.** doh reads the private `KittenEva._renderable` and then `_characterAvatar`. (`KittenEva.Renderable` is a public getter for the same object; reading the private field is unnecessary and adds a second name to maintain.)

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L166-L179
    /// <summary>Gets the CharacterAvatar from a KittenEva via reflection.</summary>
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
```

2. **Walk a path of field names.** Each hop reads a public or private instance field; the final object is the renderable whose `MaterialIndices` field is looked up up the type hierarchy, because the field is declared on a base class:

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L186-L220
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
    }

    /// <summary>Gets a field value by name, searching public and non-public fields.</summary>
    private static object? GetFieldValue(object instance, string name)
    {
        var field = instance.GetType().GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(instance);
    }

    private static FieldInfo? FindFieldInHierarchy(Type? type, string fieldName)
    {
        while (type != null)
        {
            var field = type.GetField(fieldName,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }
```

   `Type.GetField` with `NonPublic` does not return private members of base classes, which is why `FindFieldInHierarchy` walks `BaseType` with `DeclaredOnly`. (`MaterialIndices` is `protected`, which would be found by `GetField` with `NonPublic` on a derived type, but a walk is robust if the field ever moves up or down.)

3. **Collect the five arrays** by their paths, skip any that are null, and bail out with a log line if none was found:

```csharp
// doh.lib/Spawning/KittenSpawner.Materials.cs L35-L67
            // Collect MaterialIndices arrays from all renderables
            var renderables = new List<(string name, int[] indices)>();

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
```

4. **Replace handles in place.** The arrays hold live references, so the next draw uses the new handle. doh keeps a map from old handle to cloned handle and rewrites every matching entry (the material cloning itself is in the [tint article](../../appearance/tint-and-restyle-kitten-appearance/overview.md)):

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

## Complete example

- [doh.lib/Spawning/KittenSpawner.Materials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/Spawning/KittenSpawner.Materials.cs#L1-L221) - the whole partial class: avatar walk, handle swap, error cleanup.
- The tinting and pool budget side is in [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md).

## Variations and gotchas

- **Only paths are tested.** The field names `Core`, `Fur`, `Attachments`, `Helmet`, `Mmu`, `CharacterModel`, `CatFurRenderable`, `HelmetMesh`, `VisorMesh`, `MmuMesh` and `MaterialIndices` exist in build 5554. A rename returns null for that renderable and doh prints `No MaterialIndices found`. With compiled access the first hops would be checked by the compiler (`avatar.Core.CharacterModel`, `avatar.Fur.CatFurRenderable`, `avatar.Attachments.Helmet.HelmetMesh`); only the final protected array needs reflection, so a mod that reaches the avatar with [approach 1](reach-a-kittens-avatar-and-skeleton-1.md) can shorten the walk.
- **The attachment meshes are `null` for characters without them.** `Helmet.HelmetMesh` and `VisorMesh` are null when the character has no helmet or visor attachment, `Mmu.MmuMesh` when it has no MMU; the walker returns null for those.
- **Cosmetic attachments are not walked.** `Attachments.CosmeticAttachments` is a `List<CosmeticAttachment>` of structs; each has a `Mesh` (`StaticMeshRenderable`) with its own `MaterialIndices`. Extend the walker with an index hop if you need them.
- **Fur shells are special.** The fur renderable's material needs shader `ExtraData`, so cloned fur materials are built separately.
- **Seated kittens** are drawn through `IVASeat`'s own `KittenRenderable`, not `KittenEva.Renderable`, so this path does not reach them.
- **Do not hold the array across avatar rebuilds.** A new `KittenRenderable` (for example after a save load) allocates new arrays; re-walk after a load ([restore after a load](../restore-kitten-customizations-after-a-load/overview.md)).

## See also

- [Reach a kitten's avatar, skeleton, bones and animation catalog](overview.md)
- [Approach 1: one reflection hop](reach-a-kittens-avatar-and-skeleton-1.md), [approach 2: clip catalog](reach-a-kittens-avatar-and-skeleton-2.md), [approach 4: draw path](reach-a-kittens-avatar-and-skeleton-4.md)
- Mod feature: [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md)
- [Tint kitten fur, body and eyes, and hide the visor, helmet or MMU](../../appearance/tint-and-restyle-kitten-appearance/overview.md)
