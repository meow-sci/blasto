---
type: How-To
title: "Follow the boot sequence: host process, load contexts and hook binding"
description: A numbered walkthrough of StarMap.exe from StarMapConfig.json to the first mod hook, with the three load contexts, what resolves where and how to observe it from a mod
category: getting-started
group: loader
topic: how-starmap-launches-ksa
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/host-process-launch
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/per-mod-assembly-load-context
  - mods/starmap/features/mod-dependency-graph
  - mods/starmap/features/attribute-hook-discovery
techniques: [assembly-load-context, starmap-lifecycle, reflection]
hooks:
  - reflection System.Reflection.Assembly.Location
  - reflection System.Runtime.Loader.AssemblyLoadContext.GetLoadContext
lifecycle: [startup:starmap-before-main, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Program, KSA.App, KSA.ModLibrary, KSA.Mod]
tags: [starmap, launcher, assembly-load-context, boot-sequence, hooks, harmony]
---

StarMap is not injected into `KSA.exe`. The player starts `StarMap.exe`, a .NET console program, and
that process becomes the game: it loads `KSA.dll` as a library, discovers and initializes mods,
applies Harmony patches and then calls KSA's own `Main`. This article is a reference for that sequence
so a mod author can predict **what is loaded, in which context and when**. It is a reading
article; the practical consequences for packaging are in [Ship third-party DLLs next to your mod](../../dependencies/ship-third-party-dlls-with-a-mod/overview.md) and
for dependencies in [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md). The sub-topics in the sibling articles are the
mod folder rules ([Folder and manifest rules as StarMap consumes them](./how-starmap-launches-ksa-2.md)), the per-instance data folder
([Manual Harmony prefix on Constants.DocumentsFolderPath](./how-starmap-launches-ksa-3.md)) and patching a not-yet-loaded game assembly
([Preload into the core load context from StarMapBeforeMain](./how-starmap-launches-ksa-4.md)). Demonstrated by StarMap itself; sources are pinned at StarMap
`4dc60f8`.

## Background: how the game does it

KSA's entry point is `Program.Main(string[])`. It first resets the working directory from
`AppDomain.CurrentDomain.BaseDirectory`, runs `ProcessCliArgs` over a dictionary that recognizes only
`-build-info`, `-fixed-viewport` and `-force-x11`, constructs `Program` (the heavy startup) and calls
`Run`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) Because the first line trusts `BaseDirectory`, a launcher that hosts the
game must make `BaseDirectory` the game folder, which StarMap does explicitly (step 7 below). Unknown
arguments such as `--restarted` or `-InstancePath` are ignored by the game, which is why StarMap can
forward them freely. `App.Run` is the frame loop and returns when an exit code is requested.[current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74)

## Lifecycle and timing

Steps 1 to 9 all happen under `startup:starmap-before-main`: `KSA.dll` is loaded but none of the
`Program` constructor has run, so there is no window, Vulkan device, ImGui context or KSA log sink, and
`GameSettings.Current` still holds defaults. Mods' `StarMapBeforeMain` methods run inside step 8,
**before** StarMap applies its own Harmony patches. The hooks that need a running game
(`StarMapImmediateLoad`, `StarMapAllModsLoaded`, frame hooks) fire later from those patches; their
exact positions are in [StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

The boot sequence, in order:

1. **Start `StarMap.exe` from its own folder.** `./StarMapConfig.json`, `./0Harmony.dll` and
   `./StarMap.Core.dll` are resolved against the current directory.
2. **Read the config.** `LoaderConfig.TryLoadConfig` reads `StarMapConfig.json`; if missing it writes a
   default and exits with "Please fill the StarMapConfig.json and restart the program".[StarMap/LoaderConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/LoaderConfig.cs#L7-L51)

   | Key | Meaning |
   |---|---|
   | `GameLocation` | KSA install folder or full path of `KSA.dll`; a folder gets `KSA.dll` appended and the file must exist |
   | `RepositoryLocation` | Serialized but never read in this checkout |
   | `GameArguments` | Appended to StarMap's own arguments and passed to KSA's `Main` |

3. **Share Harmony.** StarMap's own `Program.Main` loads `./0Harmony.dll` into `AssemblyLoadContext.Default` first, so
   every context that consults `Default` first gets the same Harmony assembly.
4. **Create the core context and run.** `CoreAssemblyLoadContext` has two `AssemblyDependencyResolver`s,
   one rooted at `KSA.dll`, one at `StarMap.Core.dll`:[StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29)

   ```csharp
   // StarMap/Program.cs L9-L29
   static async Task Main(string[] args)
   {
       var gameConfig = new LoaderConfig();

       if (!gameConfig.TryLoadConfig())
       {
           return;
       }

       AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath("./0Harmony.dll"));

       var gameAssemblyContext = new CoreAssemblyLoadContext(gameConfig.GameLocation);
       using var gameSurveyer = new GameSurveyer(gameAssemblyContext, gameConfig.GameLocation, [.. args, ..gameConfig.GameArguments]);
       if (!gameSurveyer.TryLoadCoreAndGame())
       {
           Console.WriteLine("StarMap - Unable to load mod manager and game.");
           return;
       }

       gameSurveyer.RunGame();
   }
   ```

5. **Resolve into the core context.** The context's `Load` returns an assembly already in `Default` by
   simple name, else the game's copy, else StarMap.Core's copy:[StarMap/CoreAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/CoreAssemblyLoadContext.cs#L21-L36)

   ```csharp
   // StarMap/CoreAssemblyLoadContext.cs L21-L36
   protected override Assembly? Load(AssemblyName assemblyName)
   {
       var existingInDefault = Default.Assemblies
           .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
       if (existingInDefault != null)
           return existingInDefault;

       var path = _gameDependencyResolver.ResolveAssemblyToPath(assemblyName);

       if (path is not null)
           return LoadFromAssemblyPath(path);

       path = _starMapDependencyResolver.ResolveAssemblyToPath(assemblyName);

       return path != null ? LoadFromAssemblyPath(path) : null;
   }
   ```

6. **Load the core and the game.** `GameSurveyer.TryLoadCoreAndGame` loads `StarMap.Core.dll` into the
   context, instantiates the first concrete `IStarMapCore`, then loads `KSA.dll` into the same context.
7. **Fix the directories.** It sets the working directory to the KSA folder and sets the
   `APP_CONTEXT_BASE_DIRECTORY` AppContext value to the same folder. Without the second, KSA's
   `Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory)` would jump back to StarMap's
   folder and every relative `Content/...` path would break.[StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L61)

   ```csharp
   // StarMap/GameSurveyer.cs L25-L56
   public bool TryLoadCoreAndGame()
   {
       var modManagerAssembly = _gameAssemblyContext.LoadFromAssemblyPath(Path.GetFullPath("./StarMap.Core.dll"));

       var starMapCore = modManagerAssembly.GetTypes().FirstOrDefault((type) => typeof(IStarMapCore).IsAssignableFrom(type) && !type.IsInterface);
       if (starMapCore is null) return false;
       var createdCore = Activator.CreateInstance(starMapCore, [_gameAssemblyContext]);
       if (createdCore is not IStarMapCore core) return false;

       _game = _gameAssemblyContext.LoadFromAssemblyPath(_gameLocation);

       var gameDirectory = Path.GetDirectoryName(_gameLocation);
       if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
       {
           Console.WriteLine("StarMap - Game directory not found");
           return false;
       }

       Directory.SetCurrentDirectory(gameDirectory);
       AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", gameDirectory + Path.DirectorySeparatorChar);

       _core = core;
       core.Init();
       return true;
   }

   public void RunGame()
   {
       Debug.Assert(_game is not null, "Load needs to be called before running game");

       _game.EntryPoint!.Invoke(null, [_args]);
   }
   ```

8. **Initialize the core.** `core.Init()` runs `ModLoader.Init` and then Harmony `PatchAll` for StarMap's
   own patches. `ModLoader.Init` does, in order:[StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L8-L36)
   1. `DocumentsPathPatches.Apply()` (instance-path override, see [Manual Harmony prefix on Constants.DocumentsFolderPath](./how-starmap-launches-ksa-3.md));
   2. `ModLibrary.PrepareManifest()`, the same call KSA makes later, cached so StarMap and KSA see one manifest;
   3. for each **enabled** entry in manifest order: `RuntimeMod.TryCreateMod` finds `mod.toml` under
      `Content/<id>` then `<Documents>/.../mods/<id>`, reads `[StarMap]`, defaults `EntryAssembly` to the
      id, skips silently if `<EntryAssembly>.dll` is absent, otherwise creates a `ModAssemblyLoadContext`
      and loads the DLL by name;
   4. dependency resolution: a mod whose `ModDependencies` are not all registered yet is parked, others go
      on to `InitializeMod` (see [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md));
   5. `InitializeMod`: first type with an attribute *named* `StarMapModAttribute`, `Activator.CreateInstance`,
      bind public methods whose attribute names match StarMap's known hooks and whose signatures pass
      `IsValidSignature`, then invoke the mod's `StarMapBeforeMain`;[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)
   6. finally, parked mods whose remaining dependencies are all optional are initialized.

   ```csharp
   // StarMap.Core/StarMapCore.cs L25-L35
   public void Init()
   {
       _loader.Init();
       _harmony.PatchAll(typeof(StarMapCore).Assembly);
   }

   public void DeInit()
   {
       _harmony.UnpatchAll();
       _loader.Dispose();
   }
   ```

9. **Apply StarMap's Harmony patches** (`ProgramPatcher`, `ModLibraryPatches`, `ModPatches`). They turn into
   the hook dispatchers: prefix on `Mod.PrepareSystems` (`StarMapImmediateLoad`), postfix on
   `ModLibrary.LoadAll` (`StarMapAllModsLoaded`), prefix on `Program.OnDrawUiFrame`, postfix on
   `Program.OnDrawUiViewports` and `Program.OnFrame`, and a prefix on `ModLibrary.PrepareAll` for the restart
   prompt.
10. **Run KSA.** `RunGame` invokes `KSA.Program.Main` through `Assembly.EntryPoint` on the same thread. It
    returns only after `App.Run` ends and `Program.Dispose` has run.
11. **Tear down.** `GameSurveyer` is a `using var`, so `Dispose` calls `DeInit`: `UnpatchAll()` with **no id**
    (removing every Harmony patch in the process), then each mod's `StarMapUnload`.

## Load contexts and what resolves where

```
AssemblyLoadContext.Default      StarMap.exe, StarMap.Types.dll, 0Harmony.dll, framework
        ^  (consulted first by both contexts below, by simple name)
CoreAssemblyLoadContext          KSA.dll + its deps (Brutal.*, Tomlet, ...), StarMap.Core.dll, StarMap.API.dll
        ^  (consulted second by every mod context)
ModAssemblyLoadContext x N       one per mod: entry DLL + private deps from <Entry>.deps.json
```

A mod's `Load(AssemblyName)` tries, in order: `Default` by name, the core context's loaded assemblies by
name, `coreContext.LoadFromAssemblyName`, the contexts of dependency mods whose `ImportedAssemblies`
contains the name, and finally its own `AssemblyDependencyResolver`.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72) Consequences:

- Harmony and the game are **shared**: a mod's patches and the game's types have one identity everywhere.
- Anything the game (or `Default`) can resolve wins over a copy in your folder, whatever version you built
  against. Shipping `0Harmony.dll` or `StarMap.API.dll` is pointless.
- Two mods shipping the same library get two separate copies with separate statics unless one explicitly
  imports the other's (see [Share types and design an inter-mod API between StarMap mods](../../dependencies/share-types-between-mods/overview.md)).
- Native libraries are not resolved through the mod's `deps.json` (`LoadUnmanagedDll` is not overridden),
  so a NuGet-style `runtimes/<rid>/native/` layout is never found. The runtime's default `DllImport`
  probing still looks in the directory of the calling assembly, so a file with the exact platform name
  beside the mod DLL loads (see [Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md)).
- Contexts are never unloaded and are not named (the constructor calls `base()`).

## Complete example

All of it is in StarMap: [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs), [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs), [StarMap/CoreAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/CoreAssemblyLoadContext.cs), [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs),
[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs), [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs) and [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs). From a mod, the observable facts are:

```csharp
// adapted from RealAtmospheres ModMain.cs L44-L57 and OhMyStars src/ModPaths.cs L29-L52
// where did the loader put me?
string modFolder = Path.GetDirectoryName(typeof(Mod).Assembly.Location)!;        // real path in the mod folder
var myAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Mod).Assembly);
var gameAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(KSA.Program).Assembly);
Console.WriteLine($"mod ALC != game ALC: {!ReferenceEquals(myAlc, gameAlc)}");
```

`Assembly.Location` is a real path because the mod context loads from a file path; the two contexts differ,
but types from `KSA.dll` are the same objects in both.

## Variations and gotchas

- **Hosting a .NET game in your own launcher.** The pattern generalizes: pre-load shared libraries into
  `Default`, create a custom `AssemblyLoadContext` that falls back to `Default` then the game's
  `AssemblyDependencyResolver`, load the game assembly, set both the working directory and
  `APP_CONTEXT_BASE_DIRECTORY` (many games reset the directory from `BaseDirectory`), then call
  `Assembly.EntryPoint.Invoke(null, [args])`. StarMap's commit "Fix crash on startup with KSA r3646" added the
  `APP_CONTEXT_BASE_DIRECTORY` line for exactly this reason; expect similar breakage whenever the game changes
  how it finds its folder.
- **Nothing catches.** Neither `RunGame` nor `App.Run` wraps a frame in a `try`, and StarMap invokes hooks
  without one, so an exception from a hook unwinds out of `Main` and ends the process (see
  [Keep mod exceptions out of KSA's frame loop](../../lifecycle/contain-exceptions-in-starmap-hooks/overview.md)).
- **Exceptions in `StarMapBeforeMain`** propagate out of `ModLoader.Init`, so the launch aborts.
- **The game ignores extra CLI arguments**; only the three `-` flags above exist, so passing
  `GameArguments` gains nothing except for mods that read `Environment.GetCommandLineArgs()` themselves.
- **Working directory.** Start `StarMap.exe` from its own folder, or its relative paths fail. IDE launch
  profiles must set it (see [Generated launchSettings profile from ModDeploy.targets](../../build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-5.md)).
- **First run.** The README instructs: run once to generate the config, set `GameLocation`, run again. The
  Windows installer skips that step by writing `StarMapConfig.json` with the KSA folder chosen during setup.

## See also

- [Folder and manifest rules as StarMap consumes them](./how-starmap-launches-ksa-2.md), [Manual Harmony prefix on Constants.DocumentsFolderPath](./how-starmap-launches-ksa-3.md), [Preload into the core load context from StarMapBeforeMain](./how-starmap-launches-ksa-4.md), [How StarMap launches KSA and loads your mod](./overview.md)
- [Host KSA in-process from a launcher executable](../../../../mods/starmap/features/host-process-launch.md), [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md),
  [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md), [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md)
- [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md)
