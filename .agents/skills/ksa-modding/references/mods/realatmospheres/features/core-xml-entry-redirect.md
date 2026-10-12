---
type: Mod Feature
title: Core XML entry redirect
description: Harmony prefixes on four KSA.Mod loader methods swap the Core mod's Astronomicals.xml and Sol template entries for absolute paths to patched copies, so the game's own loaders read the copies
mod: realatmospheres
category: assets
techniques: [harmony-prefix, reflection, xml-patch]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.Mod.LoadAssetBundles
  - harmony-prefix KSA.Mod.LoadEditorTagDefinitions
  - harmony-prefix KSA.Mod.LoadSystems
  - harmony-prefix KSA.Mod.PrepareSystems
  - reflection KSA.Mod.Assets
  - reflection KSA.Mod.SystemTemplates
lifecycle: [startup:immediate-load, startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.XmlLoader
  - KSA.SystemInfo
  - KSA.SystemLibrary
  - KSA.ConfigOnStartPopup
  - Core.Files.Paths.Filepath
tags: [xml, assets, system-templates, harmony, prefix, reflection, file-redirect]
---

The Sol system's atmospheres live in two places: the body library `Core/Astronomicals.xml` (Earth,
Venus, Mars, the giants, loaded as an asset bundle) and the system templates `SolSystem.xml`,
`SolSystemDense.xml` and `SolSystemInterstellar.xml` (Titan, Pluto, Triton and Neptune are defined
inline there). Real Atmospheres writes patched copies of those files (see
[Shared shadow for two cooperating mods](cross-mod-shared-shadow.md)) and then swaps the file names
inside the Core mod's own lists for the copies' absolute paths. The game's loaders do the rest.[^ra-modmain]

## How it works

- **Where the names come from.** `KSA.Mod` deserializes `mod.toml`: `assets` into the private
  `string[] Assets` and `systems` into the public `string[] SystemTemplates`. Each loader builds the
  path with `Path.Combine(DirectoryPath, entry)` and hands it to `XmlLoader.Load<T>`
  ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L61-L66),
  [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L169)).[^ksa-mod]
  `Path.Combine` returns its second argument unchanged when that argument is rooted, so an absolute
  entry escapes the mod folder without any further patch.
- **Assets prefix.** `ModAssetsPrefix` runs before `Mod.LoadAssetBundles` and
  `Mod.LoadEditorTagDefinitions`. It reads `Id` by reflection, ignores every mod but `Core`, gets the
  private `Assets` field with `AccessTools.Field`, and replaces the entry whose file name is
  `Astronomicals.xml` with the shadow path ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L159-L190)).
  `InterstellarAstronomicals.xml` has a different file name and is left alone.
- **Systems prefix.** `ModSystemsPrefix` runs before `Mod.LoadSystems` and `Mod.PrepareSystems` and
  swaps each `SystemTemplates` entry whose file name has a shadow copy
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L195-L224)).
- **Permanent, idempotent swap.** The arrays are modified in place, so later callers see the swapped
  entry too; both prefixes compare against the shadow path first, so running them again is a no-op.
- **Why both system methods.** `PrepareSystems` stores the entry string as `SystemInfo.XmlName`, and
  `LoadSystems` later loads only the entry equal to the selected system's `XmlName`
  ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L441)). Swapping before both keeps
  the comparison consistent ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L80-L90)).
- **Deliberately not `XmlLoader.Load<T>`.** The comment at the install site records that patching the
  generic loader broke every other deserialization, because a patch on one reference-type
  instantiation of shared generic code hijacks all of them
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L66-L78)). Patching the non-generic callers avoids that.

## Lifecycle and timing

The prefixes are installed at `[StarMapBeforeMain]` and fire during the game's startup sequence in
the `Program` constructor
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)):[^ksa-program]

| Order | Call | Prefix that fires | Lifecycle |
|---|---|---|---|
| 1 | `ModLibrary.PrepareAll` -> `Mod.PrepareSystems` (system picker headers) | systems | `startup:immediate-load` |
| 2 | `ModLibrary.PreloadAssetBundles` -> `LoadAssetBundles`, `LoadSystems` with `Preload = true` | assets, systems | `startup:asset-load` |
| 3 | `ModLibrary.LoadEditorTags` -> `LoadEditorTagDefinitions` | assets | `startup:asset-load` |
| 4 | `ModLibrary.LoadAll` -> `LoadAssetBundles`, `LoadSystems` | assets, systems | `startup:asset-load` |

([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L449-L506)). All of it runs on
the main thread. The swap must be in place before step 1, which is why the mod installs at
`BeforeMain` rather than `[StarMapAllModsLoaded]` (that fires after `LoadAll`).

Patch order at step 1: StarMap applies its own patches in `StarMapCore.Init` only after every mod's
`BeforeMain` has run ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L29)),[^starmap-starmapcore]
and Harmony orders equal-priority prefixes by registration. So on the Core mod's `PrepareSystems` the
Real Atmospheres prefix runs before StarMap's `StarMapImmediateLoad` prefix
([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)).
A KittenExtensions prefix on `ModLibrary.PrepareAll` (its XML patch pass) has already finished before
the `PrepareAll` loop reaches any `Mod` ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L21-L28)).[^kx-patches]

## Key code

| Path | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L66-L90) | Install the two prefixes on four `Mod` methods |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L159-L190) | `ModAssetsPrefix`: swap `Astronomicals.xml` in Core's `Assets` |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L195-L224) | `ModSystemsPrefix`: swap the Sol templates in Core's `SystemTemplates` |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L125-L131) | Split the shared shadow's result into the two redirect tables |

## Snippets

```csharp
// ModMain.cs L164-L190
    public static void ModAssetsPrefix(object __instance)
    {
        string? shadow = ShadowBuilder.ShadowAstronomicals;
        if (shadow == null) return;

        _idProp ??= __instance.GetType().GetProperty("Id");
        if (_idProp?.GetValue(__instance) is not string id
            || !id.Equals("Core", StringComparison.OrdinalIgnoreCase))
            return;

        _assetsField ??= AccessTools.Field(__instance.GetType(), "Assets");
        if (_assetsField?.GetValue(__instance) is not string[] assets) return;

        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] != shadow
                && Path.GetFileName(assets[i]).Equals("Astronomicals.xml", StringComparison.OrdinalIgnoreCase))
            {
                assets[i] = shadow;
                if (!_loggedXml)
                {
                    ShadowBuilder.Log("redirected Core Astronomicals.xml");
                    _loggedXml = true;
                }
            }
        }
    }
```

```csharp
// current/decomp/KSA/Mod.cs L157-L169
	public void LoadAssetBundles()
	{
		string[] assets = Assets;
		foreach (string path in assets)
		{
			string filePath = Path.Combine(DirectoryPath, path);
			AssetBundle assetBundle = XmlLoader.Load<AssetBundle>(filePath, this);
			if (assetBundle != null)
			{
				Register(assetBundle);
			}
		}
	}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Mod.Assets` (private), `Mod.SystemTemplates`, `Mod.Id` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L61-L82) | The lists that are rewritten; the mod filter |
| `Mod.LoadAssetBundles`, `LoadEditorTagDefinitions` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L190) | Patched; read `Assets` |
| `Mod.LoadSystems`, `PrepareSystems` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L155) | Patched; read `SystemTemplates`, compare `XmlName` |
| `ModLibrary.PrepareAll`, `PreloadAssetBundles`, `LoadEditorTags`, `LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L449-L522) | When the patched methods run |
| `Core/mod.toml` `systems`, `assets` | [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L20-L66) | The stock entries being swapped |

## Pitfalls and version notes

- **Does not combine with KittenExtensions (Continued).** Its `ModLibrary.PrepareAll` prefix builds
  one in-memory document from every mod's `mod.toml` lists read from disk, with each file keyed by the
  full path of the original entry (the full paths of `Content/Core/Astronomicals.xml`,
  `Content/Core/SolSystem.xml` and so on)
  ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L133-L177)).
  It then transpiles `Mod.PrepareSystems`, `LoadSystems`, `LoadAssetBundles` and `LoadPlanetMeshes` so
  that `XmlLoader.Load<T>` becomes `XmlPatcher.Load<T>`, which looks the file up in that document by
  `Path.GetFullPath(filePath)` instead of reading it
  ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L35-L42),
  [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L28-L54)).[^kx-patcher]
  Real Atmospheres' prefixes run before those transpiled bodies and have already replaced Core's
  entries with the shadow paths, which were never imported. The lookup misses and `XmlPatcher.Load<T>`
  returns `null`: either the node is invalid, or the lookup throws and the exception is logged. Which
  of the two happens depends on the `XPathPatch` submodule, which is not checked out. The stock loaders
  skip a `null` result. So `PrepareSystems` registers no Sol system headers and `LoadSystems` gets no
  Sol template. `LoadAssetBundles` registers no Core `Astronomicals.xml` bundle, so the library bodies
  that every Sol template pulls in with `<LoadFromLibrary>` are missing. KittenExtensions patches that
  target those files are applied to the document copy, which nothing reads. Only
  `LoadEditorTagDefinitions` is not transpiled, so it reads the shadow file from disk. These
  consequences come from reading the code, not from running both mods in game. Neither mod's
  `mod.toml` declares the conflict.
- **The swap persists in memory for the session.** Anything that reads `Mod.SystemTemplates` later
  sees absolute shadow paths. `SystemInfo.XmlName` is read only by `Mod.LoadSystems`, and the saved
  last-used system is the template `Id`, not the file name
  ([current/decomp/KSA/ConfigOnStartPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfigOnStartPopup.cs#L107)).[^ksa-configonstartpopup]
  So settings written with the mod still name the same system after it is removed.
- **One redirect per file.** Two mods each redirecting `Astronomicals.xml` to their own copy would
  overwrite each other; Real Atmospheres solves this with its sibling mod by sharing one copy.
- **Absolute texture paths inside the copies.** The patched XML points new textures at
  `{ASSETS}\...` absolute paths with backslashes. These rely on `Mod.GetPath` passing rooted paths
  through ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L1854-L1858),
  [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)).
  `GetPath` also runs `Filepath.CorrectSeparators`, which turns every backslash into `/`, so these
  paths do not depend on the platform's separator
  ([current/decomp/Core.Files.Paths/Filepath.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core.Files.Paths/Filepath.cs#L11-L14)).[^core-filepath]

## See also

- [Shared shadow for two cooperating mods](cross-mod-shared-shadow.md)
- [Anchor-verified text patching of the shadow tree](shadow-tree-anchor-patching.md)
- [Per-body atmosphere optics in the system XML](atmosphere-optics-in-system-xml.md)
- [XmlLoader call-site redirect (KittenExtensions)](../../kittenextensions/features/xmlloader-call-site-redirect.md)
- [Find out when KSA reads each mod.toml key and where hooks can see the data](../../../howto/assets/asset-xml/mod-xml-load-order/mod-xml-load-order.md) - how-to article demonstrating this feature
- [Reach another mod's or Core's files with absolute paths and Id-only references](../../../howto/assets/asset-xml/resolve-asset-ids-and-mod-relative-paths/resolve-asset-ids-and-mod-relative-paths-2.md) - how-to article demonstrating this feature
- [Swap Core's XML entries for patched copies with prefixes on the Mod loaders](../../../howto/assets/xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-1.md) - how-to article demonstrating this feature

[^ra-modmain]: ModMain.cs
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-starmapcore]: StarMap: StarMap.Core/StarMapCore.cs
[^kx-patches]: KittenExtensions: KittenExtensions/Patches.cs
[^kx-patcher]: KittenExtensions: KittenExtensions/Patch/Patcher.cs
[^ksa-configonstartpopup]: KSA.ConfigOnStartPopup (decompiled, build 2026.10.10.5554)
[^core-filepath]: Core.Files.Paths.Filepath (decompiled, build 2026.10.10.5554)
