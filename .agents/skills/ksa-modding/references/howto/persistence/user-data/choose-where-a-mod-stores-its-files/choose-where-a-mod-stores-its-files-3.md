---
type: How-To
title: Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template
description: Split a mod into a read-only install folder and a user-writable data folder with its own paths class, ship a commented template under a different name, and copy it to the live file only when that file is missing
category: persistence
group: user-data
topic: choose-where-a-mod-stores-its-files
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/toml-config-seeded-template, mods/gatos/features/qcow2-overlay-guest-disks]
techniques: [toml-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary]
tags: [paths, data-folder, install-folder, template, seed, updates, msbuild]
---

Outcome: updates and redeploys can replace the whole install without touching anything the player made. gatOS ships read-only artifacts (the guest image, QEMU, a commented config template) in the install folder and keeps the live config, disks and logs in a user-writable data folder with its own paths class. Demonstrated by [TOML config seeded from a shipped template](../../../../mods/gatos/features/toml-config-seeded-template.md) and [qcow2 overlay guest disks](../../../../mods/gatos/features/qcow2-overlay-guest-disks.md).

## Background: how the game does it

The game puts mods in `<docs>/mods/<Mod>` (`ModLibrary.LocalModsFolderPath`; [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)) and gives the mod its folder as `Mod.DirectoryPath` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91)). It does not say where a mod's own writable data goes, and on the default install the mod folder lives in the documents tree, which is also where players expect their own data. gatOS's data dir default is `<MyDocuments>/My Games/Kitten Space Agency/mods/gatOS`, which on a default Windows install is the same directory as the mod folder (the comment in [gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs#L64-L72) says so). Naming is therefore the only thing keeping install files and user files apart.

## Lifecycle and timing

`GatOsPaths.ModDir` is set once in `StarMapAllModsLoaded` and the live config is loaded right after (`startup:all-mods-loaded`, [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L225-L236)); the data directory is created on first access. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **One class owns every location.** Two roots: the data dir (user-writable, runtime) and the mod dir (read-only install, set once at init). Never compute paths elsewhere:

   ```csharp
   // gatOS.Vm/GatOsPaths.cs L3-L48
   /// <summary>
   ///     The single home for every gatOS filesystem location. Two roots:
   ///     <list type="bullet">
   ///         <item>
   ///             <description>
   ///                 The <b>data dir</b> (user-writable, runtime) under
   ///                 <c>MyDocuments/My Games/Kitten Space Agency/mods/gatOS/</c> — disks, logs, config.
   ///             </description>
   ///         </item>
   ///         <item>
   ///             <description>
   ///                 The <b>mod dir</b> (read-only install) where <c>mod.toml</c> and the bundled guest
   ///                 image / QEMU live. Set once at mod init via <see cref="ModDir"/>.
   ///             </description>
   ///         </item>
   ///     </list>
   ///     Never hardcode these locations elsewhere (OS_PLAN.md T0.4).
   /// </summary>
   public static class GatOsPaths
   {
       // Overridable for tests; null means "compute the real per-OS data dir".
       private static string? _dataDirOverride;

       /// <summary>
       ///     The user-writable runtime data directory, created on first access. Defaults to
       ///     <c>&lt;MyDocuments&gt;/My Games/Kitten Space Agency/mods/gatOS</c> and can be redirected
       ///     for tests via <see cref="OverrideDataDirForTests"/>.
       /// </summary>
       public static string DataDir
       {
           get
           {
               var dir = _dataDirOverride ?? ComputeDefaultDataDir();
               Directory.CreateDirectory(dir);
               return dir;
           }
       }

       /// <summary>Where per-profile qcow2 overlays and the installed base image live.</summary>
       public static string DisksDir => EnsureSubdir("disks");

       /// <summary>Where QEMU stdout/stderr + serial logs are written.</summary>
       public static string LogsDir => EnsureSubdir("logs");

       /// <summary>The user config file (TOML); read/written by GameMod.</summary>
       public static string ConfigFile => Path.Combine(DataDir, "gatos.toml");
   ```

2. **Resolve the data dir from `MyDocuments`** (or, to follow an instance path, from `Constants.DocumentsFolderPath` - see [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md)). Create subfolders on first access:

   ```csharp
   // gatOS.Vm/GatOsPaths.cs L80-L96
   private static string EnsureSubdir(string name)
   {
       var dir = Path.Combine(DataDir, name);
       Directory.CreateDirectory(dir);
       return dir;
   }

   private static string RequireModDir()
       => ModDir ?? throw new InvalidOperationException(
           "GatOsPaths.ModDir has not been set. GameMod must set it at mod init before accessing bundled assets.");

   private static string ComputeDefaultDataDir()
   {
       // MyDocuments resolves to the OS user-documents folder on every platform.
       var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
       return Path.Combine(documents, "My Games", "Kitten Space Agency", "mods", "gatOS");
   }
   ```

3. **Ship the template under a different name.** `gatos.default.toml` in the install folder, `gatos.toml` in the data dir. The name difference is the safety mechanism: on Windows the two folders are the same directory, so shipping the live file would let an update overwrite a player's customisations:

   ```csharp
   // gatOS.Vm/GatOsPaths.cs L64-L72
   /// <summary>
   ///     The pre-generated default config shipped in the mod folder (<c>gatos.default.toml</c>).
   ///     Editable before the first launch; <see cref="ConfigFile"/> is seeded from it on first run
   ///     when it exists. Deliberately a different name from <see cref="ConfigFile"/>: on Windows the
   ///     mod folder and the data dir are the same directory, so shipping the live <c>gatos.toml</c>
   ///     would let a mod-update overwrite a player's customizations — the template never does.
   /// </summary>
   /// <exception cref="InvalidOperationException"><see cref="ModDir"/> has not been set.</exception>
   public static string BundledConfigFile => Path.Combine(RequireModDir(), "gatos.default.toml");
   ```

4. **Seed the live file only if it is missing.** Edits the player made to the template before the first launch carry over; updates never replace the live file:

   ```csharp
   // gatOS.GameMod/Configuration/GatOsConfig.cs L557-L592
   public static GatOsConfig LoadOrCreate(string path, string? bundledDefaultPath = null)
   {
       if (!File.Exists(path))
       {
           // Prefer seeding from the bundled default so pre-launch edits in the install folder
           // carry over; the copied file is then read below like any existing config.
           if (bundledDefaultPath is not null && File.Exists(bundledDefaultPath))
           {
               try
               {
                   File.Copy(bundledDefaultPath, path);
                   ModLog.Log.Info($"Seeded the config at '{path}' from the bundled default '{bundledDefaultPath}'.");
               }
               catch (Exception ex)
               {
                   ModLog.Log.Warn(
                       $"Could not seed the config from '{bundledDefaultPath}' ({ex.Message}); writing defaults instead.");
               }
           }

           if (!File.Exists(path))
           {
               var fresh = new GatOsConfig();
               try
               {
                   fresh.Save(path);
                   ModLog.Log.Info($"Created the default config at '{path}'.");
               }
               catch (Exception ex)
               {
                   ModLog.Log.Warn($"Could not write the default config to '{path}': {ex.Message}");
               }

               return fresh;
           }
       }
   ```

5. **Keep the build from wiping the live file.** The deploy target deletes stale top-level files from the install folder so a redeploy never leaves old DLLs, but excludes `gatos.toml`; the template is copied separately:

   ```xml
   <!-- gatOS.GameMod/gatOS.GameMod.csproj L50-L56 -->
   <!-- gatos.toml is excluded: on Windows the install dir and the runtime data dir are the same
        folder, so the live config lives here. Wiping it would discard the player's settings on
        every rebuild/redeploy. The shipped default is the separate gatos.default.toml below. -->
   <ItemGroup>
       <StaleDistFile Include="$(DistDir)*" Exclude="$(DistDir)gatos.toml"/>
   </ItemGroup>
   <Delete Files="@(StaleDistFile)"/>
   ```

   ```xml
   <!-- gatOS.GameMod/gatOS.GameMod.csproj L65-L71 -->
   <!-- Pre-generated default config: shipped in the install folder so the common knobs (VM
        memory/CPUs/disk) are visible and editable before the first launch. Shipped as
        gatos.default.toml (NOT the live gatos.toml — see the wipe note above): on first run
        gatOS seeds the data-dir gatos.toml from it (GatOsConfig.LoadOrCreate /
        GatOsPaths.BundledConfigFile), and a mod-update can never clobber the player's config.
        Keep gatos.default.toml in sync with GatOsConfig defaults. -->
   <Copy SourceFiles="$(MSBuildThisFileDirectory)Configuration\gatos.default.toml" DestinationFolder="$(DistDir)" Condition="Exists('$(MSBuildThisFileDirectory)Configuration\gatos.default.toml')"/>
   ```

6. **Keep large static blobs in the install and changes in the data dir.** The guest image and QEMU stay in the install folder (copied with `SkipUnchangedFiles` so a redeploy costs almost nothing); every change a guest makes goes to a per-profile qcow2 overlay in the data dir (`DisksDir`), so replacing the base image never loses user state ([qcow2 overlay guest disks](../../../../mods/gatos/features/qcow2-overlay-guest-disks.md)).

## Complete example

| File | Role |
|---|---|
| [gatOS.Vm/GatOsPaths.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GatOsPaths.cs) | Data dir, mod dir, config file, bundled template path |
| [gatOS.GameMod/Configuration/GatOsConfig.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Configuration/GatOsConfig.cs#L557-L619) | Seed once, then load tolerantly |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L44-L75) | Deploy target with the wipe exclusion and template copy |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L225-L236) | Sets `ModDir` and loads |

## Variations and gotchas

- **Keep the template in sync** with the class defaults (the csproj comment says so); a stale template seeds stale defaults.
- **Instance paths.** The data dir comes from `MyDocuments`, so it does not follow `-InstancePath`. gatOS's own config lives under the real user documents folder in all instances.
- **The template is not the config.** Players who edit `gatos.default.toml` after the first launch see no effect; the file header and the status UI say to edit `gatos.toml`.
- **Cleanup.** A data folder under `mods/<mod>` is removed by players who delete the mod folder to uninstall, taking their data with it; that is the cost of putting it there instead of in a sibling folder ([Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md)).
- **No automatic migration** between template versions: the live file keeps its older keys, and unknown or missing keys are handled by the tolerant loader ([Map one flat settings class to gatos.toml with Tomlyn and snake_case names](../../settings-formats/persist-mod-settings-toml/persist-mod-settings-toml-1.md)).

## See also

- [Keep settings beside the mod DLL, resolved from the loaded assembly](choose-where-a-mod-stores-its-files-1.md), [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md), [overview](./overview.md)
- [TOML config seeded from a shipped template](../../../../mods/gatos/features/toml-config-seeded-template.md), [qcow2 overlay guest disks](../../../../mods/gatos/features/qcow2-overlay-guest-disks.md)
- [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md)
