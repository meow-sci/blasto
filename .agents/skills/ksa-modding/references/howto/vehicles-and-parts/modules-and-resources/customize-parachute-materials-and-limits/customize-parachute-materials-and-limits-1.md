---
type: How-To
title: Swap parachute canopy materials by editing MaterialIndices in a ChuteRenderable.Draw prefix
description: Create a runtime GPU MaterialData entry and write its handle into slot 0 of each canopy renderable's material index array before the game submits the draw
category: vehicles-and-parts
group: modules-and-resources
topic: customize-parachute-materials-and-limits
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/free-fallin-canopy-material-substitution]
techniques: [harmony-prefix, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.ChuteRenderable.Draw
  - reflection KSA.ChuteRenderable._renderable
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:load, frame:render, render:prepass]
game_types:
  - KSA.ChuteRenderable
  - KSA.AnimatedRenderable
  - KSA.MaterialData
  - KSA.Parachute
  - KSA.SuperMeshRenderSystem
  - KSA.PbrMaterialReference
tags: [parachute, canopy, material, gpu, reflection, restore]
---
Outcome: every parachute canopy in the game (deployed now or later) shows one custom look: a tinted stock pattern, a PNG
mapped through the panel UVs, or a PNG projected over the whole canopy, with roughness and metallic controls, and
**Restore Stock** puts each canopy back to the style it originally chose. unscience's Free Fallin does it by building one
`MaterialData` in the game's GPU material system and, each time a canopy is drawn, writing its handle into slot 0 of the
canopy renderable's material index array.

Prerequisites: StarMap, Harmony, reflection into a private field and a protected one. Demonstrated by
[unscience/free-fallin-canopy-material-substitution](../../../../mods/unscience/features/free-fallin-canopy-material-substitution.md). If the authored materials are enough, use the editor's "Canopy
Color" combo and `Parachute.SetCanopyMaterial` instead (see the [overview](overview.md)).

## Background: how the game does it

- `Parachute.DrawCanopy` creates one `ChuteRenderable` per live canopy lazily. Its constructor copies the canopy GLB's
  material array, replaces slot 0 with the parachute's selected `CanopyMaterial` (through
  `MaterialSystem.GetOrLoad(materialId)`) and builds the private `AnimatedRenderable _renderable`
  [current/decomp/KSA/ChuteRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteRenderable.cs#L20-L36)[^ksa-chuterenderable] [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1185)[^ksa-parachute].
- `ChuteRenderable.Draw(ViewHandle, ...)` updates the pose and calls `_renderable.Draw(view)`, which for every mesh sets
  `instanceData.data.X = MaterialIndices[i]` and submits the instance to the main-opaque, prepass and shadow buckets
  [current/decomp/KSA/ChuteRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteRenderable.cs#L38-L50) [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L209-L258)[^ksa-animatedrenderable]. `MaterialIndices` is a `protected readonly
  int[]` [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L32); the array is mutable even though the field is readonly.
- Because the copy happens at submission, a handle in force when `Draw` runs is used for every view and pass that frame.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| prefix installed | `StarMapAllModsLoaded` -> the mod's patcher | `startup:all-mods-loaded` |
| Apply / Restore Stock | panel buttons from `StarMapAfterGui` | `frame:draw-ui` |
| Apply from a save / reset to stock | save participant restore / reset | `event:load` |
| prefix runs | `Program.RenderGame` -> `vehicle.UpdateParachuteRenderData(viewport)` -> `Parachute.DrawCanopy` -> `ChuteRenderable.Draw`; also for each visible secondary viewport in `RenderViewport`; portrait viewports return early | `frame:render` (mesh-bucket submission at the start of `RenderGame` recording, before the prepass), `render:prepass` consumers |

The swap runs on the main thread at submission time; the cloth simulation is untouched because the prefix only reads the
renderable. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the private field and the protected array** with `AccessTools` at patch time; throw if either is missing so
   a rename is a startup error:

```csharp
// free-fallin.lib/FreeFallinPatches.cs L12-20
    private static readonly FieldInfo RenderableField = AccessTools.Field(typeof(ChuteRenderable), "_renderable")
        ?? throw new MissingFieldException(typeof(ChuteRenderable).FullName, "_renderable");
    private static readonly FieldInfo MaterialIndicesField = AccessTools.Field(typeof(AnimatedRenderable), "MaterialIndices")
        ?? throw new MissingFieldException(typeof(AnimatedRenderable).FullName, "MaterialIndices");
    private static readonly List<ObservedCanopy> Observed = new();
    private static ConditionalWeakTable<AnimatedRenderable, ObservedCanopy> Seen = new();
    private static readonly MethodInfo Target = AccessTools.Method(typeof(ChuteRenderable), nameof(ChuteRenderable.Draw))
        ?? throw new MissingMethodException(typeof(ChuteRenderable).FullName, nameof(ChuteRenderable.Draw));
    private static readonly MethodInfo Prefix = AccessTools.Method(typeof(FreeFallinPatches), nameof(BeforeDraw))!;
```

2. **Prefix `ChuteRenderable.Draw`**, active only while a custom material exists. On first sight of each renderable record
   its native slot-0 handle in a `ConditionalWeakTable` plus a list of weak references; then write the custom handle:

```csharp
// free-fallin.lib/FreeFallinPatches.cs L54-69
    private static void BeforeDraw(ChuteRenderable __instance)
    {
        if (!CanopyMaterialController.Enabled) return;
        if (RenderableField.GetValue(__instance) is not AnimatedRenderable renderable) return;
        Track(renderable);
        SetHandle(renderable, CanopyMaterialController.CurrentMaterialHandle);
    }

    private static void Track(AnimatedRenderable renderable)
    {
        if (Seen.TryGetValue(renderable, out _)) return;
        if (MaterialIndicesField.GetValue(renderable) is not int[] indices || indices.Length == 0) return;
        var observed = new ObservedCanopy(renderable, indices[0]);
        Seen.Add(renderable, observed);
        Observed.Add(observed);
    }
```

3. **Write the handle** into slot 0 and remember it as "last applied":

```csharp
// free-fallin.lib/FreeFallinPatches.cs L105-111
    private static void SetHandle(AnimatedRenderable renderable, int handle)
    {
        if (MaterialIndicesField.GetValue(renderable) is not int[] indices || indices.Length == 0) return;
        indices[0] = handle;
        if (Seen.TryGetValue(renderable, out ObservedCanopy? observed))
            observed.LastAppliedHandle = handle;
    }
```

4. **Build the material.** One `MaterialData` per Apply: stock diffuse with the repeat sampler (or an uploaded PNG), tint
   times brightness as `AlbedoColor`, the stock normal map to keep the cloth weave, a stock PBR map with AO, roughness
   and metallic multipliers (R = AO, G = roughness, B = metallic), and the stock emissive map. Register it by name and
   resolve its handle:

```csharp
// free-fallin.lib/CanopyMaterialController.cs L70-92
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
        var materialAsset = renderSystem.MaterialSystem.GetOrLoad(materialName);
        int handle = materialAsset.Handle;
        owned.Own(renderSystem.MaterialSystem, materialAsset, () => MaterialColorState.Forget(handle));
        MaterialColorState.Record(handle, tint);
        FreeFallinPatches.ReplaceObserved(handle);
        CurrentMaterialHandle = handle;
        AppliedSettings = settings;
```

5. **Retarget living canopies** when a new material replaces the old one, before the old one is released:

```csharp
// free-fallin.lib/FreeFallinPatches.cs L92-103
    internal static void ReplaceObserved(int handle)
    {
        for (int i = Observed.Count - 1; i >= 0; i--)
        {
            if (!Observed[i].Renderable.TryGetTarget(out AnimatedRenderable? renderable))
            {
                Observed.RemoveAt(i);
                continue;
            }
            SetHandle(renderable, handle);
        }
    }
```

6. **Restore** writes the native handle back only when the slot still holds the handle this mod last wrote (a newer owner is
   left alone), then drops all tracking so the next Apply captures fresh native choices:

```csharp
// free-fallin.lib/FreeFallinPatches.cs L71-90
    private static void RestoreObserved()
    {
        for (int i = Observed.Count - 1; i >= 0; i--)
        {
            ObservedCanopy observed = Observed[i];
            if (observed.Renderable.TryGetTarget(out AnimatedRenderable? renderable)
                && MaterialIndicesField.GetValue(renderable) is int[] indices
                && indices.Length > 0
                && observed.LastAppliedHandle >= 0
                && indices[0] == observed.LastAppliedHandle)
            {
                indices[0] = observed.OriginalHandle;
            }
        }

        // Drop both weak references and identity markers. A later Apply must capture each native
        // material afresh, including after KSA recreates a canopy with a different selected style.
        Observed.Clear();
        Seen = new ConditionalWeakTable<AnimatedRenderable, ObservedCanopy>();
    }
```

## Complete example

- [free-fallin.lib/FreeFallinPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/FreeFallinPatches.cs)[^unscience-freefallinpatches], [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs)[^unscience-canopymaterialcontroller]
- [free-fallin.lib/CanopyGpuAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyGpuAssets.cs)[^unscience-canopygpuassets] - ownership and release of GPU textures and materials

## Variations and gotchas

- **Why a prefix, not the constructor.** The renderable is created lazily and per canopy; the draw seam catches
  canopies already deployed as well as later ones, and `Restore` needs the native handle you recorded.
- **Release order.** Retarget observed canopies before releasing the previous material set; each Apply creates a new
  record and textures and retires the old allocation set.
- **Shader parameters.** Projection modes pass parameters through `ExtraData` and a patched shader; plain colour and PNG
  modes need no shader change.
- **Updates.** One private field, one protected array and the `MaterialData` shape; each is checked at patch time.

## See also

- [Overview](overview.md), [Article 2](customize-parachute-materials-and-limits-2.md)
- [unscience/free-fallin-canopy-material-substitution](../../../../mods/unscience/features/free-fallin-canopy-material-substitution.md)
- [Scale a vessel or its parts at runtime and lift the editor scale limits](../../vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md)

[^unscience-canopygpuassets]: unscience: free-fallin.lib/CanopyGpuAssets.cs
[^ksa-animatedrenderable]: current/decomp/KSA/AnimatedRenderable.cs (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: current/decomp/KSA/Parachute.cs (decompiled, build 2026.10.10.5554)
[^unscience-canopymaterialcontroller]: unscience: free-fallin.lib/CanopyMaterialController.cs
[^ksa-chuterenderable]: current/decomp/KSA/ChuteRenderable.cs (decompiled, build 2026.10.10.5554)
[^unscience-freefallinpatches]: unscience: free-fallin.lib/FreeFallinPatches.cs
