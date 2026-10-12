---
type: How-To
title: Swap Core's XML entries for patched copies with prefixes on the Mod loaders
description: Write a patched copy of a Core XML file at StarMapBeforeMain and Harmony-prefix the non-generic Mod loader methods to replace the file name in Core's own lists with the copy's absolute path
category: assets
group: xml-patching
topic: redirect-game-xml-loading
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/realatmospheres/features/core-xml-entry-redirect
  - mods/realatmospheres/features/atmosphere-optics-in-system-xml
techniques: [harmony-prefix, reflection, xml-patch]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.Mod.PrepareSystems
  - harmony-prefix KSA.Mod.LoadSystems
  - harmony-prefix KSA.Mod.LoadAssetBundles
  - harmony-prefix KSA.Mod.LoadEditorTagDefinitions
  - reflection KSA.Mod.Assets
  - reflection KSA.Mod.SystemTemplates
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:asset-load]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.XmlLoader
  - KSA.SystemInfo
  - KSA.AssetBundle
  - KSA.SystemTemplate
  - KSA.AtmosphericBodyTemplate
  - KSA.AstronomicalTemplate
  - KSA.PlanetaryBodyTemplate
tags: [xml, redirect, harmony, shadow-copy, core, system-templates, astronomicals]
---

The stock Sol system is defined in two places in Core: the body library `Astronomicals.xml` (Earth, Venus,
Mars, the giants) and the system templates `SolSystem.xml`, `SolSystemDense.xml` and
`SolSystemInterstellar.xml`, where Titan, Pluto, Triton and Neptune are defined inline. A mod that wants to
change those bodies (their atmospheres, surfaces, clouds) without editing the install can write a patched
copy of the file into its own folder and tell the game to read the copy instead. Real Atmospheres does
exactly that by replacing file names inside the Core `Mod` object's own arrays with the absolute paths of
its copies. No IL is rewritten and no generic method is patched. This also answers "how do I change Sol's
stock system and body XML without editing Core".

Demonstrated by [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md) and
[Per-body atmosphere optics in the system XML](../../../../mods/realatmospheres/features/atmosphere-optics-in-system-xml.md). Compare approach 2 ([Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](redirect-game-xml-loading-2.md)),
which uses KittenExtensions, and [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md), the declarative way when a patch suffices.

Prerequisites: StarMap, Harmony, reflection into private members
([Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md)).

## Background: how the game does it

`KSA.Mod` reads its file lists from `mod.toml`: `assets` into a private `string[] Assets` and `systems`
into a public `string[] SystemTemplates`. Core's manifest lists the files being swapped:

```toml
# current/Content/Core/mod.toml L21-L21
systems = [ "SolSystem.xml", "SolSystemDense.xml", "EarthSystem.xml" , "EarthOnly.xml", "SolSystemInterstellar.xml", "EarthOnlyInterstellar.xml" ]
```

Each loader joins the entry with the mod folder and passes the result to `XmlLoader.Load<T>`:

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

`Path.Combine(DirectoryPath, path)` returns its second argument unchanged when that argument is rooted, so
an entry that is an absolute path makes the loader read a file anywhere on disk. Nothing else needs to be
patched. The same holds for `LoadEditorTagDefinitions`, `LoadSystems` and `PrepareSystems`
([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L180), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L417-L440)). `Mod.GetPath` (used for asset `Path` attributes) has the same
rooted-path behaviour ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)), which also lets the patched XML reference textures
elsewhere (see [Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)).

`PrepareSystems` stores the entry string in `SystemInfo.XmlName`, and `LoadSystems` later loads only the
entry equal to the selected system's `XmlName`:

```csharp
// current/decomp/KSA/Mod.cs L128-L137
	public void LoadSystems()
	{
		string[] systemTemplates = SystemTemplates;
		foreach (string text in systemTemplates)
		{
			if (SystemLibrary.Default != null && text != SystemLibrary.Default.XmlName)
			{
				continue;
			}
			string filePath = Path.Combine(DirectoryPath, text);
	// ...
```

## Lifecycle and timing

The copy must exist and the swap must be installed before `ModLibrary.PrepareAll` runs, so both happen at
`startup:starmap-before-main`. The prefixes then fire in the `Program` constructor's sequence
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)):

| Order | Call | Prefix that fires | Lifecycle |
|---|---|---|---|
| 1 | `ModLibrary.PrepareAll` -> `Mod.PrepareSystems` (system picker headers) | systems | `startup:immediate-load` |
| 2 | `ModLibrary.PreloadAssetBundles` -> `LoadAssetBundles`, `LoadSystems` with `Preload = true` | assets, systems | `startup:asset-load` |
| 3 | `ModLibrary.LoadEditorTags` -> `LoadEditorTagDefinitions` | assets | `startup:asset-load` |
| 4 | `ModLibrary.LoadAll` -> `LoadAssetBundles`, `LoadSystems` | assets, systems | `startup:asset-load` |

All of it runs on the main thread ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L506)). The swap must be in place before row 1,
which is why the mod installs at `BeforeMain` and not `StarMapAllModsLoaded` (that fires after `LoadAll`).
StarMap applies its own patches only after every `BeforeMain` has run, so on the Core mod's `PrepareSystems`
the mod's prefix runs before StarMap's `StarMapImmediateLoad` prefix. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md).

## Steps

1. **Pick the files and the edit.** Decide which Core files hold what you change. Library bodies (Earth,
   Venus, Mars, the giants) are in `Astronomicals.xml` (listed in `assets`); `SolSystem*.xml` templates
   (`systems`) pull them in with `<LoadFromLibrary Id="Venus" Parent="Sol"/>` and define Titan, Pluto,
   Triton and Neptune inline, so editing a body that is a `LoadFromLibrary` reference means editing the
   library file, not the system file.

2. **Write the patched copies at `BeforeMain`.** Read from the install's `Content/Core` and write into your
   own folder. Failure must leave the game stock, so wrap the whole install (the edit techniques are in
   [Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md)):

   ```csharp
   // ModMain.cs L24-L35
       [StarMapBeforeMain]
       public void BeforeMain()
       {
           try
           {
               Install();
           }
           catch (Exception ex)
           {
               ShadowBuilder.Log("FAILED to install (game will run stock): " + ex);
           }
       }
   ```

3. **Find the `Mod` type and make the shadow.** The mod works through reflection only, so it references
   nothing but StarMap.API and Harmony:

   ```csharp
   // ModMain.cs L58-L64
           string coreDir = Path.Combine(installRoot, "Content", "Core");
           if (!Directory.Exists(coreDir))
               throw new DirectoryNotFoundException("Core content not found at " + coreDir);

           ShadowBuilder.Build(coreDir, modDir);

           var harmony = new Harmony("gunshy.realatmospheres");
   ```

4. **Prefix the non-generic loaders.** Four methods consume the two arrays. Assets are read by
   `LoadAssetBundles` and `LoadEditorTagDefinitions`; system templates by `LoadSystems` and
   `PrepareSystems`:

   ```csharp
   // ModMain.cs L66-L90
           // XML seam: prefix the two non-generic consumers of Mod.Assets and swap
           // Core's Astronomicals entry for the shadow's absolute path. Never patch
           // the generic XmlLoader.Load<T> itself: shared generic code means a
           // patched Load<AssetBundle> hijacks EVERY reference-type instantiation
           // with T=AssetBundle and breaks all other deserialization (learned the
           // hard way - it crashed the game).
           var assetsPrefix = new HarmonyMethod(typeof(Patches), nameof(Patches.ModAssetsPrefix));
           foreach (string name in new[] { "LoadAssetBundles", "LoadEditorTagDefinitions" })
           {
               MethodInfo m = AccessTools.Method(modType, name)
                   ?? throw new InvalidOperationException($"Mod.{name} not found");
               harmony.Patch(m, prefix: assetsPrefix);
           }

           // System templates (Titan lives inline in SolSystem/SolSystemDense.xml):
           // same permanent-swap trick on Mod.SystemTemplates. PrepareSystems sets
           // SystemInfo.XmlName = the entry string, so swapping BEFORE both
           // consumers keeps XmlName comparisons self-consistent everywhere.
           var systemsPrefix = new HarmonyMethod(typeof(Patches), nameof(Patches.ModSystemsPrefix));
           foreach (string name in new[] { "LoadSystems", "PrepareSystems" })
           {
               MethodInfo m = AccessTools.Method(modType, name)
                   ?? throw new InvalidOperationException($"Mod.{name} not found");
               harmony.Patch(m, prefix: systemsPrefix);
           }
   ```

5. **Swap the entry for the shadow's absolute path.** The prefix runs for every `Mod`, so filter on the
   mod `Id`, read the private array by reflection, and replace the matching file name in place. The swap
   persists in the array, and comparing against the shadow path first keeps the prefix idempotent:

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

   The system-template prefix does the same for `SystemTemplates` and a map of file name to shadow path:

   ```csharp
   // ModMain.cs L199-L224
       public static void ModSystemsPrefix(object __instance)
       {
           if (ShadowBuilder.ShadowSystemFiles.Count == 0) return;

           _idProp ??= __instance.GetType().GetProperty("Id");
           if (_idProp?.GetValue(__instance) is not string id
               || !id.Equals("Core", StringComparison.OrdinalIgnoreCase))
               return;

           _systemTemplatesField ??= AccessTools.Field(__instance.GetType(), "SystemTemplates");
           if (_systemTemplatesField?.GetValue(__instance) is not string[] systems) return;

           for (int i = 0; i < systems.Length; i++)
           {
               if (ShadowBuilder.ShadowSystemFiles.TryGetValue(Path.GetFileName(systems[i]), out string? shadow)
                   && systems[i] != shadow)
               {
                   systems[i] = shadow;
                   if (!_loggedSystems)
                   {
                       ShadowBuilder.Log("redirected Core system templates (Titan)");
                       _loggedSystems = true;
                   }
               }
           }
       }
   ```

6. **Patch both system methods.** `PrepareSystems` stores the entry as `SystemInfo.XmlName` and
   `LoadSystems` compares it with the selected system. Swapping before both keeps the comparison
   consistent; the saved last-used system is the template `Id`, not the file name, so player settings
   stay valid when the mod is removed.

7. **Check the log.** The mod logs `redirected Core Astronomicals.xml` and `redirected Core system templates`
   on first use. If they are missing, the swap did not happen and the game runs stock.

## Complete example

- [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) (install, both prefixes)
- [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) (builds the copies; edit techniques in [Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md))
- [Shared/SharedShadow.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Shared/SharedShadow.cs) (one shared copy for two mods, [Let two mods edit the same Core file with a shared shadow folder and a named mutex](redirect-game-xml-loading-4.md))
- The stock files: [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml), [current/Content/Core/SolSystem.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/SolSystem.xml)

## Variations and gotchas

- **Do not patch `XmlLoader.Load<T>` itself.** The comment at the install site records that patching the
  generic loader hijacked every deserialization with the same `T` (a patched `Load<AssetBundle>` is shared
  code for every reference-type instantiation) and crashed the game. Patch the non-generic callers.
- **It only helps if the mod owns the whole file.** The copy replaces the file for everyone: another mod
  that redirects the same file overwrites yours. Two cooperating mods can share one copy
  ([Let two mods edit the same Core file with a shared shadow folder and a named mutex](redirect-game-xml-loading-4.md)).
- **It does not combine with KittenExtensions.** KittenExtensions builds its document from the original
  `mod.toml` lists read from disk, keyed by the full path of each original entry, and transpiles the same
  loaders to read from it ([Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](redirect-game-xml-loading-2.md)). The prefixes here run first and swap the
  entries for paths that were never imported, so the lookup misses and the stock loaders skip the
  resulting `null`. By reading the code, the Sol system headers, the Core `Astronomicals.xml` bundle (and
  with it every `LoadFromLibrary` body) would be missing, while `LoadEditorTagDefinitions` (which
  KittenExtensions leaves stock) would still read the shadow file. This was not tried in game and neither
  mod declares the conflict; if you must run both, use patches ([Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md)) instead.
- **Absolute paths inside the copies.** The patched XML points new textures at absolute paths. `Mod.GetPath`
  passes rooted paths through and `Filepath.CorrectSeparators` normalises backslashes, so the paths work on
  either separator.
- **Culture.** Numbers formatted into XML must use `CultureInfo.InvariantCulture`; a decimal comma once
  stopped the game starting ([Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md)).
- **Game updates.** The copy is rebuilt from the install at every launch, so a game update changes what you
  patch; anchors that no longer match must skip and log rather than corrupt the file.

## See also

- [Transpile the Mod loaders so XmlLoader.Load calls read from an in-memory document](redirect-game-xml-loading-2.md), [Edit shadow copies with anchor-verified text patches and generated XML between markers](redirect-game-xml-loading-3.md), [Let two mods edit the same Core file with a shared shadow folder and a named mutex](redirect-game-xml-loading-4.md),
  [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md), [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md)
- [Define atmospheres, scattering and cloud layers for a body in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md), [Replace or text-patch the game's GLSL without editing game files](../../../shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md)
- [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md), [Per-body atmosphere optics in the system XML](../../../../mods/realatmospheres/features/atmosphere-optics-in-system-xml.md)
