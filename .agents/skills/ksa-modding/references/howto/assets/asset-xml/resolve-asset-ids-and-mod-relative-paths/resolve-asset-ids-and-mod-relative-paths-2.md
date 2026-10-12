---
type: How-To
title: Reach another mod's or Core's files with absolute paths and Id-only references
description: Use rooted paths that Path.Combine passes through to point XML at files outside the mod folder, gate insertions on File.Exists, and reference Core's textures and meshes by Id alone
category: assets
group: asset-xml
topic: resolve-asset-ids-and-mod-relative-paths
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/realatmospheres/features/cloud-layers-in-system-xml
  - mods/realatmospheres/features/core-xml-entry-redirect
techniques: [xml-asset, xml-patch]
hooks: [xml absolute Path attribute]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.FileReference
  - KSA.SerializedId
  - KSA.ModLibrary
  - KSA.MeshAtlasFileReference
  - KSA.MeshReference
tags: [xml, paths, absolute, cross-mod, core, references, file-exists]
---

`Path.Combine(a, b)` returns `b` unchanged when `b` is rooted, and every asset path in KSA goes through
`Mod.GetPath`, which is a `Path.Combine`. An XML `Path` that is an absolute path therefore reaches files outside the
mod folder: another mod's textures, a shadow tree your mod generated, a player-chosen file. Real Atmospheres relies on
this to point Core's cloud layers at textures in its own `assets` folder, and the same rule is what makes the Core
entry redirect work. This article shows the pattern, the safety checks that go with it, and the Id-only reference that
reaches Core's own assets without any path.

Demonstrated by [Cloud layers in the system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md) and [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md). The basic
rules are in [Lay out mod-folder-relative paths and prefixed global asset ids](resolve-asset-ids-and-mod-relative-paths-1.md).

## Background: how the game does it

`Mod.GetPath` is `Filepath.CorrectSeparators(Path.Combine(DirectoryPath, localPath))`:

```csharp
// current/decomp/KSA/Mod.cs L389-L392
	public string GetPath(string localPath)
	{
		return Filepath.CorrectSeparators(Path.Combine(DirectoryPath, localPath));
	}
```

`Path.Combine` discards `DirectoryPath` when `localPath` is rooted, and `CorrectSeparators` turns every backslash into
a forward slash, so an absolute Windows path written with backslashes in XML works on either separator style:

```csharp
// current/decomp/Core.Files.Paths/Filepath.cs L11-L14
	public static string CorrectSeparators(string inPath)
	{
		return inPath.Replace("\\", "/");
	}
```

The same rule applies to the `mod.toml` file lists, which is what lets a mod redirect a Core file to a patched copy
([Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md)). For references, a file element with an `Id` and no `Path` is not loaded at all: it
resolves through the registry with `ModLibrary.Get<T>(Id)` ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L72),
[current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L148-L152)). A `MeshReference` in `PartModel` XML is always a reference
([current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L72-L82)), and a `PbrMaterial` with no `Diffuse`, `Normal` or `AoRoughMetal` child is one too
([current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L75-L94)).

## Lifecycle and timing

`startup:starmap-before-main` for generating the XML (Real Atmospheres builds its text at `BeforeMain`) and
`startup:asset-load` for the game reading it. Absolute paths are read when `FileReference.Load` runs in the parallel
loader pass, so the target file must exist before then; a missing one logs an error and leaves the reference unloaded.
See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md).

## Steps

1. **Write the XML with a placeholder and substitute the absolute folder.** Real Atmospheres keeps its textures in
   `assets/` beside the DLL and writes `Path="{ASSETS}\Name.dds"` in its payload templates, then replaces the
   placeholder with the absolute assets directory when it builds the XML:

   ```csharp
   // Payloads.cs L227-L228
       public const string VenusDetailTexture =
           """<Texture Id="VenusCloudDetail" Path="{ASSETS}\VenusCloudDetail.png" Category="Terrain"/>""";
   ```

2. **Gate every insertion on the files existing.** A layer pointing at a texture that is not there would fail the load
   of the whole file. Check first, log and skip otherwise (this is also how deleting an asset reverts one feature):

   ```csharp
   // ShadowBuilder.cs L498-L507
           string layer = Payloads.CirrusLayerTemplate.Replace("{ASSETS}", assetsDir);
           foreach (string tex in new[] { "MarsCirrusMaskVolumetric.dds", "MarsCirrusMask2D.png" })
           {
               if (!File.Exists(Path.Combine(assetsDir, tex)))
               {
                   Log($"WARN: missing asset {tex}, skipping cirrus layer");
                   return s;
               }
           }
           return s[..j] + layer + s[j..];
   ```

3. **Reference Core's assets by `Id` only.** Omit `Path` and the element is a reference. Real Atmospheres swaps a cloud
   layer's detail tile between Core's own texture (by Id) and its own file (by absolute path):

   ```csharp
   // Payloads.cs L226-L228
       public const string VenusStockDetailTexture = "<Texture Id=\"CloudDetail2048\"/>";
       public const string VenusDetailTexture =
           """<Texture Id="VenusCloudDetail" Path="{ASSETS}\VenusCloudDetail.png" Category="Terrain"/>""";
   ```

   A part can reuse a Core mesh and material the same way. gatOS's example part (in its docs) builds an emitter
   from Core's spotlight head mesh and material without shipping any art:

   ```xml
   <!-- docs/examples/CoreLaserAAssets.xml L42-L51 -->
       <SubPart Id="CoreLaserA_Subpart_EmitterA">
           <PartModel Id="CoreLaserA_Subpart_EmitterA_Model">
               <Mesh Id="CoreElectricalA_Subpart_SpotlightA" />
               <Material Id="CoreElectricalA_Material" />
           </PartModel>
           <MeshView>
               <Mesh Id="CoreElectricalA_Subpart_SpotlightA_VM" />
           </MeshView>
       </SubPart>

   ```

4. **Use a path outside the mod folder only where you must.** Prefer shipping your own copy under your own prefix
   (approach 1). The rooted-path trick is for derived or generated files (a shadow tree, a decrypted resource) and
   for XML you are rewriting at startup.

5. **Mind separators.** Backslashes in the XML are converted by `CorrectSeparators`; Windows drive letters are not
   portable, so on Linux build the path from `Path.Combine` in code and format it into the XML.

## Complete example

- [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L226-L228) (Id reference and absolute path texture)
- [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L479-L507) (existence-gated insertion with `{ASSETS}` substitution)
- [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L164-L190) (absolute shadow path swapped into Core's `assets`)

## Variations and gotchas

- **Absolute paths are machine specific.** They are written into a derived copy at every launch, never into shipped
  XML. Shipping an absolute path would break on every other machine.
- **Same id, two textures.** If your absolute-path texture has an `Id` that already exists, the first registration wins
  and yours becomes a reference to it. Give new textures a fresh prefixed Id.
- **Another mod's files may not exist.** Another mod may be uninstalled or on another path; check existence first.
- **The Core redirect uses the same rule.** Replacing an entry in Core's `assets` array with an absolute shadow path works
  only because `Path.Combine` passes it through ([Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md)).
- **KittenExtensions' document keys each imported file by `Path.GetFullPath`**, so a list entry swapped for an
  absolute path that it never imported is not found ([Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md)).

## See also

- [Lay out mod-folder-relative paths and prefixed global asset ids](resolve-asset-ids-and-mod-relative-paths-1.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md),
  [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md)
- [Cloud layers in the system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md), [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md)
