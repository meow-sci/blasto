---
type: Mod Feature
title: Host KSA in-process from a launcher executable
description: StarMap.exe reads StarMapConfig.json, loads KSA.dll and StarMap.Core into a custom AssemblyLoadContext, fixes the working and base directories, and invokes the game's Main by reflection
mod: starmap
category: getting-started
techniques: [assembly-load-context, reflection, json-config]
hooks: [reflection KSA.Program.Main (Assembly.EntryPoint), harmony PatchAll com.starmap.core]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Program, KSA.App]
tags: [starmap, loader, launcher, assembly-load-context, harmony, bootstrap]
---

StarMap is not injected into `KSA.exe`. The player starts `StarMap.exe`, a .NET 10 console
program, and that process becomes the game: it loads `KSA.dll` as a library, discovers and
initializes mods, applies its Harmony patches, and then calls the game's own `Main` on the same
thread. Everything a
mod later sees (one shared Harmony, KSA types with a single identity, the lifecycle hooks) follows
from how this host process is assembled.

## How it works

1. **Config.** `LoaderConfig.TryLoadConfig` reads `./StarMapConfig.json` relative to the current
   working directory. If the file is missing it writes a default one and exits with "Please fill
   the StarMapConfig.json and restart the program". Keys:[^sm-loaderconfig]

   | Key | Type | Meaning |
   |---|---|---|
   | `GameLocation` | string | KSA install folder or the full path of `KSA.dll`; a folder gets `KSA.dll` appended, and the file must exist |
   | `RepositoryLocation` | string | Serialized but never read by any code in this checkout (README: "can be kept empty") |
   | `GameArguments` | string[] | Appended to StarMap's own command-line arguments and passed to KSA's `Main` |

2. **Shared Harmony.** `Program.Main` loads `./0Harmony.dll` into `AssemblyLoadContext.Default`
   before anything else, so every later load context that consults `Default` first gets the same
   Harmony assembly.[^sm-program]
3. **Core load context.** `CoreAssemblyLoadContext` is built with two
   `AssemblyDependencyResolver`s: one rooted at `KSA.dll` (the game's dependency set) and one
   rooted at `./StarMap.Core.dll`. Its `Load` returns an assembly already in `Default` by simple
   name (case-insensitive), else the game's copy, else StarMap.Core's copy.[^sm-corealc]
4. **Load core and game.** `GameSurveyer.TryLoadCoreAndGame` loads `StarMap.Core.dll` into that
   context, finds the first concrete type assignable to `IStarMapCore` and constructs it with the
   context as argument, then loads `KSA.dll` into the same context.[^sm-gamesurveyer]
5. **Fix the directories.** It sets the process working directory to the KSA folder and sets the
   `APP_CONTEXT_BASE_DIRECTORY` AppContext value to the same folder. KSA's `Program.Main` starts
   with `Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory)`, and
   `BaseDirectory` would otherwise be StarMap's folder, so every relative `Content/...` path in the
   game would break.[^ksa-program]
6. **Initialize the core.** `StarMapCore.Init` first runs `ModLoader.Init` (instance-path
   override, manifest discovery, per-mod load contexts, each mod's `StarMapBeforeMain`), then
   `new Harmony("com.starmap.core").PatchAll(typeof(StarMapCore).Assembly)`, which applies the
   `ProgramPatcher`, `ModLibraryPatches` and `ModPatches` classes.[^sm-starmapcore]
7. **Run the game.** `RunGame` calls `_game.EntryPoint!.Invoke(null, [_args])`, which is
   `KSA.Program.Main(string[])`. That call returns only after the game loop in `App.Run` ends
   and `Program.Dispose` has run.[^ksa-app]
8. **Tear down.** `GameSurveyer` is a `using var`, so on return its `Dispose` calls
   `IStarMapCore.DeInit` (unpatch, then `StarMapUnload` hooks).

Resulting load contexts (all in one process):

| Load context | Created by | Contains |
|---|---|---|
| `Default` | .NET host | `StarMap.exe`'s own assembly, `StarMap.Types.dll`, `0Harmony.dll`, framework |
| `CoreAssemblyLoadContext` | `StarMap/Program.cs` | `KSA.dll` and its dependencies (`Brutal.*`, Tomlet, ...), `StarMap.Core.dll`, `StarMap.API.dll` |
| `ModAssemblyLoadContext`, one per mod | `RuntimeMod.TryCreateMod` | the mod's entry assembly and private dependencies |

`IStarMapCore` lives in `StarMap.Types`, which the launcher itself references, so it sits in
`Default`. When `StarMap.Core` (in the core context) references it, `CoreAssemblyLoadContext.Load`
returns the `Default` copy, which is why the `createdCore is not IStarMapCore` check in the
launcher succeeds across the context boundary.[^sm-istarmapcore] `StarMap.Core` excludes Tomlet's
runtime asset in its project file, so at run time it binds the game's Tomlet through the game
resolver.[^sm-core-csproj]

## Lifecycle and timing

All of this is `startup:starmap-before-main`. While it runs:

- `KSA.dll` is loaded but none of `Program`'s startup code has executed: no window, no Vulkan
  device, no ImGui context, no KSA log sinks, and `GameSettings.Current` still holds built-in
  defaults rather than the player's `settings.toml` (all of these are set up in the `Program`
  constructor).[^ksa-program]
- The working directory is already the KSA folder, so relative game paths resolve.
- Mods' `StarMapBeforeMain` methods run inside step 6, before StarMap's own Harmony patches are
  applied. That is harmless because no game code runs until step 7, and it gives a mod the
  earliest point at which it can patch code that runs inside the `Program` constructor.
- Everything runs on the process main thread, the same thread that later runs the frame loop.

## Key code

| Path | Role |
|---|---|
| [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L9-L29) | Entry point: config, shared Harmony, core context, run, dispose |
| [StarMap/LoaderConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/LoaderConfig.cs#L7-L51) | `StarMapConfig.json` read, default write, `GameLocation` normalization |
| [StarMap/CoreAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/CoreAssemblyLoadContext.cs#L11-L36) | Load context for KSA plus StarMap.Core |
| [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L61) | Loads core and game, fixes directories, invokes `Main`, disposes |
| [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L8-L36) | `IStarMapCore` implementation: mod loader init, then `PatchAll` |
| [StarMap.Types/IStarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Types/IStarMapCore.cs#L3-L7) | Cross-context contract `Init` / `DeInit` |

## Snippets

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

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `Program.Main(string[])` (private static, the assembly entry point) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) | Invoked through `Assembly.EntryPoint`; resets the working directory from `AppDomain.CurrentDomain.BaseDirectory`, parses CLI args, constructs `Program` and calls `Run` |
| `Program.CliArgs` / `ProcessCliArgs` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L102-L107) | Only `-build-info`, `-fixed-viewport` and `-force-x11` are recognized; any other argument StarMap forwards (for example `--restarted` or `-InstancePath`) is ignored by the game |
| `App.Run` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L74) | The frame loop; returns when `RequestExit` sets an exit code, which is when `RunGame` returns |

## Pitfalls and version notes

- **Working directory matters.** `./StarMapConfig.json`, `./0Harmony.dll` and
  `./StarMap.Core.dll` are resolved against the current directory, so `StarMap.exe` must be
  started from its own folder. KSAero's deploy targets generate an IDE launch profile with
  `workingDirectory` set to the StarMap folder for exactly this reason, and StarMap's own restart
  flow sets `WorkingDirectory` explicitly.[^ksaero-moddeploy]
- **KSA r3646 broke this once.** The `APP_CONTEXT_BASE_DIRECTORY` line was added when KSA began
  resetting its working directory from `AppDomain.CurrentDomain.BaseDirectory` in `Main`
  (StarMap commit 21dcef7, "Fix crash on startup with KSA r3646"). Any future change to how KSA
  locates its install folder can break the launcher the same way.
- **Nothing catches.** Neither `RunGame` nor KSA's `App.Run` wraps the frame in a `try`, so an
  exception thrown from any mod hook unwinds out of `Main` and ends the process.[^ksa-app]
- **Install guide.** The README tells players to run `StarMap.exe` once to generate the config,
  set `GameLocation`, and run it again.[^sm-readme] The Windows installer writes the file for them
  (see [Release packaging and installer](release-packaging-and-installer.md)).
- **Targets .NET 10** (`net10.0`) and references `Lib.Harmony` 2.4.2.[^sm-csproj]

## See also

- [Discover code mods through KSA's mod manifest](manifest-mod-discovery.md)
- [Isolate each mod in its own AssemblyLoadContext](per-mod-assembly-load-context.md)
- [Startup lifecycle hooks](startup-lifecycle-hooks.md)
- [Unload hook and shutdown order](unload-and-shutdown.md)
- [Generate a launchSettings profile so Rider or Visual Studio runs StarMap and hits mod breakpoints](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-5.md) - how-to article demonstrating this feature
- [Follow the boot sequence: host process, load contexts and hook binding](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md) - how-to article demonstrating this feature

[^sm-program]: StarMap/Program.cs
[^sm-loaderconfig]: StarMap/LoaderConfig.cs
[^sm-corealc]: StarMap/CoreAssemblyLoadContext.cs
[^sm-gamesurveyer]: StarMap/GameSurveyer.cs
[^sm-starmapcore]: StarMap.Core/StarMapCore.cs
[^sm-istarmapcore]: StarMap.Types/IStarMapCore.cs
[^sm-core-csproj]: StarMap.Core/StarMap.Core.csproj
[^sm-csproj]: StarMap/StarMap.csproj
[^sm-readme]: StarMap README
[^ksaero-moddeploy]: KSAero build/ModDeploy.targets
[^ksa-program]: KSA.Program (decompiled)
[^ksa-app]: KSA.App (decompiled)
