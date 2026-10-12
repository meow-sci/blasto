---
type: Mod Feature
title: Mod meshes held by a kitten as cosmetic attachments
description: Hang a mod's own glTF on a kitten bone at runtime by adding it to CharacterAvatar.Attachments.CosmeticAttachments, building the StaticMeshRenderable the way the engine builds Core's, with the material slot filled first and an optional see-through visor-glass technique
mod: ksagolf
category: kittens
techniques: [reflection, glb-model, xml-asset, runtime-attachment, starmap-lifecycle]
hooks:
  - starmap StarMapAfterOnFrame
  - starmap StarMapAfterGui
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection KSA.SuperMeshRenderSystem.GltfSystem/MaterialSystem GetOrLoad(Core.AssetName)
  - reflection KSA.StaticMeshRenderable..ctor(IMeshRenderer, Core.AssetName, IMeshRenderer, bool)
lifecycle: [frame:after-on-frame, frame:draw-ui, frame:render]
game_types:
  - KSA.CharacterAvatar
  - KSA.KittenRenderable
  - KSA.StaticMeshRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.LoadedAssetRef
  - KSA.GltfPbrAssetRef
  - KSA.GpuObjectAssetRef
  - KSA.AssetManager
  - KSA.CharacterRenderResources
  - KSA.AnimatedRenderable
  - Core.AssetName
tags: [kitten, attachment, prop, mesh, gltf, material, glass, translucent, reflection, bridge]
---

KSAGolf puts a putter in a kitten's paw without declaring a `<Character>` and without patching
anything. `CharacterAvatar.Attachments.CosmeticAttachments` is a public `List`. Every frame the
kitten renderer places each entry on its socket bone and draws it.[^ksa-kittenrenderable] The mod
adds an entry at runtime, with a `StaticMeshRenderable` it builds itself from the mod's own
`<GltfFile>` and `<PbrMaterial>`.

There are three pieces:

- `Ksa/AttachmentMesh.cs` builds the renderable the way `CharacterAvatar` builds Core's own
  attachments. It can also build a see-through variant on the helmet visor's glass technique.
- `Ksa/KittenPropProbe.cs` is the developer probe behind the bridge's `kitten_prop` command. It hangs
  either a copy of Core's helmet or any declared mod pair on any bone.
- `Ksa/Golf.cs` uses the same calls to hang `KSAGolf_Putter` on `Wrist_R`, and `GolfPose` aims it
  each view (see [Putting address pose](../features/putting-address-pose.md)).

## How it works

**The engine's placement rule.** For each cosmetic attachment,
`KittenRenderable.UpdateRenderData` reads `GetBoneTransform(SocketIndex)` and writes
`Mesh.Transform = attachment.Transform * (RotZ(-90°) * RotX(-90°) * bone)`. It then calls
`Mesh.Draw(view)` straight away.[^ksa-kittenrenderable] So the mesh's own `Transform` is overwritten
every frame. The attachment's `Transform` field, which the engine sets once at construction and
then only composes, is what a mod writes to aim the prop.

**Building the renderable (`AttachmentMesh.TryBuild`).**

1. Find the asset-name type without referencing its assembly. The type is read off
   `LoadedAssetRef.Id`'s property type, which is `Core.AssetName`.[^ksa-loadedassetref] The mod's
   own comment says this type lives in `Planet.Core.dll`, which the mod does not
   reference.[^golf-attachmentmesh]
2. Load the glTF and the material through the public `SuperMeshRenderSystem.GltfSystem` and
   `MaterialSystem` fields. Each is an `AssetManager<T>`, and the call is to its
   `GetOrLoad(AssetName)`, invoked by reflection.[^ksa-supermesh][^ksa-assetmanager]
3. **Fill every material slot before building.** The code writes the material into each slot of the
   cached `GltfPbrAssetRef.Materials`. The `StaticMeshRenderable` constructor reads
   `Materials[MaterialIndices[i]].Handle` once into its own `MaterialIndices`, so a material written
   afterwards never reaches that renderable.[^ksa-staticmesh] An exported `.glb` must name a
   material slot, because that is what gives the `Materials` array a length.
4. Construct `StaticMeshRenderable(technique, AssetName, MeshRendererStaticPrePass, isOpaque)` with
   `Activator`, because the constructor takes an `AssetName`. The technique is
   `MeshRendererStaticPbr` for a solid mesh. With `seeThrough: true` it is
   `Program.Instance.CharacterRenderResources.GlassRenderer`, with `isOpaque: false` and
   `CastShadows = false`. That is exactly how `CharacterAvatar` builds the helmet's
   visor.[^ksa-characteravatar][^ksa-charrenderres] The course builder's tinted ghost pieces use this
   path.

**The probe (`KittenPropProbe.Apply`).** It composes the attachment transform as
`Scale * RotX * RotY * RotZ * Translation`, or takes a whole row-major `matrix`. Then, for every
EVA kitten:

- It resolves the socket bone by name.
- It builds the mesh once per avatar. `helmet` clones Core's helmet through its own asset id.
  Anything else is `<mesh>_Glb` plus `<mesh>_Material`.
- It adds a new `CosmeticAttachment { Mesh, SocketIndex, Transform }`, or, for a mesh already there,
  reads the struct out of the list, changes it and writes it back.

The reply carries the matrix the render last drew with (`mesh.Transform`), the attachment matrix and
the drawn bone positions (`AnimatedRenderable.GetBoneTransform`). Those were the numbers used to
solve the putter's seat in the paw.[^golf-propprobe]

**The asset ids it loads.** `KSAGolfProps.xml` declares `KSAGolf_Ball_Glb` with
`KSAGolf_Ball_Material` and `KSAGolf_Putter_Glb` with `KSAGolf_Putter_Material`. Each is one mesh
with one primitive, authored in centimetres, because the kitten is drawn through
`CharacterCore.Scale = 0.01`.[^golf-propsxml][^ksa-characteravatar] The XML itself is documented in
[Prop and course asset XML](../features/prop-and-course-asset-xml.md). The flown result recorded in
KITTEN-ARMS.md: both props draw at the wrist with their own textures, the putter's −Z hangs down on
the right wrist, and at scale 0.45 its 39 cm reaches the ground.[^golf-kittenarms]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| `kitten_prop` builds or moves a prop | `Bridge.Update` from `[StarMapAfterOnFrame]` | `frame:after-on-frame` |
| `Golf.Hold` builds the putter and adds it on `Wrist_R` | `Golf.Step` from `KSAGolfMod.StepOnce`: the `[StarMapAfterGui]` pass when the HUD is shown, otherwise the `OnFrameCelestials` prefix or the frame postfix | `frame:draw-ui` (usually) |
| The engine places and draws every attachment | `KittenEva.UpdateRenderData` inside `Program.RenderGame` / `RenderViewport`, once per view, after the pose processors have run | `frame:render` |
| `GolfPose.UpdateSkeleton` rewrites the putter's attachment `Transform` | Same render call, per view, just before the attachment loop | `frame:render` |

Building a `StaticMeshRenderable` registers its meshes with the pass bucket system. Removing one has
to `Dispose()` it, which unregisters them.[^ksa-staticmesh] Both happen on the main thread outside
the render pass. Writing an attachment's `Transform` from a StarMap hook works for a static offset
(the probe), but it is one frame stale against a moving bone. `GolfPose` instead writes it from
`UpdateSkeleton`, which the engine calls after that view's skeleton is built and before the
attachment loop reads it.[^ksa-animatedrenderable]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/AttachmentMesh.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/AttachmentMesh.cs#L26-L80) | `TryBuild` (solid or see-through), `NameOf`, reflective `GetOrLoad` |
| [src/KSAGolf/Ksa/KittenPropProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPropProbe.cs#L20-L107) | `kitten_prop`: compose the local transform, add or update the attachment, report what was drawn |
| [src/KSAGolf/Ksa/KittenPropProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPropProbe.cs#L149-L179) | Clone Core's helmet as a stand-in whose materials are already resolved |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L471-L511) | `Hold`: the putter on `Wrist_R`, MMU and helmet hidden while golfing |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L642-L676) | Ghost pieces built `seeThrough: true` with a tint material, solid pieces with the shared atlas |
| [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml) | The two `<GltfFile>` / `<PbrMaterial>` pairs |

## Snippets

```csharp
// src/KSAGolf/Ksa/AttachmentMesh.cs L38-L62
            SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
            object gltfName = Activator.CreateInstance(AssetNameType, gltfId)!;
            object materialName = Activator.CreateInstance(AssetNameType, materialId)!;

            if (Load(system, "GltfSystem", gltfName) is not GltfPbrAssetRef gltf)
            {
                why = $"glTF '{gltfId}' did not load";
                return false;
            }

            if (Load(system, "MaterialSystem", materialName) is not GpuObjectAssetRef material)
            {
                why = $"material '{materialId}' did not load";
                return false;
            }

            // An exported .glb names a material slot, which is what gives this array a length.
            for (int i = 0; i < gltf.Materials.Length; i++) gltf.Materials[i] = material;

            object technique = seeThrough ? Program.Instance.CharacterRenderResources.GlassRenderer : system.MeshRendererStaticPbr;
            mesh = (StaticMeshRenderable?)Activator.CreateInstance(
                typeof(StaticMeshRenderable), technique, gltfName, system.MeshRendererStaticPrePass, !seeThrough);
            if (mesh is null) why = "the renderable could not be built";
            else if (seeThrough) mesh.CastShadows = false;
            return mesh is not null;
```

```csharp
// src/KSAGolf/Ksa/AttachmentMesh.cs L75-L80
    private static object? Load(SuperMeshRenderSystem system, string managerField, object name)
    {
        object? manager = system.GetType().GetField(managerField, BindingFlags.Instance | BindingFlags.Public)?.GetValue(system);
        MethodInfo? load = manager?.GetType().GetMethod("GetOrLoad", [AssetNameType!]);
        return load?.Invoke(manager, [name]);
    }
```

```csharp
// src/KSAGolf/Ksa/KittenPropProbe.cs L78-L89
                list.Add(new CharacterAvatar.CosmeticAttachment { Mesh = mesh, SocketIndex = socket, Transform = local });
                Props.Add((avatar, mesh!, kind));
            }
            else
            {
                // A struct in a list: read it, change it, write it back.
                int i = list.FindIndex(a => ReferenceEquals(a.Mesh, mesh));
                if (i < 0) continue;
                CharacterAvatar.CosmeticAttachment a = list[i];
                a.SocketIndex = socket;
                a.Transform = local;
                list[i] = a;
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `CharacterAvatar.Attachments.CosmeticAttachments`, `CosmeticAttachment { Mesh, SocketIndex, Transform }` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L73-L101) | The public list and the struct the mod adds |
| `CharacterAvatar.SetupAttachmentsFromRenderRef` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L447-L523) | Reference build: materials into slots, then `new StaticMeshRenderable`; visor on `GlassRenderer`, `isOpaque: false`, no shadow |
| `KittenRenderable.UpdateRenderData` (attachment loop) | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L399-L404) | Overwrites `Mesh.Transform` from the attachment transform and the bone, then draws |
| `StaticMeshRenderable` constructor, `Draw`, `CastShadows`, `Dispose` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L31-L54) | Captures material handles at construction; registers mesh buckets |
| `SuperMeshRenderSystem.GltfSystem` / `MaterialSystem` / `MeshRendererStaticPbr` / `MeshRendererStaticPrePass` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L42-L84) | Asset managers and the default static techniques |
| `AssetManager<T>.GetOrLoad(AssetName)` | [current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs#L49) | Cached load of a declared asset id |
| `LoadedAssetRef.Id` | [current/decomp/KSA/LoadedAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LoadedAssetRef.cs) | Source of the `AssetName` type and of the helmet's id |
| `GltfPbrAssetRef.Materials` | [current/decomp/KSA/GltfPbrAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GltfPbrAssetRef.cs) | The shared material-slot array filled before construction |
| `CharacterRenderResources.GlassRenderer` | [current/decomp/KSA/CharacterRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterRenderResources.cs#L26) | Translucent technique used for the visor and the see-through ghost |
| `Core.AssetName(string)` | [current/decomp/Core/AssetName.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/AssetName.cs) | Asset id struct, constructed by `Activator` |
| `AnimatedRenderable.GetBoneTransform` | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L301) | Drawn bone matrices in the probe's report |

## Pitfalls and version notes

- **The cached glTF is shared.** `GetOrLoad` returns one `GltfPbrAssetRef` per id, so filling its
  `Materials` changes it for every renderable built from it afterwards. Fill the slots immediately
  before each construction, as `TryBuild` does. That is also what lets one glTF back a solid course
  piece and a tinted see-through ghost.
- **No material slot means no slots.** A `.glb` exported without a material has an empty
  `Materials` array. The loop then fills nothing and, according to KITTEN-ARMS.md, the build
  throws.[^golf-kittenarms]
- **Units, primitives and node transforms.** The mod's notes say an attachment must be in
  centimetres baked into the vertices, with one mesh and one primitive, because `GltfPbrSystem`
  aliases index buffers across primitives.[^golf-modnotes] Check this against your own export; it
  was not re-derived from the decomp here.
- **Hiding a prop means removing it.** The attachment loop in `KittenRenderable.UpdateRenderData`
  does not check `CosmeticAttachment.Enabled` or `HasAttachment`, so every entry in the list is drawn.
- **Only on camera.** The engine draws attachments inside the kitten's render, so a kitten no view
  draws has no prop drawn either.
- **After a second save load.** KITTEN-ARMS.md records that a prop attached after the second save
  load in one session draws nothing, with the draw matrix stuck at identity. A fresh launch works.
  The cause is unknown (unverified here).
- **Why not a `<Character>`.** The mod's notes give three reasons. A mod character is dressed onto
  about a quarter of every roster. Removing the mod later terminates the game on nearly every save.
  A manifest that loads the mod before Core makes the character throw at load. A runtime attachment
  leaves nothing in the save.[^golf-modnotes]
- **Version notes.** Built against 2026.9.22.5482. In 2026.10.10.5554 the glass technique's pipeline
  layout changed (one fewer descriptor set) and `StaticMeshRenderable.Draw` now fills
  `ShadowRenderable.Center`. Neither changes the calls this feature makes, and `GlassRenderer`,
  `GltfSystem`, `MaterialSystem` and the constructor signature are unchanged.[^ksa-charrenderres]

## See also

- [Prop and course asset XML](../features/prop-and-course-asset-xml.md)
- [GLB authoring pipeline and mesh checks](../features/glb-authoring-and-mesh-checks.md)
- [Putting address pose drawn on the kitten skeleton](../features/putting-address-pose.md)
- [Drawing a mod's static mesh into every viewport](../features/mod-mesh-draw-hook.md)
- [Posing kitten bones through the pose-processor list](../features/kitten-pose-processor.md)
- [File command bridge and MCP server](../features/file-command-bridge.md)
- [Add a CosmeticAttachment with a static offset and build the prop mesh from the mod's glTF and material](../../../howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/attach-a-prop-to-a-kitten-bone-1.md) - how-to article demonstrating this feature
- [Compose a Character from the shared asset blocks](../../../howto/kittens/characters/define-a-kitten-character-in-asset-xml/define-a-kitten-character-in-asset-xml.md) - how-to article demonstrating this feature

[^golf-propprobe]: src/KSAGolf/Ksa/KittenPropProbe.cs
[^golf-attachmentmesh]: src/KSAGolf/Ksa/AttachmentMesh.cs
[^golf-propsxml]: src/KSAGolf/KSAGolfProps.xml
[^golf-kittenarms]: docs/KITTEN-ARMS.md
[^golf-modnotes]: docs/KSA-MODDING-NOTES.md
[^ksa-kittenrenderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-characteravatar]: KSA.CharacterAvatar (decompiled, build 2026.10.10.5554)
[^ksa-staticmesh]: KSA.StaticMeshRenderable (decompiled, build 2026.10.10.5554)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-loadedassetref]: KSA.LoadedAssetRef (decompiled, build 2026.10.10.5554)
[^ksa-assetmanager]: KSA.AssetManager (decompiled, build 2026.10.10.5554)
[^ksa-charrenderres]: KSA.CharacterRenderResources (decompiled, build 2026.10.10.5554)
[^ksa-animatedrenderable]: KSA.AnimatedRenderable (decompiled, build 2026.10.10.5554)
