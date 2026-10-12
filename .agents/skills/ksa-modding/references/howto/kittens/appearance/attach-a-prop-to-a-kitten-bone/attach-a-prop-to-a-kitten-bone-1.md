---
type: How-To
title: Add a CosmeticAttachment with a static offset and build the prop mesh from the mod's glTF and material
description: Build a StaticMeshRenderable from mod-declared GltfFile and PbrMaterial assets, fill its material slots first, and add or update a CosmeticAttachment on a named bone by read, change and write back
category: kittens
group: appearance
topic: attach-a-prop-to-a-kitten-bone
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/kitten-held-props, mods/ksagolf/features/prop-and-course-asset-xml]
techniques: [reflection, glb-model, xml-asset]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - reflection KSA.KittenRenderable._characterAvatar
  - reflection KSA.AssetManager.GetOrLoad
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
tags: [kitten, prop, cosmetic-attachment, static-mesh, ksagolf]
---
A kitten can hold a mesh of your own without a `<Character>` definition and without any Harmony patch. `CharacterAvatar.Attachments.CosmeticAttachments` is a public `List<CosmeticAttachment>`, and every frame `KittenRenderable.UpdateRenderData` puts each entry on its socket bone and draws it. This article adds an entry at runtime and builds the entry's `StaticMeshRenderable` itself, from the mod's own `<GltfFile>` and `<PbrMaterial>` assets (including a see-through variant on the helmet visor's glass technique). Prerequisites: StarMap, a mod asset bundle declaring the glTF and material ([define a kitten character in asset XML](../../characters/define-a-kitten-character-in-asset-xml/overview.md) shows the XML conventions; [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md) shows this mod's), and reflection into the avatar ([reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)). Demonstrated by KSAGolf, which puts a putter in a paw: [Mod meshes held by a kitten as cosmetic attachments](../../../../mods/ksagolf/features/kitten-held-props.md). For a prop that must sit exactly on the drawn hand in every view, continue with [approach 2](attach-a-prop-to-a-kitten-bone-2.md).

## Background: how the game does it

The attachment list and its element are public. The element's `HasAttachment` property includes `Enabled`, but the draw loop does not call it:

```csharp
// current/decomp/KSA/CharacterAvatar.cs L73-L98
	public struct CharacterAttachments
	{
		public Helmet Helmet = default(Helmet);

		public Mmu Mmu = default(Mmu);

		public List<CosmeticAttachment> CosmeticAttachments = new List<CosmeticAttachment>();

		public CharacterAttachments()
		{
		}
	}

	public struct CosmeticAttachment
	{
		public bool Enabled = true;

		public StaticMeshRenderable? Mesh = null;

		public int SocketIndex = 0;

		public float4x4 Transform = default(float4x4);

		public readonly bool HasAttachment => Mesh != null && Enabled;

		public CosmeticAttachment()
```

For each entry the renderer reads the socket bone's transform, composes `attachment.Transform * (RotZ(-90 deg) * RotX(-90 deg) * bone)` into the **mesh's own** `Transform` (overwriting it every frame), and draws. So the field a mod writes to aim a prop is the attachment's `Transform`, not the mesh's:

```csharp
// current/decomp/KSA/KittenRenderable.cs L399-L406
		foreach (CharacterAvatar.CosmeticAttachment cosmeticAttachment in _characterAvatar.Attachments.CosmeticAttachments)
		{
			float4x4 boneTransform3 = _characterAvatar.Core.CharacterModel.GetBoneTransform(cosmeticAttachment.SocketIndex);
			float4x4 float4x8 = float4x4.CreateRotationZ(-(float)Math.PI / 2f) * float4x4.CreateRotationX(-(float)Math.PI / 2f);
			cosmeticAttachment.Mesh.Transform = cosmeticAttachment.Transform * (float4x8 * boneTransform3);
			cosmeticAttachment.Mesh.Draw(view);
		}
	}
```

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L301-L307
	public float4x4 GetBoneTransform(int boneIdx)
	{
		if (Skeleton.BoneCount <= boneIdx)
		{
			return float4x4.Identity;
		}
		return Skeleton.WorldTransformList[boneIdx] * Transform;
```

The engine builds the helmet visor the way this article builds a see-through prop: material slots filled first, then a `StaticMeshRenderable` on the glass technique with `isOpaque: false` and no shadow. The `StaticMeshRenderable` constructor reads `GltfAssetRef.Materials[...]` handles **once** into its own `MaterialIndices`, so a material written to the slot afterwards never reaches that renderable:

```csharp
// current/decomp/KSA/CharacterAvatar.cs L460-L470
			GltfPbrAssetRef gltf2 = GetGltf(characterReference.CharacterVisor.Get().Source, skinned: false);
			for (int j = 0; j < characterReference.CharacterVisor.Get().Materials.Count; j++)
			{
				gltf2.Materials[j] = GetMaterial(characterReference.CharacterVisor.Get().Materials[j]);
			}
			Helmet helmet = new Helmet();
			helmet.HelmetMesh = new StaticMeshRenderable(meshRendererStaticPbr, gltf.Id, meshRendererStaticPrePass);
			helmet.VisorMesh = new StaticMeshRenderable(glassRenderer, gltf2.Id, meshRendererStaticPrePass, isOpaque: false)
			{
				CastShadows = false
			};
```

```csharp
// current/decomp/KSA/StaticMeshRenderable.cs L43-L53
		MaterialIndices = new int[GltfAssetRef.Meshes.Length];
		for (int i = 0; i < ColourMeshBucketHandles.Length; i++)
		{
			IMeshAsset meshAsset = GltfAssetRef.Meshes[i];
			if (meshAsset != null)
			{
				ColourMeshBucketHandles[i] = passBucketSystem.RegisterMesh(meshAsset, colourPassRenderer);
				ShadowDepthMeshBucketHandles[i] = passBucketSystem.RegisterMesh(meshAsset, shadowPassRendererStatic);
				MaterialIndices[i] = GltfAssetRef.Materials[GltfAssetRef.MaterialIndices[i]].Handle;
			}
		}
```

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| Build the mesh, add the attachment | KSAGolf's per-step update (`Golf.Hold`), from `[StarMapAfterGui]` when the HUD is shown, otherwise from the `OnFrameCelestials` prefix or the frame postfix; the developer probe runs from `[StarMapAfterOnFrame]` | `frame:draw-ui` (usually), `frame:after-on-frame` |
| The engine places and draws every attachment | `KittenEva.UpdateRenderData` inside `Program.RenderGame` / `RenderViewport`, once per view | `frame:render` |

Building a `StaticMeshRenderable` registers its meshes with the pass bucket system, and removing one must call `Dispose()` to unregister them; both happen on the main thread outside the render pass. Writing an attachment's `Transform` from a StarMap hook works for a static offset, but it is one frame stale against a moving bone: the render reads the transform of the bone as animated in that view. For zero-lag placement see [approach 2](attach-a-prop-to-a-kitten-bone-2.md). See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the assets.** One `<GltfFile>` and one `<PbrMaterial>` per prop. KSAGolf authors props in centimetres with one mesh, one primitive and one material slot, because the kitten is drawn through `CharacterCore.Scale = 0.01`. An exported `.glb` must name a material slot: that is what gives `GltfPbrAssetRef.Materials` a length.

```xml
<!-- src/KSAGolf/KSAGolfProps.xml L14-L23 -->
    <GltfFile Id="KSAGolf_Putter_Glb">
        <Source Path="Meshes/KSAGolf_Putter.glb"/>
    </GltfFile>
    <PbrMaterial Id="KSAGolf_Putter_Material">
        <Diffuse Path="Textures/KSAGolf_Putter_Diffuse.png" Category="Default"/>
        <Normal Path="Textures/KSAGolf_Putter_Normal.png" Category="Default"/>
        <AoRoughMetal Path="Textures/KSAGolf_Putter_PBR.png" Category="Default"/>
    </PbrMaterial>
</Assets>
```

2. **Get the asset-name type without referencing its assembly.** `Core.AssetName` lives in an assembly the mod does not reference, so read the type off `LoadedAssetRef.Id`'s property type. If you do reference the game's core assembly you can use `new AssetName(...)` directly.

```csharp
// src/KSAGolf/Ksa/AttachmentMesh.cs L16
    private static readonly Type? AssetNameType = typeof(LoadedAssetRef).GetProperty("Id")?.PropertyType;
```

3. **Load the glTF and the material from the cached managers.** `SuperMeshRenderSystem.GltfSystem` and `MaterialSystem` are public fields, each an `AssetManager<T>`; `GetOrLoad(AssetName)` is invoked by reflection here for the same assembly reason:

```csharp
// src/KSAGolf/Ksa/AttachmentMesh.cs L75-L80
    private static object? Load(SuperMeshRenderSystem system, string managerField, object name)
    {
        object? manager = system.GetType().GetField(managerField, BindingFlags.Instance | BindingFlags.Public)?.GetValue(system);
        MethodInfo? load = manager?.GetType().GetMethod("GetOrLoad", [AssetNameType!]);
        return load?.Invoke(manager, [name]);
    }
```

4. **Fill every material slot before constructing the renderable, then construct it.** The cached glTF is shared, so fill the slots immediately before each construction (that is also what lets one glTF back both a solid and a see-through variant). Use `MeshRendererStaticPbr` for solid and `CharacterRenderResources.GlassRenderer` with `isOpaque: false` and `CastShadows = false` for see-through. The constructor takes an `AssetName`, hence `Activator`:

```csharp
// src/KSAGolf/Ksa/AttachmentMesh.cs L26-L62
    public static bool TryBuild(string gltfId, string materialId, out StaticMeshRenderable? mesh, out string why, bool seeThrough = false)
    {
        mesh = null;
        why = string.Empty;
        try
        {
            if (AssetNameType is null)
            {
                why = "LoadedAssetRef.Id not found";
                return false;
            }

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

5. **Resolve the socket bone by name.** Bone names come from `Skeleton.BoneNames`; KSAGolf's right wrist is `Wrist_R`:

```csharp
// src/KSAGolf/Ksa/Golf.cs L471-L486
    private void Hold(KittenEva kitten)
    {
        if (KittenPoseProbe.AvatarOf(kitten) is not { } avatar) return;
        if (!AttachmentMesh.TryBuild("KSAGolf_Putter_Glb", "KSAGolf_Putter_Material", out StaticMeshRenderable? club, out string why))
        {
            Status = "The putter could not be loaded.";
            Log.Warn($"the putter cannot be held: {why}");
            return;
        }

        int wrist = avatar.Core.CharacterModel.Skeleton.BoneNames?.IndexOf("Wrist_R") ?? -1;
        if (wrist < 0)
        {
            club!.Dispose();
            return;
        }
```

6. **Add the attachment, or update it by read, change, write back.** `CosmeticAttachment` is a struct in a `List`, so an existing entry is read out, modified and written back; `Transform` is applied before the socket bone's own composition. To change the prop later, find it by `ReferenceEquals(a.Mesh, mesh)`:

```csharp
// src/KSAGolf/Ksa/Golf.cs L488-L493
        avatar.Attachments.CosmeticAttachments.Add(new CharacterAvatar.CosmeticAttachment
        {
            Mesh = club,
            SocketIndex = wrist,
            Transform = float4x4.CreateScale((float)_rig.Scale),
        });
```

```csharp
// src/KSAGolf/Ksa/KittenPropProbe.cs L83-L89
                // A struct in a list: read it, change it, write it back.
                int i = list.FindIndex(a => ReferenceEquals(a.Mesh, mesh));
                if (i < 0) continue;
                CharacterAvatar.CosmeticAttachment a = list[i];
                a.SocketIndex = socket;
                a.Transform = local;
                list[i] = a;
```

7. **Remove it cleanly.** Hiding a prop means removing it, because the loop ignores `Enabled`. Remove the entry, then dispose the mesh so its buckets unregister:

```csharp
// src/KSAGolf/Ksa/Golf.cs L524-L532
        if (_avatar is { } avatar)
        {
            avatar.Attachments.CosmeticAttachments.RemoveAll(a => ReferenceEquals(a.Mesh, _club));
            if (_pose is not null) avatar.Core.CharacterModel.AnimProcessors.Remove(_pose);
            avatar.Attachments.Mmu.Enabled = _mmuWas;
            avatar.Attachments.Helmet.Enabled = _helmetWas;
        }

        _club?.Dispose();
```

8. **Check the result.** With the mod's assets loaded, picking up the prop makes it appear in the kitten's paw in every view that draws the kitten (camera windows and crew portraits too); removing it makes it vanish. If the prop does not appear, the log line from `TryBuild` names the missing asset, material or build exception.

## Complete example

- [src/KSAGolf/Ksa/AttachmentMesh.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/AttachmentMesh.cs) - `TryBuild` (solid or see-through), `NameOf`, reflective `GetOrLoad`.
- [src/KSAGolf/Ksa/KittenPropProbe.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KittenPropProbe.cs) - the developer probe that hangs a prop on any bone with any transform.
- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs) - `Hold` and `LetGo`.
- [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml) - the two `<GltfFile>` / `<PbrMaterial>` pairs.
- Feature pages: [Mod meshes held by a kitten](../../../../mods/ksagolf/features/kitten-held-props.md), [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md).

## Variations and gotchas

- **The cached glTF is shared.** `GetOrLoad` returns one `GltfPbrAssetRef` per id, so filling its `Materials` changes it for every renderable built from it afterwards. Fill the slots right before each construction.
- **No material slot means no slots.** A `.glb` exported without a material has an empty `Materials` array; the fill loop then does nothing, and per the mod's notes the build throws.
- **Units, primitives and node transforms.** The mod's notes say an attachment must be in centimetres baked into the vertices, with one mesh and one primitive, because `GltfPbrSystem` aliases index buffers across primitives. Check this against your own export; it was not re-derived from the decompile here.
- **Only on camera.** The engine draws attachments inside the kitten's render, so a kitten no view draws has no prop drawn either.
- **After a second save load (unverified).** The mod's notes record that a prop attached after the second save load in one session draws nothing, with the draw matrix stuck at identity; a fresh launch works. The cause is unknown.
- **Why not a `<Character>`.** The mod's notes give three reasons: a mod character is dressed onto about a quarter of every roster; removing the mod later terminates the game on nearly every save; and a manifest that loads the mod before Core makes the character throw at load. A runtime attachment leaves nothing in the save.
- **Version notes.** KSAGolf was built against 2026.9.22.5482. In build 2026.10.10.5554 the glass technique's pipeline layout changed (one fewer descriptor set) and `StaticMeshRenderable.Draw` fills `ShadowRenderable.Center`; neither changes the calls used here.

## See also

- [Topic overview](overview.md), [approach 2: solve the attachment per view against the wrist](attach-a-prop-to-a-kitten-bone-2.md)
- Mod features: [Mod meshes held by a kitten](../../../../mods/ksagolf/features/kitten-held-props.md), [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md)
- [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [Define a kitten character in asset XML](../../characters/define-a-kitten-character-in-asset-xml/overview.md), [Tint and restyle kitten appearance](../tint-and-restyle-kitten-appearance/overview.md)
- [Draw mod meshes with the engine renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
