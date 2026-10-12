---
type: How-To
title: Anchor the log on the game's logs folder and gate developer tools with a marker file
description: Resolve the log directory from Constants.LogsFolderPath instead of guessing, and switch developer-only tools on with an empty file that only the deploy script writes
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/own-log-file-and-developer-switch]
techniques: [logging, starmap-lifecycle, msbuild]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:immediate-load, startup:all-mods-loaded, event:shutdown]
game_types: [KSA.Constants]
tags: [logging, constants, logs-folder, developer-mode, marker-file, deploy]
---

KSArmory keeps `KSArmory.log` (same batched, rotated class as [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md)) and adds two practices
worth copying: a `Build` class that reads identity from the assembly, and a **developer marker**, an empty file
named `developer` beside the DLL that `tools/deploy.sh` writes and the release archive never contains, which turns
on the bridge, scenario runner and research switches. One correction up front: despite this article's
title, KSArmory's own `Log` does **not** use `Constants.LogsFolderPath`; it probes candidate directories
(approach 3). The adapted code below shows what anchoring on the constant looks like.

## Background: how the game does it

`Constants.LogsFolderPath` is `Path.Combine(DocumentsFolderPath, "logs")` (lower-case), and the `Program`
constructor calls `Directory.CreateDirectory(Constants.LogsFolderPath)` at the very top of startup, so the folder
exists by the time any StarMap hook after `StarMapBeforeMain` runs.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287)[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L696)
`DocumentsFolderPath` is the property StarMap patches for `-InstancePath` / `STARMAP_INSTANCE_PATH`
([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45)), so anything derived from `LogsFolderPath` follows an instance override while a
hand-built `MyDocuments/...` path does not.

## Lifecycle and timing

- First write: the `loading` line in `OnImmediateLoad` (`startup:immediate-load`, StarMap's prefix on
  `Mod.PrepareSystems`), after the constructor created the logs folder (Program.cs L694).
- `Build` statics initialize on first access, also in `OnImmediateLoad`; they touch only assembly metadata and one
  file-system probe.
- `[StarMapAllModsLoaded]` starts the developer tools only when `Build.Developer`, and logs which kind of install
  it is.
- Last write: `Unload` logs `unloaded` then `Log.Shutdown()`.

## Steps

1. **Resolve the folder from the game constant** (adapted; KSArmory guesses instead). Fall back to a temp folder if
   the constant's folder is missing:

   ```csharp
   // adapted from KSArmory/src/KSArmory/Ksa/Log.cs L202-L229: candidate source replaced by the game constant
   private static IEnumerable<string> CandidateDirectories()
   {
       yield return KSA.Constants.LogsFolderPath;   // follows -InstancePath; created by the Program constructor
       yield return Path.GetTempPath();
   }
   ```

   Everything else in `EnsureResolved` (rotate, header, `_path`) stays as in approach 1. Reference `KSA.dll`
   (`Private=false`, [Reference the game's assemblies from a mod project without shipping them](../../build/reference-game-assemblies-from-a-mod-project/overview.md)).
2. **Expose the folder for sidecar files.** KSArmory's `Log.Folder` is "the one place this mod knows how to find on
   every platform"; the bridge, scenario files, settings fallback and save repair all put their files beside the
   log:[src/KSArmory/Ksa/Log.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Log.cs#L187-L198)

   ```csharp
   // src/KSArmory/Ksa/Log.cs L187-L198
       /// <summary>
       /// The folder the log went to, which is the one place this mod knows how to find on every
       /// platform. Anything else it writes goes beside it rather than repeating the search.
       /// </summary>
       public static string Folder
       {
           get
           {
               lock (Gate) EnsureResolved();
               return _path is null ? Path.GetTempPath() : Path.GetDirectoryName(_path) ?? Path.GetTempPath();
           }
       }
   ```

3. **Read build identity from the assembly**, not from constants that go stale. `Developer` is the marker file:[src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs#L20-L34)

   ```csharp
   // src/KSArmory/Ksa/Build.cs L20-L34
       public static bool Developer { get; } = ResolveDeveloper();

       private static bool ResolveDeveloper()
       {
           try
           {
               string? dir = Path.GetDirectoryName(typeof(Build).Assembly.Location);
               return dir is not null && File.Exists(Path.Combine(dir, "developer"));
           }
           catch
           {
               return false;
           }
       }
   ```

   `KsaRunning` is `typeof(KSA.Vehicle).Assembly.GetName().Version`, `KsaBuild` is the stamped
   `AssemblyMetadata` (see [Mixed assets list with csproj content copy and build stamp](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-2.md) for the stamp, [Stamp the targeted build into assembly metadata](../check-the-game-version/check-the-game-version-2.md) for the
   comparison).
4. **Have the deploy script write the marker, and never the packaging script:**[tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh)

   ```bash
   # tools/deploy.sh L67-L71
   # Marks this as a developer's install, which shows the bridge, the scenario runner and the panel's
   # developer controls (Build.Developer). package.sh stages from the build output, never from here, so
   # the release archive cannot carry it.
   : > "$TARGET/developer"
   ```

   Equivalent MSBuild: a post-build `Touch` into the deploy folder in your deploy target
   ([Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md)).
5. **Log identity and gate tools at startup**, in `OnFullyLoaded`:[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L218-L229)

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L218-L229
           if (Build.Developer)
           {
               _scenario.Begin(ScenarioRunner.Requested());
               _bridge = new Bridge(_config, craft => _roster?.For(craft)?.Weapon,
                                    () => _roster?.All ?? [], () => _roster?.Loose ?? [],
                                    _countermeasures, craft => _ui?.Manage(craft),
                                    () => _heads?.All ?? []);
           }
   
           Log.Info(Build.Developer
                        ? "developer install: the bridge, the scenario runner and the developer controls are on"
                        : "player install: developer tools are off");
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Log.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Log.cs) | Same `Log` class as KSAGolf/KSACars; `Folder` for sidecar files |
| [src/KSArmory/Ksa/Build.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Build.cs) | `Version`, `KsaBuild`, `KsaRunning`, `Developer` |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/deploy.sh) | Writes the marker |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Logs identity at immediate load, starts developer tools |

## Variations and gotchas

- **Lower-case `logs`.** KSA's folder is `logs`; KSArmory's guess is `Logs`, which only matches by accident on
  Windows. The constant avoids this entirely.
- **Instance paths.** The probing code ignores `-InstancePath`; the constant does not (until `StarMapUnload`, where
  StarMap's patch is gone, see [StarMapUnload plus ProcessExit for exit paths that skip it](../clean-up-on-game-exit/clean-up-on-game-exit-1.md)).
- **Built for 5541, running 5554.** KSArmory's `ksa-assemblies.lock` pins `2026.10.7.5541`; on build 5554 its
  startup line reads `reporting off`, which is the intended consequence of a build mismatch.
- **Docs drift.** KSArmory's modding notes claim `Console.WriteLine` reaches the in-game console; the decomp does
  not show a stdout redirect (unverified).
- **The marker is a convention**, not a security boundary.

## See also

- [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md), [Path probing across Documents and XDG locations](./write-a-per-mod-log-file-3.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Own log file, build stamps and a developer marker file](../../../../mods/ksarmory/features/own-log-file-and-developer-switch.md)
- [Warn when the running game build differs from the one a mod was tested on](../check-the-game-version/overview.md), [Deploy a mod on every build and debug it from an IDE](../../build/deploy-a-mod-on-every-build/overview.md), [Manual Harmony prefix on Constants.DocumentsFolderPath](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)
