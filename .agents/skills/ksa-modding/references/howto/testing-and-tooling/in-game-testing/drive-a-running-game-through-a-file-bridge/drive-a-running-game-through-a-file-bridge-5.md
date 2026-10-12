---
type: How-To
title: Keep dumps and perf scopes out of release builds with a DEBUG flag and a runtime switch
description: Compile validation dumps and timing scopes only into Debug builds and gate their runtime use behind constant flags so a release build pays one boolean check
category: testing-and-tooling
group: in-game-testing
topic: drive-a-running-game-through-a-file-bridge
approach: 5
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf]
techniques: [msbuild, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.Universe]
tags: [testing, developer-only, debug-flag, validation-dump, perf]
---

Outcome: in-game self-checks (a one-shot validation dump the first time a star system is loaded, timing scopes on hot paths) that exist only in a Debug build, so
a release download has no code path that can run them. Demonstrated by KSA-DeltaVMap
([in-game validation dumps and perf](../../../../mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf.md)). Use this gate for diagnostics that are cheap to
rebuild for, instead of a bridge; prerequisite: a normal StarMap mod built with Debug and Release configurations.

## Background: how the game does it

Nothing in the game is involved in the gate; it is the C# compiler's `DEBUG` symbol, defined by the Debug configuration of the mod's project. What the game does
provide is the moment at which a dump may safely read it: the celestial system exists only after the scene is loaded, `Universe.CurrentSystem` is null before, and the
dump reads game state that is only stable on the main thread inside a frame hook.

## Lifecycle and timing

The dump runs in the `[StarMapAfterGui]` handler (`frame:draw-ui`, postfix on `Program.OnDrawUiViewports`, main thread). It runs after the window drew, on the first frame at which
`Universe.CurrentSystem` is non-null, and latches so it runs once per load. It only reads. Mind that `StarMapAfterGui` does not run while the HUD is hidden, so the dump
waits for a frame with the UI on ([the frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md), [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)).

## Steps

1. **Declare the flags once.** A Debug build turns them on, a Release build gets constant `false`:

   ```csharp
   // DeltaVMap/Core/DebugConfig.cs L1-L13
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
   ```

   The hard-coded `LayoutDumpDir` constant on the next lines is the author's own machine path; put yours in an environment variable or a gitignored file instead, and make a failed `Directory.CreateDirectory` degrade to log-only as the mod does.

2. **Compile the instrumentation type out of release.** The whole `PerfTracker` file is inside `#if DEBUG`, so the type does not exist in a release assembly. Its timing scope is a `readonly ref struct` that stamps the timestamp only when the runtime flag is on:

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

   Call sites wrap their `using` in `#if DEBUG` too ([DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1100-L1102)). A periodic report (every 5 s: average, minimum, maximum and call count per key) is covered in [Measure what a mod costs per frame](../measure-and-budget-mod-performance/overview.md).

3. **Fire the dump once, never letting it unwind into the render path:**

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

4. **Log failures once per key** so a bad body logs one line instead of a flood:

   ```csharp
   // DeltaVMap/Core/LogHelper.cs L5-L20
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
   ```

5. **Reset the latch on unload** (`Mod.Unload` resets `_validationDumped`, the tracker and the dedupe sets) so a reload dumps again, and build the Release package from the Release configuration only: the project's `PackageRelease` target runs only for `Configuration == 'Release'` ([DeltaVMap/DeltaVMap.csproj](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/DeltaVMap.csproj#L67-L79)).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/DebugConfig.cs) | Constant flags per configuration |
| [DeltaVMap/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/PerfTracker.cs) | DEBUG-only scope and report |
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs) | One-shot dump trigger |
| [DeltaVMap/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/LogHelper.cs) | Keyed once-only logging |

## Variations and gotchas

- **The dump classes still compile into release** (only their trigger is dead code, and the `PerfTracker` type and its call sites are removed); wrap them in `#if DEBUG` as well if the binary size or the symbol surface matters.
- **A release build cannot be diagnosed.** If a tester reports a bug you cannot reproduce, you must send them a Debug build, or use the marker-file gate from [article 1](drive-a-running-game-through-a-file-bridge-1.md) or the manifest key from [article 4](drive-a-running-game-through-a-file-bridge-4.md).
- **Never ship a Debug build.** Nothing stops it; a Debug build also pays the timing cost. Build packages only in Release.
- **Absolute paths** in debug scaffolding fail on other machines; fall back to the log.

## See also

- [Overview](overview.md), [article 1](drive-a-running-game-through-a-file-bridge-1.md)
- [Measure what a mod costs per frame](../measure-and-budget-mod-performance/overview.md), [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [KSA-DeltaVMap in-game validation dumps and perf](../../../../mods/ksa-deltavmap/features/in-game-validation-dumps-and-perf.md)
