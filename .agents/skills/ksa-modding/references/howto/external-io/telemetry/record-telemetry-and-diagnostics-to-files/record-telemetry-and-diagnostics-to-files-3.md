---
type: How-To
title: Latched dumps from StarMapAfterGui behind a DebugConfig flag
description: Run validation dumps once per session after the universe exists, write text and SVG files built by pure code shared with offline tests, and log each error only once per key
category: external-io
group: telemetry
topic: record-telemetry-and-diagnostics-to-files
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf
  - mods/ksa-deltavmap/features/offline-layout-and-math-tests
techniques: [starmap-lifecycle, svg-dump]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Celestial
  - Brutal.Logging.DefaultCategory
  - Brutal.Logging.LogCategory
tags: [dump, svg, latch, validation, debug-config, log-once, offline-tests]
---

Outcome: a DEBUG build of the mod checks itself the first time a system is loaded: it logs bodies and key values, lays out a synthetic stress tree and the real system, asserts no overlaps, and writes SVG and text renderings to disk to open in a browser. Release builds pay one boolean check per frame. Hot paths are timed with zero-cost-when-off scopes, and every error path logs once per key. Requires StarMap. Demonstrated by [In-game validation dumps and perf scopes](../../../../mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf.md) and [Offline layout and math tests](../../../../mods/ksa-deltavmap/features/offline-layout-and-math-tests.md) in KSA-DeltaVMap.

## Background: how the game does it

StarMap exposes no "universe ready" attribute. The game itself has static `Program.OnGameLoaded` and `Universe.OnLoaded` methods ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2607)) that a mod could patch, but this mod avoids another patch and polls: `Universe.CurrentSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114-L114)) is null until a system is loaded. Logging goes through `Brutal.Logging.DefaultCategory.Log` ([current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs)); the dump uses `Info`, `Warning` and `Error`.

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (a Harmony postfix on `Program.OnDrawUiViewports`), main thread, `frame:draw-ui`. The dumps run after the mod's own window draw on the first frame at which `Universe.CurrentSystem` exists, whether or not the window is open. The dumps only read.
- That hook does not fire when the HUD is hidden, so a dump waits for a frame with the HUD shown; this is acceptable for a debug aid.
- Release builds compile `ValidationDump` and `Performance` to `false`, so a release build pays one boolean check per frame.
- `Unload` resets the latch, the perf tracker and the log-once sets, so a reload dumps again.

## Steps

1. **Gate with compile-time flags.** In a release build both are `false` constants of the build, so the dump never runs:

   ```csharp
   // DeltaVMap/Core/DebugConfig.cs L1-L22
   namespace DeltaVMap.Core;

   internal static class DebugConfig
   {
   #if DEBUG
       public static bool Performance = true;
       public static bool ValidationDump = true;
   #else
       public static bool Performance = false;
       public static bool ValidationDump = false;
   #endif

       public static bool Any => Performance || ValidationDump;

       // Local dev directory for the layout debug dump (in-game LayoutDump and the test
       // harness); debug-only scaffolding, so a hardcoded source-tree path is fine.
       public const string LayoutDumpDir = @"F:\Coding\KSA Modding\private\temp\deltav-layout";
   }

   ```

2. **Latch and guard in the draw hook.** Return early unless the flag is set, the latch is clear and `Universe.CurrentSystem` is non-null; set the latch before running so a throwing dump never repeats; wrap the whole run in a try/catch that logs once and never unwinds into the render path:

   ```csharp
   // DeltaVMap/Mod.cs L50-L72
       [StarMapAfterGui]
       public void Draw(double dt)
       {
           MapWindow.DrawActive(Program.MainViewport);

           if (_validationDumped || !DebugConfig.ValidationDump)
               return;
           if (Universe.CurrentSystem == null)
               return;

           _validationDumped = true;
           try
           {
               DvValidationDump.Run();
               VisualTreeDump.Run();
               LayoutDump.Run();
           }
           catch (Exception ex)
           {
               // Never let a dump failure unwind into the render path.
               LogHelper.ErrorOnce("validation-dump", $"[DvMap] Validation dump failed: {ex}");
           }
       }
   ```

3. **Log each error once per key.** Keys carry context (`"map-build-" + rootId`), so one bad body logs once without hiding a different one:

   ```csharp
   // DeltaVMap/Core/LogHelper.cs L1-L27
   using Brutal.Logging;

   namespace DeltaVMap.Core;

   internal static class LogHelper
   {
       private static readonly HashSet<string> _loggedWarnings = new();
       private static readonly HashSet<string> _loggedErrors = new();

       public static void WarnOnce(string key, string message)
       {
           if (_loggedWarnings.Add(key))
               DefaultCategory.Log.Warning(message);
       }

       public static void ErrorOnce(string key, string message)
       {
           if (_loggedErrors.Add(key))
               DefaultCategory.Log.Error(message);
       }

       public static void Reset()
       {
           _loggedWarnings.Clear();
           _loggedErrors.Clear();
       }
   }
   ```

4. **Cap and fall back.** The tree dump caps output per tree (600 lines); the layout dump creates its output directory and, if that fails, logs a warning and continues log-only:

   ```csharp
   // DeltaVMap/Layout/LayoutDump.cs L23-L39
       internal static void Run()
       {
           string outDir = DebugConfig.LayoutDumpDir;
           DefaultCategory.Log.Info($"{Tag} === Layout dump; writing to '{outDir}' ===");

           try
           {
               Directory.CreateDirectory(outDir);
           }
           catch (Exception ex)
           {
               DefaultCategory.Log.Warning($"{Tag} Could not create dump directory '{outDir}': {ex.Message}. SVG/text files will be skipped.");
               outDir = string.Empty;
           }

           // The game-independent acceptance case: a 100+ node tree with a three-hub bus.
           DumpLayout(SyntheticTree.BuildLargeMoonRoot(), LayoutConfig.Default, outDir, "synthetic 100+ node tree");
   ```

5. **Build the output with pure code shared with tests.** `LayoutDumpFormat` is pure string building (SVG, a fit-preview SVG, a text tree); the in-game dump and the offline tests call the same functions, and the layout runs without a text measurer so the character-count estimate matches between environments. `OverlapCheck` gives pass or fail verdicts instead of something to eyeball ([DeltaVMap/Layout/LayoutDumpFormat.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutDumpFormat.cs#L17-L42), [DeltaVMap/Layout/OverlapCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/OverlapCheck.cs)).

6. **Time hot paths with a ref-struct scope that costs nothing when off.** The constructor stamps the time only when `DebugConfig.Performance` is on, and `Dispose` records the elapsed ticks; call sites write `using var perf = new PerfTracker.Scope("DvMap.Draw.Canvas");` inside `#if DEBUG`. The tracker logs avg, min, max and call count every 5 s at debug level:

   ```csharp
   // DeltaVMap/Core/PerfTracker.cs L24-L40
       internal readonly ref struct Scope
       {
           private readonly string _name;
           private readonly long _start;

           public Scope(string name)
           {
               _name = name;
               _start = DebugConfig.Performance ? Stopwatch.GetTimestamp() : -1;
           }

           public void Dispose()
           {
               if (_start < 0) return;
               Record(_name, Stopwatch.GetTimestamp() - _start);
           }
       }
   ```

7. **Reset on unload** so a mod reload re-runs the dumps (`_validationDumped`, `PerfTracker`, `LogHelper.Reset()`) ([DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L74-L90)).

## Offline harness

Most of the logic is also tested without the game: a console project pulls individual `.cs` files from the mod with `<Compile Include>` and excludes the files that touch game types, so layout, closed-form kernels and formatting run on synthetic trees and hand-checked values and return a non-zero exit code on any failure ([DeltaVMap.Tests/DeltaVMap.Tests.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/DeltaVMap.Tests.csproj), [DeltaVMap.Tests/Program.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.Tests/Program.cs)). That only works because the mod keeps those kernels free of game types by design. Unit-testing mod logic without the game and laying out trees are separate topics in the testing-and-tooling category.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L46-L72) | One-shot dump trigger in the AfterGui handler |
| [DeltaVMap/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/DebugConfig.cs) | `#if DEBUG` flags and dump directory |
| [DeltaVMap/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/PerfTracker.cs) | Ref-struct timing scopes, periodic report |
| [DeltaVMap/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/LogHelper.cs) | Keyed once-only warning and error logging |
| [DeltaVMap/Layout/LayoutDump.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutDump.cs) | Layout dump with SVG and text files |
| [DeltaVMap/Layout/LayoutDumpFormat.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutDumpFormat.cs) | SVG and text rendering of a layout |

## Variations and gotchas

- **The dump directory is hardcoded** to the author's source tree in `DebugConfig.LayoutDumpDir`; the author calls this acceptable for debug-only scaffolding. In your own mod prefer a folder under the KSA documents path ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283)) and keep the log-only fallback.
- **One shot per session.** If a dump should be repeatable, add a button that clears the latch rather than polling again.
- **A DEBUG build only.** Do not ship the flags on; the dump allocates and writes files from the draw hook.
- **Do not log from tight loops.** The once-per-key dedupe sets are plain `HashSet<string>`s used on the main thread only.

## See also

- [Overview](overview.md)
- [Approach 2: buffered file sink](record-telemetry-and-diagnostics-to-files-2.md)
- [In-game validation dumps and perf scopes](../../../../mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf.md), [Offline layout and math tests](../../../../mods/ksa-deltavmap/features/offline-layout-and-math-tests.md)
- [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
