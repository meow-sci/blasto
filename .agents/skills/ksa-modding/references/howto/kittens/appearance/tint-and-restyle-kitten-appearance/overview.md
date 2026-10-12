---
type: Game Feature
title: Tint kitten fur, body and eyes, and hide the visor, helmet or MMU
description: How mods recolour kittens by changing AlbedoColor in the shared GPU material buffer or in per-kitten material clones, and hide the visor, helmet or MMU with a transpiler or the attachment flags
category: kittens
group: appearance
topic: tint-and-restyle-kitten-appearance
demonstrated_by:
  - mods/unscience/features/humble-arteest-kitten-color
  - mods/unscience/features/doh-runtime-gpu-materials
  - mods/gatos/features/vehicle-and-eva-paint
  - mods/unscience/features/humble-arteest-kitten-visor-toggle
  - mods/ksagolf/features/putting-session
  - mods/ksagolf/features/kitten-pose-processor
lifecycle:
  - frame:draw-ui
  - frame:render
  - frame:prepare
  - frame:after-on-frame
  - event:load
  - startup:all-mods-loaded
game_types:
  - KSA.GpuMaterialSystem
  - KSA.GpuObjectSystem
  - KSA.MaterialData
  - KSA.CharacterRenderResources
  - KSA.CharacterAvatar
  - KSA.AnimatedRenderable
  - KSA.CatFurRenderable
  - KSA.KittenRenderable
  - KSA.AssetManager
  - KSA.GpuObjectAssetRef
  - KSA.PbrMaterialReference
  - Brutal.Collections.FreeListIndexPool
  - KSA.StaticMeshRenderable
  - KSA.KittenEva
  - KSA.IVASeat
tags: [kitten, appearance, material, tint, visor, helmet, mmu]
---
A kitten's look is data, and the engine reads it every frame: the colour of each mesh is an `AlbedoColor` in a shared GPU material record, the material bound to each mesh is an integer handle in a protected array on its renderable, and the helmet, visor and MMU are optional attachments gated by public flags. Mods change a kitten's appearance by writing one of those three things. This topic covers five ways: write the colour into the shared record (every kitten that uses the material changes), clone the materials per kitten and swap the handles (two flavours, one budgeted for the fixed 512-slot pool), and hide the visor, helmet or MMU (a transpiler for the visor alone, or flipping the attachment flags per avatar).

## Engine background

| Piece | Where | What it does | Source |
|---|---|---|---|
| Material pool | `Program.MaterialSystem` (`GpuMaterialSystem`, capacity 512), `GpuObjectSystem<T>` | One device-local buffer of `MaterialData`, name map `AssetMap`, no resize | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1002), [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L20-L34) |
| Material record | `MaterialData` | `AlbedoColor` at byte 16, 80-byte stride | [current/decomp/KSA/MaterialData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MaterialData.cs#L6-L20) |
| Slot allocation | `GpuObjectSystem.CreateObject`, `Free` | Allocates before it knows the name is new; frees by handle | [current/decomp/KSA/GpuObjectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectSystem.cs#L45-L83) |
| Mesh binding | `AnimatedRenderable.MaterialIndices`, `CatFurRenderable.MaterialIndices`, `StaticMeshRenderable.MaterialIndices` | Protected int arrays of handles read at draw time | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L32) |
| Fur material | `CharacterRenderResources.CreateFurMaterial` | Needs fur texture, sampler and mask in `ExtraData` | [current/decomp/KSA/CharacterRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterRenderResources.cs#L54-L71) |
| Shaders | `ModelPbr.frag`, `ModelTranslucent.frag` | Alpha under 0.1 discards; glass and eyes ignore alpha | [current/Content/Core/Shaders/Mesh/ModelPbr.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelPbr.frag#L66-L67), [current/Content/Core/Shaders/Mesh/ModelTranslucent.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelTranslucent.frag#L72-L81) |
| Attachments | `CharacterAvatar.Helmet`, `Mmu` (`Enabled`, `HasAttachment`) | Gate the helmet shell, visor and MMU draws | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L103-L138) |
| Draw site | `KittenRenderable.UpdateRenderData` | Draws body, fur, helmet, visor, MMU, cosmetics per view | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L356-L371) |

Kittens are drawn per viewport inside `RenderGame` / `RenderViewport`, not in `UpdateRenderingResources`, so material writes and flag flips made in the UI phase apply to the same frame (see the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). After a load the native world is rebuilt and material handles are not stable; see [restore kitten customizations after a load](../../characters/restore-kitten-customizations-after-a-load/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Staged one-float4 write into the shared material buffer](tint-and-restyle-kitten-appearance-1.md) | unscience (Humble Arteest) | `vulkan-buffer` | `frame:draw-ui`, `event:load` | Cheapest; changes every kitten using the material (and any other material you write); glass and eyes barely respond |
| [2. Clone the materials per kitten and swap `MaterialIndices`, inside the 512-slot pool](tint-and-restyle-kitten-appearance-2.md) | unscience (doh) | `reflection` | `frame:draw-ui`, `event:load`, `frame:prepare` | Per-kitten colour; needs a budget, a reserve and by-name release because the pool never grows |
| [3. Mod-owned material clones bound to the EVA renderables](tint-and-restyle-kitten-appearance-3.md) | gatos | `reflection` | `frame:draw-ui`, `frame:after-on-frame` | Minimal and reversible; clone pool keyed by (source, colour) and capped by a setting |
| [4. Transpile the single `VisorMesh.Draw` call into a gated forwarder](tint-and-restyle-kitten-appearance-4.md) | unscience (Humble Arteest) | `harmony-transpiler` | `startup:all-mods-loaded`, `frame:render` | Hides only the visor, in every view, for every kitten; install fails loudly if the IL shape changes |
| [5. Toggle `Helmet.Enabled` and `Mmu.Enabled`](tint-and-restyle-kitten-appearance-5.md) | ksagolf | `reflection` | `frame:draw-ui`, `frame:render` | No patch, per avatar, trivially reversible; hides helmet and visor together, not the visor alone |

## See also

- [Restore kitten customizations after a load](../../characters/restore-kitten-customizations-after-a-load/overview.md), [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [Spawn an EVA kitten from code](../../characters/spawn-an-eva-kitten-from-code/overview.md)
- [Hide a kitten's body from one viewport](../hide-a-kitten-body-from-one-viewport/overview.md), [Attach a prop to a kitten bone](../attach-a-prop-to-a-kitten-bone/overview.md)
- [Paint vessel parts with custom colours](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md), [Replace a game texture at runtime](../../../rendering/textures-and-decals/replace-a-game-texture-at-runtime/overview.md), [Grow game GPU buffers for runtime content](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md)
- Mod features: [Kitten Color](../../../../mods/unscience/features/humble-arteest-kitten-color.md), [doh runtime GPU materials](../../../../mods/unscience/features/doh-runtime-gpu-materials.md), [Vehicle and EVA paint](../../../../mods/gatos/features/vehicle-and-eva-paint.md), [Kitten visor toggle](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md), [Putting session](../../../../mods/ksagolf/features/putting-session.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
