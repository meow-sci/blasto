---
type: Mod Feature
title: Swap the meshes and textures of existing planetary rings at runtime
description: Rocky McRock Face mutates the public XML-backed ring reference tree of a ringed body (LOD meshes, rock material, band texture, field settings), snapshots the originals, and recreates the rings renderer so the constructor-baked ring data is rebuilt from the edit
mod: unscience
submod: rocky-mcrock-face
category: celestials
techniques: [reflection, xml-asset, vulkan-buffer]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Apply / Restore Defaults buttons)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; ringed-body rescan every 2 s)
  - reflection KSA.Program._planetTransparenciesRenderer
  - reflection KSA.PlanetTransparenciesRenderer._ringsRenderer
  - reflection KSA.PlanetTransparenciesRenderer._ringRendererCreated
lifecycle: [frame:draw-ui, frame:prepare, render:scene-opaque, render:atmosphere]
game_types:
  - KSA.PlanetaryRingsReference
  - KSA.RingObjectsReference
  - KSA.RingLodReference
  - KSA.MeshFileReference
  - KSA.PbrMaterialReference
  - KSA.TexturePowerReference
  - KSA.DistanceReference
  - KSA.DoubleReference
  - KSA.Program
  - KSA.PlanetTransparenciesRenderer
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderData
  - KSA.Universe
tags: [rings, celestials, renderer-rebuild, reflection, meshes, textures]
---

Saturn's rings are a flat band plus an instanced rock field: a handful of rock meshes (one per LOD,
at most 5) that GPU culling replicates across a 40x40x40 chunk grid around the camera. Rocky
McRock Face lets a player pick any loaded mesh per LOD (fuel tanks, kitten chairs, the kitten
itself), any bound texture for the rock material, a new band texture, and new size, density, draw
distance and field thickness; Apply makes the ring re-appear with those assets and Restore
Defaults puts the stock ring back.[^rocky-readme]

Mechanism in one line: the whole ring definition is public XML-backed data on the body template,
read only when the rings renderer is constructed, so the mod writes the new assets into that tree
and then forces the rings renderer to be recreated through `Program.RebuildRenderer()`.

## How it works

The data path is all public fields:
`Celestial.BodyTemplate.RingsReference` (`PlanetaryRingsReference`) → `RingObjects`
(`RingObjectsReference`: `Lods`, `MaterialReference`, `Size`, `Thickness`, `RenderDistance`,
`Density`) → `Lods[i].MeshFileReference.Get().Mesh` (`MeshReference`, the swap slot), plus
`PbrMaterialReference.DiffuseReference` / `NormalReference` / `PBRMap` and the band
`PlanetaryRingsReference.Texture`.[^ksa-ring-objects-reference][^ksa-mesh-file-reference][^ksa-pbr-material-reference]

1. **Discover and snapshot.** `RefreshBodies` scans `Universe.CurrentSystem.All.OfType<Celestial>()`
   for templates with a `RingsReference` that has LODs, and snapshots each tree once, keyed by the
   `PlanetaryRingsReference` instance: per-LOD meshes, the three material textures, the band texture
   and the four field-setting reference objects.[^ring-swap-controller]
2. **Resolve everything first.** `Apply` resolves every selected mesh (converted to a ring-usable
   mesh when needed) and texture from the asset catalog before writing anything, so a failed lookup
   leaves the game untouched.
3. **Mutate the tree.** It assigns `MeshFileReference.Mesh` per LOD, the three material textures,
   the band texture when one was chosen, and either new `DistanceReference` / `DoubleReference`
   objects for the field settings or the snapshotted originals. New objects are assigned rather
   than edited in place, so the snapshot objects stay pristine.
4. **Recreate the rings renderer.** `RebuildRenderer` reflects
   `Program._planetTransparenciesRenderer`, and if `_ringRendererCreated` is true it calls
   `Device.WaitIdle()`, `PlanetaryRingsRenderer.Dispose()` and clears the flag; then it calls
   `Program.RebuildRenderer()`, whose `PlanetTransparenciesRenderer.RebuildFrameResources` takes
   the `CreateRingsRenderer` branch because the flag is now false and `_anyRings` is already
   true.[^ksa-planet-transparencies]
5. **Free unused clones.** The UI then prunes converted meshes that no selection references.
6. **Restore** writes the snapshot back and rebuilds the same way.

Why the rebuild is required (the constructor-baking invariant): the `PlanetaryRingsRenderData`
constructor copies `Lods[i].MeshFileReference.Get().Mesh` into `_meshLods`, the maximum
`BoundingSphereRadius` into the culling radius, `DeviceMesh.IndexCount` per LOD, the material
`BindlessHandle`s and the field settings into a per-frame UBO, binds the band and control image
views into a descriptor set, and sizes the chunk instance buffer from density times draw distance
cubed.[^ksa-rings-render-data] It is only constructed from `PlanetaryRingsRenderer.PopulatePlanets`,
which only runs in the renderer's constructor; `PlanetaryRingsRenderer.RebuildFrameResources`
recreates frame resources only.[^ksa-rings-renderer] Without the dispose step a
`RebuildRenderer()` call hitches but changes nothing, which the authors record as the original
symptom.[^scope-rings]

Rocky does not touch `PlanetTransparenciesRenderer.PopulatePlanets` or `_anyRings`: it never adds
or removes a ring, so the set of ringed bodies is unchanged. That is the difference from Bloomin'
Onion's rebuild, which must handle bodies gaining or losing rings.

The control texture is deliberately not offered: `PlanetaryRingsRenderData.UpdateData` CPU-samples
it every frame assuming four bytes per texel, while the band is only GPU-sampled and safe to
swap.[^ksa-rings-render-data]

## Lifecycle and timing

| Path | Where | Phase |
|---|---|---|
| Apply / Restore Defaults | `ApplySelection` / `RestoreDefaults` in `RenderContent`, from `[StarMapAfterGui]` | `frame:draw-ui` |
| Ringed-body rescan, first catalog scan | `ISubmod.Update`, every 2 s | `frame:draw-ui` |
| Save restore | `RestoreRingSwaps` → `Apply` per body, then one `RebuildRenderer`, replayed at the physics handoff in `PrepareFrame` | `frame:prepare` |
| Rock field drawn from the rebuilt data | `RenderRingMeshes` in `RenderGame` | `render:scene-opaque` |
| Band, ring shadow and atmosphere shadow | `PlanetTransparenciesRenderer.Render`, `PlanetRenderer.Render`, `AtmosphereRenderer.FillPlanetAtmosphereData` | `render:atmosphere` |

The button path calls `Program.RebuildRenderer()` directly from inside the ImGui frame, before the
frame acquires a swapchain image or records commands. `WaitIdle` before the dispose and
`RebuildRenderer`'s own `WaitIdle` calls (L5112, L5210) drain in-flight frames that may still
reference the old ring buffers and pipelines.[^ksa-program] The full analysis of that call versus
the game's deferred `ScheduleRendererRebuild()` is in
[runtime planetary rings](./bloomin-onion-runtime-planetary-rings.md#lifecycle-and-timing).

A band swap is visible to the planet's ring shadow and to the atmospheric ring shadow on the next
recorded frame without any rebuild, because both read `RingsReference.Texture` every frame for the
nearby or atmospheric body.[^ksa-atmosphere-renderer]

## Key code

| Path | Role |
|---|---|
| [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs) | Snapshot, apply, restore, dispose-and-rebuild |
| [rocky-mcrock-face.lib/RingSelection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSelection.cs) | Per-body override model ("" keeps the stock asset) |
| [rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Ui.cs#L211-L230) | Apply, rebuild, prune order |
| [rocky-mcrock-face.lib/RockyMcRockFaceSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RockyMcRockFaceSubmod.cs#L35-L75) | Throttled rescan; clone pruning |
| [rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Saves.cs) | Applied selections saved and replayed |

## Snippets

Writing the selection into the public reference tree:

```csharp
// rocky-mcrock-face.lib/RingSwapController.cs L129-L152
        for (int i = 0; i < lodCount; i++)
        {
            var fileReference = objects.Lods[i].MeshFileReference?.Get();
            if (fileReference != null) fileReference.Mesh = meshes[i];
        }
        objects.MaterialReference.DiffuseReference = diffuse;
        objects.MaterialReference.NormalReference = normal;
        objects.MaterialReference.PBRMap = pbr;
        if (band != null) rings.Texture = band;

        if (selection.OverrideFieldSettings)
        {
            objects.Size = new DistanceReference(selection.SizeM);
            objects.Density = DoubleReference.FromValue(selection.DensityPerKm3);
            objects.RenderDistance = new DistanceReference(selection.RenderDistanceKm, DistanceUnit.Kilometers);
            objects.Thickness = new DistanceReference(selection.ThicknessKm, DistanceUnit.Kilometers);
        }
        else
        {
            objects.Size = defaults.Size;
            objects.Density = defaults.Density;
            objects.RenderDistance = defaults.RenderDistance;
            objects.Thickness = defaults.Thickness;
        }
```

Forcing the rings renderer to be recreated:

```csharp
// rocky-mcrock-face.lib/RingSwapController.cs L208-L249
    public bool RebuildRenderer(out string message)
    {
        try
        {
            var program = Program.Instance;
            if (program == null)
            {
                message = "game renderer not ready";
                return false;
            }
            DisposeRingsRendererForRecreation();
            program.RebuildRenderer();
            message = "renderer rebuilt";
            return true;
    // ...
    private static void DisposeRingsRendererForRecreation()
    {
        var transparencies = ReflectionHelpers.GetFieldValue(Program.Instance, "_planetTransparenciesRenderer");
        if (transparencies == null) return;
        if (ReflectionHelpers.GetFieldValue(transparencies, "_ringRendererCreated") is not true) return;
        if (ReflectionHelpers.GetFieldValue(transparencies, "_ringsRenderer") is not PlanetaryRingsRenderer ringsRenderer)
            return;
        // In-flight frames may still reference ring pipelines/buffers.
        Program.GetRenderer().Device.WaitIdle();
        ringsRenderer.Dispose();
        ReflectionHelpers.SetFieldValue(transparencies, "_ringRendererCreated", false);
    }
```

What the game bakes at construction, and why the rebuild is needed (build 5554):

```csharp
// current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs L204-L240
		_meshLods = new MeshReference[_reference.RingObjects.NumLods];
		for (int i = 0; i < _reference.RingObjects.NumLods; i++)
		{
			_meshLods[i] = _reference.RingObjects.Lods[i].MeshFileReference.Get().Mesh;
		}
		double num = 0.0;
		for (int j = 0; j < _reference.RingObjects.NumLods; j++)
		{
			double boundingSphereRadius = _reference.RingObjects.Lods[j].MeshFileReference.Get().Mesh.BoundingSphereRadius;
			num = double.Max(boundingSphereRadius, num);
		}
		// ...
			reference2.LodCount = _reference.RingObjects.NumLods;
			for (int l = 0; l < _reference.RingObjects.NumLods; l++)
			{
				reference2.LodProperties[l].X = _reference.RingObjects.Lods[l].MinScreenSizePixels;
				reference2.LodProperties[l].Y = _meshLods[l].DeviceMesh.IndexCount;
			}
			reference2.LodProperties[5].Y = 8f;
			reference2.MeshDiffuseId = _reference.RingObjects.MaterialReference.DiffuseReference.Get().BindlessHandle;
			reference2.MeshNormalId = _reference.RingObjects.MaterialReference.NormalReference.Get().BindlessHandle;
			reference2.MeshPbrId = _reference.RingObjects.MaterialReference.PBRMap.Get().BindlessHandle;
```

The order in the panel: mutate, rebuild, then free clones that are no longer referenced:

```csharp
// rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Ui.cs L211-L221
    private void ApplySelection(RingedBody body, RingSelection selection)
    {
        if (!_controller.Apply(body, selection, out var message))
        {
            SetStatus(message, true);
            return;
        }
        bool rebuilt = _controller.RebuildRenderer(out var rebuildMessage);
        if (rebuilt) PruneUnusedMeshClones();
        SetStatus($"{message}; {rebuildMessage}", !rebuilt);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `RingObjectsReference.{Lods, MaterialReference, Size, Thickness, RenderDistance, Density, NumLods}` | [current/decomp/KSA/RingObjectsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RingObjectsReference.cs#L7-L31) | Swapped fields |
| `MeshFileReference.Mesh` / `Get()` | [current/decomp/KSA/MeshFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshFileReference.cs#L14-L31) | Per-LOD mesh slot |
| `PbrMaterialReference.{DiffuseReference, NormalReference, PBRMap}` | [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L13-L19) | Rock material textures |
| `PlanetaryRingsRenderData` constructor | [current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs#L180-L307) | The baking that forces a rebuild |
| `PlanetaryRingsRenderer.PopulatePlanets`, `RebuildFrameResources`, `Dispose`, `RenderMeshes` | [current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs#L324-L345) | Constructor-only population; `RebuildFrameResources` (L449) does not repopulate; `RenderMeshes` (L571) draws `DeviceMesh` per LOD |
| `PlanetTransparenciesRenderer._ringsRenderer` / `_ringRendererCreated` (private) and `RebuildFrameResources` | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L398-L412) | Create branch reached after the dispose |
| `Program._planetTransparenciesRenderer` (private), `Program.RebuildRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5211) | The rebuild routine |
| `AtmosphereRenderer.FillPlanetAtmosphereData` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1154-L1162) | Live band reader (Saturn is an atmospheric body) |
| Saturn `<RingObjects>` XML | [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L4291-L4300) | Stock LODs are inline `<Mesh Id= Path=>` elements |

## Pitfalls and version notes

- Field renames of `_ringsRenderer` / `_ringRendererCreated` / `_planetTransparenciesRenderer`
  make the dispose step silently skip: Apply then hitches without effect. All three exist in 5554.
- Only primitive 0 of each LOD mesh is drawn (`RenderMeshes` binds `DeviceMesh`, which is
  `DevicePrimitives[0]`), with the single rock material; multi-material part meshes lose their
  other parts.[^ksa-rings-renderer]
- The swap writes into whatever `MeshFileReference.Get()` returns. Saturn's LODs are inline
  definitions with their own `Path` (each ring owns its objects), so the swap stays local. A ring
  whose LOD referenced a shared mesh entry by id would resolve `Get()` to the library object and
  the swap would leak to its other users (inferred from `MeshFileReference.Get`, not observed).[^ksa-astronomicals-xml]
- Never set the band to null on an atmospheric body: `FillPlanetAtmosphereData` dereferences
  `Texture.Get()` every frame. Restore only assigns a non-null snapshot.
- The band shadows use the band's alpha, so an opaque part texture casts a solid dark band.
  The distant-sphere renderer keeps the stock band after a swap (no sync, unlike Bloomin' Onion).
- Converted clones hold GPU buffers and have a finalizer (`MeshReference.~MeshReference`). They are
  disposed only after defaults are restored and the renderer rebuilt, and cached for the
  controller's lifetime so the GC cannot free a mesh the renderer still uses.
- Overrides on a Bloomin' Onion ring are possible because any template with ring LODs is listed;
  the save adapters restore Bloomin' Onion first, then Rocky, and reset in the opposite order.[^rocky-saves]

## See also

- [Runtime planetary rings](./bloomin-onion-runtime-planetary-rings.md) (rebuild timing analysis, adding rings to new bodies)
- [Interleaved mesh clone](./rocky-mcrock-face-interleaved-mesh-clone.md) (how part meshes become ring-usable)
- [Ring asset catalog](./rocky-mcrock-face-asset-catalog.md) (where the mesh and texture ids come from)
- [Painted ring textures](./bloomin-onion-painted-ring-textures.md)
- [Rocky McRock Face sub-mod](../submods/rocky-mcrock-face.md) and [mod overview](../overview.md)
- [Mutate the public ring reference tree, snapshot originals and recreate the rings renderer](../../../howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-2.md) - how-to article demonstrating this feature
- [Snapshot, mutate the public tree and recreate one renderer by reflection](../../../howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-1.md) - how-to article demonstrating this feature

[^rocky-readme]: rocky-mcrock-face/README.md
[^ksa-ring-objects-reference]: KSA.RingObjectsReference (decompiled, build 2026.10.10.5554)
[^ksa-mesh-file-reference]: KSA.MeshFileReference (decompiled, build 2026.10.10.5554)
[^ksa-pbr-material-reference]: KSA.PbrMaterialReference (decompiled, build 2026.10.10.5554)
[^ring-swap-controller]: rocky-mcrock-face.lib/RingSwapController.cs
[^ksa-planet-transparencies]: KSA.PlanetTransparenciesRenderer (decompiled, build 2026.10.10.5554)
[^ksa-rings-render-data]: KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderData (decompiled, build 2026.10.10.5554)
[^ksa-rings-renderer]: KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer (decompiled, build 2026.10.10.5554)
[^scope-rings]: scope/rings.md (authors' ring integration analysis)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-atmosphere-renderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
[^ksa-astronomicals-xml]: Content/Core/Astronomicals.xml (Saturn ring definition, build 2026.10.10.5554)
[^rocky-saves]: rocky-mcrock-face.lib/RockyMcRockFaceSubmod.Saves.cs
