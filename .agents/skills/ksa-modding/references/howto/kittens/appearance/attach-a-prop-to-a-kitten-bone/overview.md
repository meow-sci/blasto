---
type: Game Feature
title: Attach a prop to a kitten's hand or another bone at runtime, building the prop mesh from mod assets
description: How a mod hangs its own mesh on a kitten bone with a CharacterAvatar CosmeticAttachment, built from mod glTF and material assets, and how to place it exactly on the drawn hand per view
category: kittens
group: appearance
topic: attach-a-prop-to-a-kitten-bone
demonstrated_by:
  - mods/ksagolf/features/kitten-held-props
  - mods/ksagolf/features/prop-and-course-asset-xml
  - mods/ksagolf/features/putting-address-pose
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:render]
game_types:
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
  - KSA.StaticMeshRenderable
  - KSA.AnimatedRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.LoadedAssetRef
  - KSA.GltfPbrAssetRef
  - KSA.AssetManager
  - KSA.CharacterRenderResources
  - Core.AssetName
  - KSA.IAnimProcessor
tags: [kitten, prop, cosmetic-attachment, static-mesh, anim-processor]
---
A kitten holds a prop through `CharacterAvatar.Attachments.CosmeticAttachments`, a public list of structs `{ Mesh, SocketIndex, Transform, Enabled }`. Each frame, for every view that draws the kitten, `KittenRenderable.UpdateRenderData` places each entry's mesh on its socket bone and draws it. A mod can add an entry at runtime with a `StaticMeshRenderable` it builds from its own glTF and material, and can aim it either with a static offset or by solving the transform per view from an anim processor. No `<Character>` XML, no Harmony patch and nothing in the save.

## Engine background

| Piece | Where | What it does | Source |
|---|---|---|---|
| Attachment list | `CharacterAvatar.CharacterAttachments.CosmeticAttachments`, `CosmeticAttachment` | Public list of structs; `HasAttachment` is `Mesh != null && Enabled` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L86-L104) |
| Draw loop | `KittenRenderable.UpdateRenderData` | `Mesh.Transform = attachment.Transform * (SocketTurn * bone)`, then `Draw(view)`; ignores `Enabled` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L399-L406) |
| Bone matrix | `AnimatedRenderable.GetBoneTransform` | `WorldTransformList[i] * Transform` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L301-L307) |
| Processors | `AnimatedRenderable.UpdateAnimation`, `IAnimProcessor` | `UpdateSkeleton` runs per view before skinning | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L173-L185) |
| Mesh construction | `StaticMeshRenderable(technique, AssetName, prepass, isOpaque)` | Reads material handles once from the glTF's `Materials` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L31-L60) |
| Managers | `SuperMeshRenderSystem.GltfSystem`, `MaterialSystem` | `GetOrLoad(AssetName)` for declared assets | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L72) |

Kittens draw per viewport inside `RenderGame` / `RenderViewport` (see the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)); the character XML route to the same list is described in [define a kitten character in asset XML](../../characters/define-a-kitten-character-in-asset-xml/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Static offset on a `CosmeticAttachment`, prop mesh built from the mod's glTF and material](attach-a-prop-to-a-kitten-bone-1.md) | ksagolf | `reflection` | `frame:draw-ui`, `frame:after-on-frame`, `frame:render` | Simple; no patch; one frame stale against a moving bone; the mesh and material construction has its own gotchas |
| [2. Solve the attachment transform in `UpdateSkeleton` against the view's wrist](attach-a-prop-to-a-kitten-bone-2.md) | ksagolf | `anim-processor` | `frame:draw-ui`, `frame:render` | Zero lag in every view; needs a pose processor and bone-index plumbing |

## See also

- [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [Define a kitten character in asset XML](../../characters/define-a-kitten-character-in-asset-xml/overview.md), [Pose kitten bones with an anim processor](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md)
- [Tint and restyle kitten appearance](../tint-and-restyle-kitten-appearance/overview.md), [Hide a kitten's body from one viewport](../hide-a-kitten-body-from-one-viewport/overview.md)
- [Draw mod meshes with the engine renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md)
- Mod features: [Mod meshes held by a kitten](../../../../mods/ksagolf/features/kitten-held-props.md), [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md), [Putting address pose](../../../../mods/ksagolf/features/putting-address-pose.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../../shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md) - related topic
- [List a mod in ModMenu with the ModMenuEntry attribute](../../../ui/menus/list-a-mod-in-modmenu/overview.md) - related topic

