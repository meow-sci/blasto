---
type: How-To
title: Keep library code game-free with a logging interface swapped for a Brutal sink at load
description: Log through a tiny IModLogger interface that defaults to the console and is replaced by a LogCategory-backed sink at StarMapAllModsLoaded, so game-free projects stay testable
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/logging-and-perfstats]
techniques: [logging, starmap-lifecycle, msbuild]
hooks: [starmap StarMapAllModsLoaded, call Brutal.Logging.LogCategory.Make]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: [Brutal.Logging.LogCategory, Brutal.Logging.LogSystem]
tags: [logging, interface, testability, game-free, gatos, partial-methods]
---

gatOS has eleven projects, ten of which must build and run unit tests without the game, yet their log lines belong in
KSA's log when running in the game. It solves this with a game-free `IModLogger` interface, a static `ModLog.Log` that
defaults to a console logger, and a game-side `BrutalModLogger` swapped in at load. This is the structure to use when
your mod has a testable core ([Game-free half and compile-gated Game half](../../build/build-a-mod-without-game-references/build-a-mod-without-game-references-2.md) shows the build side of that layering).
The direct approach for single-project mods is [One LogCategory in a tiny shared assembly](./write-a-per-mod-log-file-4.md).

## Background: how the game does it

`LogCategory.Make(name)` binds a logger from `LogSystem.Factory` at creation time, and calls on a category whose
system is not enabled silently do nothing: `LogSystem.IsEnabled` defaults to false until the `Program` constructor
sets it.[current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L44-L56)[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L725) The Brutal log methods take interpolated-string handlers with
caller-info parameters; passing a prebuilt string works but formats eagerly.

## Lifecycle and timing

The swap is the first step of `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), before anything else logs.
`LogSystem` is initialized by then in practice; the sink's constructor guard covers the case where it is not, and the
mod simply stays on the console logger. After the swap, logs may be written from any thread (VM, SSH, MQTT workers),
which is why the sink never throws.

## Steps

1. **Define the interface and a static holder in a game-free assembly** with a console default:[gatOS.Logging/ModLog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/ModLog.cs#L12-L45)

   ```csharp
   // gatOS.Logging/ModLog.cs L31-L41 (closing brace and the interface elided)
   public static class ModLog
   {
       /// <summary>The active logger. Console-backed until a host calls <see cref="SetLogger"/>.</summary>
       public static IModLogger Log { get; private set; } = new ConsoleLogger();

       /// <summary>
       ///     Replaces the active logger (e.g. GameMod installs a Brutal/StarMap-backed sink).
       /// </summary>
       /// <param name="logger">The new logger. Must not be <c>null</c>.</param>
       public static void SetLogger(IModLogger logger)
           => Log = logger ?? throw new ArgumentNullException(nameof(logger));
   ```
   with `IModLogger` exposing `Debug`, `Info`, `Warn` and `Error(string, Exception?)`. Every library calls
   `ModLog.Log.Info(...)` and never references a game assembly.
2. **Write the game-side sink**, which refuses to construct when the game log system is off (so the caller keeps the
   console logger) and falls back to the console if a call throws:[gatOS.GameMod/Game/BrutalModLogger.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/BrutalModLogger.cs#L21-L38)

   ```csharp
   // gatOS.GameMod/Game/BrutalModLogger.cs L21-L38
       public BrutalModLogger()
       {
           if (!LogSystem.IsEnabled)
               throw new InvalidOperationException("the game log system is not initialized");
           _log = LogCategory.Make("gatOS");
       }

       public void Debug(string message) => Try(() => _log.Debug(message), "DBG", message);

       public void Info(string message) => Try(() => _log.Info(message), "INF", message);

       public void Warn(string message) => Try(() => _log.Warning(message), "WRN", message);

       public void Error(string message, Exception? ex = null)
       {
           var full = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
           Try(() => _log.Error(full), "ERR", full);
       }
   ```

3. **Install it through a compile-gated partial method**, so the entry class builds with no game assemblies and a
   missing assembly fails at the call site inside a `try`:[gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L214-L219)

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L214-L219
       // NoInlining on the partial impls: their bodies reference game assemblies, and a missing
       // assembly must fail at the call site (where Mod.cs catches it), not at the JIT of the
       // calling method — inlining would hoist the type resolution into the caller.
       [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
       partial void InstallGameLogging() => ModLog.SetLogger(new BrutalModLogger());
   ```

   and in the always-compiled half:[gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L955-L972)

   ```csharp
   // gatOS.GameMod/Mod.cs L961-L972
       private void TryInstallGameLogging()
       {
           try
           {
               InstallGameLogging(); // partial: compiled out when the KSA assemblies are absent
           }
           catch (Exception ex)
           {
               Console.WriteLine($"gatOS: game-backed logging unavailable, staying on console: {ex.Message}");
           }
       }
   ```

4. **Call it first** in `OnFullyLoaded` (`TryInstallGameLogging(); _instance = this; ...`), so every later line goes
   to the game log.
5. **Keep the contract tiny.** Four members (`Debug`, `Info`, `Warn`, `Error`) are enough; `Error` takes the exception so the sink formats it once.
   Add `PerfStat`-style counters separately if you need timing (gatOS measures sample time and allocated bytes per
   sample with a struct scope and `GC.GetAllocatedBytesForCurrentThread()`).

## Complete example

| File | Role |
|---|---|
| [gatOS.Logging/ModLog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Logging/ModLog.cs) | Interface, static holder, console default |
| [gatOS.GameMod/Game/BrutalModLogger.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/BrutalModLogger.cs) | Brutal-backed sink with console fallback |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs) | Partial-method implementation (compile-gated `Game/` folder) |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs) | `TryInstallGameLogging`, `OnFullyLoaded` |

## Variations and gotchas

- **Library tests use the default console logger** (or `ModLog.ResetToDefault()`, a test hook).
- **Partial methods with no implementation vanish with their calls**, so a no-game build still compiles; they must
  return `void` and have no `out` parameters.
- **`LogCategory` calls on a disabled system no-op**: the constructor guard is why gatOS keeps the console sink in
  that case instead of swallowing messages.
- **Single writer for stats.** `GC.GetAllocatedBytesForCurrentThread()` measures only the calling thread.
- **Category names are global**, so two mods using `Make("gatOS")` would share one object.

## See also

- [One LogCategory in a tiny shared assembly](./write-a-per-mod-log-file-4.md), [Thread-safe warn-once set reset on unload](./write-a-per-mod-log-file-6.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Logging shim and perf stats](../../../../mods/gatos/features/logging-and-perfstats.md), [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md)
- [Build a KSA mod that references only StarMap.API and Harmony](../../build/build-a-mod-without-game-references/overview.md), [StarMapAllModsLoaded - work that needs the full mod set](../choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md)
