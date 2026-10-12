---
type: Mod Feature
title: Swap every parachute canopy's material at draw time
description: A Harmony prefix on ChuteRenderable.Draw reflects the private AnimatedRenderable and overwrites slot 0 of its protected MaterialIndices with one runtime MaterialData (tint, PBR multipliers or uniform PBR, custom albedo), capturing and later restoring each canopy's native material handle
mod: unscience
submod: free-fallin
category: vehicles-and-parts
techniques: [harmony-prefix, reflection, vulkan-buffer]
hooks:
  - harmony-prefix KSA.ChuteRenderable.Draw
  - reflection KSA.ChuteRenderable._renderable (private)
  - reflection KSA.AnimatedRenderable.MaterialIndices (protected)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Apply / Restore Stock)
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:load, frame:render]
game_types:
  - KSA.ChuteRenderable
  - KSA.AnimatedRenderable
  - KSA.Parachute
  - KSA.Vehicle
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
  - KSA.MaterialData
  - KSA.PbrMaterialReference
  - KSA.SuperMeshRenderSystem
  - KSA.GltfPbrSystem
  - KSA.GpuTextureSystem
  - KSA.ModLibrary
  - KSA.Program
tags: [parachutes, materials, pbr, harmony, reflection, gpu-materials]
---

Free Fallin gives every parachute canopy one custom look: stock pattern recoloured by a tint and
brightness, a PNG mapped through the panel UVs, one PNG projected over the whole canopy, or a PNG
composited into the centre of the stock texture, plus ambient-occlusion, roughness and metallic
controls. It applies to canopies already deployed and to every canopy that deploys later, and
**Restore Stock** puts each canopy back to the style it originally chose.[^ff-submod]

Mechanism in one line: build one `MaterialData` record in the game's GPU material system, then, every
time a canopy is drawn, write its handle into slot 0 of that canopy renderable's material index
array, which the game copies into each instance it submits.[^ff-patches][^ff-controller]

## How it works

**The draw seam.** KSA creates one `ChuteRenderable` per live canopy (lazily, in
`Parachute.DrawCanopy`), passing the canopy GLB's materials with slot 0 replaced by the parachute's
selected `CanopyMaterial`. Its `Draw(ViewHandle, ...)` updates the cloth pose and calls the private
`AnimatedRenderable _renderable.Draw(view)`, which, for every mesh, writes
`instanceData.data.X = MaterialIndices[i]` into the instances it sends to the main-opaque, opaque
prepass and shadow buckets.[^ksa-chuterenderable][^ksa-animatedrenderable][^ksa-parachute]

**The prefix** (`BeforeDraw(ChuteRenderable __instance)`), active only while a custom material exists:

1. read `_renderable` with a cached `FieldInfo` (private on `ChuteRenderable`);
2. on first sight, record `MaterialIndices[0]` (protected on `AnimatedRenderable`) as that renderable's
   native handle in a `ConditionalWeakTable` plus a list of `WeakReference`s;
3. write the current custom handle into `MaterialIndices[0]` and remember it as "last applied".

`RestoreObserved` writes the native handle back only when the slot still holds the handle this mod
last wrote (a newer owner is left alone), then drops all tracking so the next Apply captures fresh
native choices. `ReplaceObserved(handle)` retargets all living observed canopies when a new material
replaces the old one, before the old one is released.[^ff-patches]

**The material** (`CanopyMaterialController.ApplyCore`):[^ff-controller]

| `MaterialData` field | Value |
|---|---|
| `AlbedoTexture`, `Sampler` | Stock diffuse of `ParachuteCanopy_Material_CheckerLongOrange` with the repeat sampler, or an uploaded PNG (see [texture compositing](./free-fallin-canopy-texture-compositing.md)) |
| `AlbedoColor` | `Tint.rgb * Brightness`, alpha 1 (or the saved effective colour) |
| `NormalTexture` | Stock normal map (cloth weave kept), else the glTF blank normal |
| `RoughMetallicAOTexture`, `RoughnessMetalScale` | Stock PBR map with `(AO, roughness, metallic, 1)` multipliers, or a 1x1 uniform texture with scale 1 |
| `EmissiveTexture` | Stock emissive map, else the default black texture |
| `ExtraData` | Zero, or the full-canopy projection parameters and marker (see [projection shader](./free-fallin-canopy-projection-shader.md)) |

The record is registered with `MaterialSystem.CreateObject("free-fallin/material/<n>", data)` and
resolved with `GetOrLoad` to get its handle; the shader reads `albedoColor`, `AoRoughMetalScale` and
the texture indices through `MaterialSet.glsl`
([current/Content/Core/Shaders/Common/MaterialSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/MaterialSet.glsl#L12-L41);
R = AO, G = roughness, B = metallic).[^ksa-gpuobjectsystem][^ksa-materialdata][^ksa-materialset-glsl]
Each Apply creates a new record and textures, retargets observed canopies, and retires the previous
allocation set.

## Lifecycle and timing

| Step | Where (build 5554) | Lifecycle |
|---|---|---|
| Prefix installed | `[StarMapAllModsLoaded]` -> unscience `Patcher.Patch()` -> `FreeFallinPatches.Apply` | `startup:all-mods-loaded` |
| Apply / Restore Stock | Panel buttons (`RenderContent` from `[StarMapAfterGui]`) | `frame:draw-ui` |
| Apply from a save / reset to stock | Save participant restore / reset | `event:load` |
| Prefix runs | `Program.RenderGame` -> for each vehicle in frame `vehicle.UpdateParachuteRenderData(RenderedViewport)` (L4700) -> `Parachute.DrawCanopy` -> `ChuteRenderable.Draw`; also for every visible secondary viewport in `RenderViewport` (L4493). Character-portrait viewports return early | `frame:render` (mesh-bucket submission at the start of `RenderGame` command recording, before `PrePassRenderer.Render` at L4713; nothing is recorded into the prepass itself) |

The swap happens on the main thread at submission time, so a material applied in the GUI phase is
in force for the same frame, in every view and in the prepass, shadow and colour passes, because all
of them copy `MaterialIndices[i]` when the canopy is submitted. Cloth simulation is untouched: the
prefix only reads the renderable.[^ksa-program][^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [free-fallin.lib/FreeFallinPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/FreeFallinPatches.cs) | Reflected members, prefix, observe/restore/replace |
| [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L33-L119) | Apply, `MaterialData` build, Disable, retirement |
| [free-fallin.lib/CanopyMaterialSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialSettings.cs) | The saved recipe |
| [free-fallin.lib/FreeFallinSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/FreeFallinSubmod.Saves.cs) | Save participant: last applied settings + effective albedo |
| [ksa-upgrade.tests/FreeFallinChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-upgrade.tests/FreeFallinChecks.cs) | Managed check of override, late spawn and restore semantics |

## Snippets

The prefix and first-sight capture:

```csharp
// free-fallin.lib/FreeFallinPatches.cs L54-L69
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

Restore only what this mod still owns:

```csharp
// free-fallin.lib/FreeFallinPatches.cs L71-L90
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

Building and registering the runtime material:

```csharp
// free-fallin.lib/CanopyMaterialController.cs L68-L91
        var tint = effectiveAlbedo ?? AuthoredTint(settings);

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
```

The game method being prefixed:

```csharp
// current/decomp/KSA/ChuteRenderable.cs L34-L44
	public void Draw(ViewHandle view, float3[] positionsLocal, float[]? boneScales, floatQuat[]? boneDeltas, ref readonly double4x4 local2Ego, float diameterM, double dt)
	{
		float num = ((_pose.BindHemRadius > 0f) ? (0.5f * diameterM / _pose.BindHemRadius) : 1f);
		_pose.Positions = positionsLocal;
		_pose.BoneScales = boneScales;
		_pose.BoneDeltas = boneDeltas;
		_pose.InvMeshScale = 1f / num;
		_renderable.Transform = float4x4.CreateScale(num) * float4x4.Pack(in local2Ego);
		_renderable.UpdateAnimation(dt);
		_renderable.Draw(view);
	}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ChuteRenderable.Draw(ViewHandle, float3[], float[]?, floatQuat[]?, ref readonly double4x4, float, double)`, `_renderable` | [current/decomp/KSA/ChuteRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteRenderable.cs#L12-L44) | Patched (prefix); constructor selects slot 0 from `materialId` |
| `AnimatedRenderable.MaterialIndices` (protected), `Draw(ViewHandle)` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L209-L250) | Copied into colour, prepass and shadow instances (field at L32) |
| `Parachute.DrawCanopy(ViewHandle, double, ref readonly double4x4)`, `CanopyMaterial` | [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1185-L1197) | Creates the renderable with the selected material (`CanopyMaterial` at L196) |
| `Vehicle.UpdateParachuteRenderData(IViewport)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3743-L3764) | Per-viewport caller; skips portraits |
| `Program.RenderGame` / `RenderViewport` call sites | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4689-L4713) | Before the prepass (secondary viewports at L4493) |
| `GpuObjectSystem<T>.CreateObject(AssetName, T)` | [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L45-L55) | Allocates the record in `BigBuffer` |
| `MaterialData` | [current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs) | Record layout = shader ABI |
| `PbrMaterialReference.DiffuseReference` / `NormalReference` / `PBRMap` / `EmissiveMap` | [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L13-L22) | Stock maps reused |
| `ParachuteCanopy_Material_*` | [current/Content/Core/ParachuteAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/ParachuteAssets.xml#L23-L57) | Six native canopy materials; CheckerLongOrange is the source |

## Pitfalls and version notes

- **Global by design.** One material for all canopies; per-canopy styles chosen in the game are
  overridden while the mod is active and restored per renderable afterwards.
- **Reflection by name.** `_renderable` and `MaterialIndices` are string lookups that throw at type
  initialization if renamed; slot 0 must stay the canopy mesh's material. Both are unchanged in
  5554.[^scope-parachutes]
- **5438:** the single `ParachuteCanopy_Material` asset was replaced by six authored materials and a
  per-parachute selection; the controller now uses CheckerLongOrange only as the source of stock
  maps.[^ksa-parachuteassets]
- **5482:** `ChuteRenderable.Draw` gained a leading `ViewHandle`; the prefix binds only `__instance`, so
  the name-only lookup still attaches (the managed fixture still mirrors the old parameterless
  `Draw()`).[^ff-checks]
- **Retirement waits for the GPU.** Releasing the previous material and textures calls
  `Device.WaitIdle()` after observed canopies were switched, so no in-flight frame references freed
  slots; this stalls once per Apply.[^ff-controller]
- Canopies first drawn while the mod is disabled are never tracked, which is fine: they are stock.
- Settings validation and the save payload keep the last applied recipe and the effective albedo, so a
  later Humble Arteest recolour of the private material survives a load.[^ff-saves][^ff-settings]

## See also

- [Canopy texture compositing](./free-fallin-canopy-texture-compositing.md)
- [Full-canopy projection shader](./free-fallin-canopy-projection-shader.md)
- [Kitten colour](./humble-arteest-kitten-color.md) (edits material records in the same system)
- [doh runtime GPU materials](./doh-runtime-gpu-materials.md)
- [Free Fallin sub-mod](../submods/free-fallin.md) and the [mod overview](../overview.md)
- [Swap parachute canopy materials by editing MaterialIndices in a ChuteRenderable.Draw prefix](../../../howto/vehicles-and-parts/modules-and-resources/customize-parachute-materials-and-limits/customize-parachute-materials-and-limits-1.md) - how-to article demonstrating this feature

[^ff-patches]: free-fallin.lib/FreeFallinPatches.cs
[^ff-controller]: free-fallin.lib/CanopyMaterialController.cs
[^ff-settings]: free-fallin.lib/CanopyMaterialSettings.cs
[^ff-submod]: free-fallin.lib/FreeFallinSubmod.cs
[^ff-saves]: free-fallin.lib/FreeFallinSubmod.Saves.cs
[^ff-checks]: ksa-upgrade.tests/FreeFallinChecks.cs
[^scope-parachutes]: scope/parachutes.md (authors' free-fallin integration analysis)
[^ksa-chuterenderable]: KSA.ChuteRenderable (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: KSA.Parachute (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gpuobjectsystem]: KSA.GpuObjectSystem (decompiled, build 2026.10.10.5554)
[^ksa-materialdata]: KSA.MaterialData (decompiled, build 2026.10.10.5554)
[^ksa-materialset-glsl]: Common/MaterialSet.glsl (game shader header, build 2026.10.10.5554)
[^ksa-parachuteassets]: Core/ParachuteAssets.xml (game content, build 2026.10.10.5554)
