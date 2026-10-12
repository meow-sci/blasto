---
type: How-To
title: Keep settings beside the mod DLL, resolved from the loaded assembly
description: Resolve the mod folder from Assembly.Location with fallbacks, put settings.ini, hud.ini or colors.cfg there, and understand what a redeploy or an update does to a file that is both shipped and live
category: persistence
group: user-data
topic: choose-where-a-mod-stores-its-files
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ohmystars/features/mod-root-and-packaging
  - mods/compendium/features/settings-ini-persistence
  - mods/ksa-measuretools/features/color-palettes-persistence
  - mods/ohmystars/features/ini-settings-persistence
  - mods/ksa-aircrafthud/features/ini-settings-and-settings-window
techniques: [toml-config]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary]
tags: [paths, mod-folder, assembly-location, defaults, deploy, ini]
---

Outcome: a settings file that lives in the same folder as the mod's DLL and is found whichever loader placed the mod. Four mods do this: OhMyStars (`settings.ini`), Compendium (`settings.ini`), KSA-MeasureTools (`colors.cfg`) and KSA_AircraftHUD (`hud.ini`). It is the simplest option and the one most affected by updates. Demonstrated by [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [settings.ini persistence](../../../../mods/compendium/features/settings-ini-persistence.md) and [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md) (format and write policy are in [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md)).

## Background: how the game does it

The game knows each mod's folder as `Mod.DirectoryPath`, the directory of its manifest ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L108-L109)); mods are normally found under `<docs>/mods` (`ModLibrary.LocalModsFolderPath`, [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)). Under StarMap each mod is loaded into its own `AssemblyLoadContext` from the folder named by the manifest, so `Assembly.Location` of the mod's DLL is that same folder. `StarMapImmediateLoad(Mod)` hands the `Mod` to the hook, but mods often read the assembly path instead, because it also works in the other hooks and when a stale second copy of the mod exists elsewhere.

## Lifecycle and timing

The path is a pure function of the loaded assembly, so it can be resolved at the first hook that needs it: Compendium and KSA_AircraftHUD load their file in `StarMapImmediateLoad` (`startup:immediate-load`); OhMyStars and KSA-MeasureTools load in `StarMapAllModsLoaded` (`startup:all-mods-loaded`). Nothing needs game state. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve from the assembly, with fallbacks.** OhMyStars prefers the running assembly's directory ("correct even when an external modloader loads the mod from a folder the game's ModLibrary doesn't know about"), then the game's own mod registry entry, then the default mods folder:

   ```csharp
   // src/ModPaths.cs L29-L52
   private static string ResolveModRoot() {
       // Prefer the running assembly's own directory: the mod's assets are always
       // deployed next to OhMyStars.dll, and this stays correct even when an external
       // modloader loads the mod from a folder the game's ModLibrary doesn't know about
       // (or when a second, stale copy exists in the default mods folder).
       string? assemblyDirectory = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location);
       if(!string.IsNullOrWhiteSpace(assemblyDirectory)) {
           return assemblyDirectory;
       }

       // Fall back to the game's own mod registry (mod id == mod folder name).
       Mod? mod = ModLibrary.Find("OhMyStars");
       if(mod is not null && mod != Mod.Empty && !string.IsNullOrWhiteSpace(mod.DirectoryPath)) {
           return mod.DirectoryPath;
       }

       // Last resort: the default game mods folder under Documents.
       return Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
           "My Games",
           "Kitten Space Agency",
           "mods",
           "OhMyStars");
   }
   ```

2. **Or take the one-liner.** Compendium reads `dllDir` once at the top of its immediate-load hook; MeasureTools and KSA_AircraftHUD do the same with `typeof(...).Assembly.Location` / `Assembly.GetExecutingAssembly().Location`:

   ```csharp
   // src/CompendiumMain.cs L1235-L1242
   [StarMapImmediateLoad]
   public void OnImmediateLoad(Mod mod)
   {
       try
       {
           // Gets the current working directory path (of the DLL)
           dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
           LoadSettings();
   ```

   ```csharp
   // MeasureTools/Features/Measure/MeasureColors.cs L225-L229
   private static string ConfigPath()
   {
       string directory = Path.GetDirectoryName(typeof(MeasureColors).Assembly.Location) ?? ".";
       return Path.Combine(directory, FileName);
   }
   ```

   ```csharp
   // AircraftHUDMain.cs L675-L679
   ImGuiViewport * viewport = ImGui.GetMainViewport();

   string dllDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

   LoadHudIni(Path.Combine(dllDir, "hud.ini"));
   ```

3. **Combine with the file name** (`Path.Combine(dllDir, "settings.ini")`) and read and write with `System.IO` as in [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md).

### Shipping the defaults and keeping the player's edits

When the shipped settings file is also the live one, the build decides what an update does to a player's changes:

4. **OhMyStars and Compendium copy the source ini over the installed one on every build.** The csproj copies `settings.ini` to the output (`CopyToOutputDirectory=Always`) and to the mods folder with `SkipUnchangedFiles="false"`, so published files carry the defaults; the README tells developers to close the game before building so it cannot write test settings over them:

   ```xml
   <!-- src/OhMyStars.csproj L68-L70 -->
   <None Update="settings.ini">
     <CopyToOutputDirectory>Always</CopyToOutputDirectory>
   </None>
   ```

   ```xml
   <!-- src/Compendium.csproj L81-L84 -->
   <!-- Default settings; every build resets the test copy so published files ship with defaults -->
   <None Update="settings.ini">
     <CopyToOutputDirectory>Always</CopyToOutputDirectory>
   </None>
   ```

   An update that replaces the mod folder therefore replaces the player's file. That is acceptable for options that are cheap to set again; it is not for data the player created.

5. **To keep edits across updates, ship a template and seed a separate live file** (the gatOS pattern in [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](choose-where-a-mod-stores-its-files-3.md)), or write live data to a folder an update does not touch ([Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md)).

## Complete example

| File | Role |
|---|---|
| [src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs) | Mod-root resolver with fallbacks |
| [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L8-L16) and [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1245) | Compendium's path and load |
| [MeasureTools/Features/Measure/MeasureColors.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureColors.cs#L225-L229) | MeasureTools' path |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L675-L679) | KSA_AircraftHUD's path |
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L37-L48) | Deploy target |

## Variations and gotchas

- **Timing differs by mod.** The path is available as early as `startup:immediate-load`; resolving it lazily (OhMyStars' `ModRoot`) removes the ordering question.
- **A read-only install.** If the mod folder is read-only (a packaged or system-wide install) writes fail; the mods here catch and log or ignore the failure.
- **Instance paths.** The file moves with the mod folder, not with `-InstancePath`; two instances sharing one install folder share one file. Use `Constants.DocumentsFolderPath` ([Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md)) for per-instance data.
- **Updates replace the file** unless the packaging avoids shipping it; check how your release package is built ([Package and publish a mod release (zip, CI publishing, installer)](../../../getting-started/build/package-and-publish-a-mod-release/overview.md)).
- **Uninstalling** removes the folder and the file with it, which is a feature for settings.

## See also

- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md), [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](choose-where-a-mod-stores-its-files-3.md), [overview](./overview.md)
- [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [settings.ini persistence (OhMyStars)](../../../../mods/ohmystars/features/ini-settings-persistence.md), [settings.ini persistence](../../../../mods/compendium/features/settings-ini-persistence.md), [Named color palettes persisted to a cfg file next to the mod assembly](../../../../mods/ksa-measuretools/features/color-palettes-persistence.md), [INI settings file and settings window](../../../../mods/ksa-aircrafthud/features/ini-settings-and-settings-window.md)
- [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md)
