---
type: Mod Feature
title: Debug-build validation dumps, perf scopes and once-only logging
description: In DEBUG builds the AfterGui hook runs a one-shot delta-v, visual-tree and layout dump (log lines plus SVG/text files) on the first loaded system, ref-struct PerfTracker scopes time the hot paths, and keyed WarnOnce/ErrorOnce logging keeps per-frame failures from flooding the log
mod: ksa-deltavmap
category: testing-and-tooling
techniques: [starmap-lifecycle, conditional-compilation, svg-dump, log-dedupe]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Universe, KSA.CelestialSystem, KSA.Celestial, Brutal.Logging.DefaultCategory]
tags: [testing, debugging, logging, profiling, svg, debug-build]
---

A DEBUG build of the mod checks itself the first time a system is loaded: it logs every body's
ladder radii and key transfer costs, re-roots the graph at a few representative bodies and logs
the trees, lays out a synthetic stress tree and the real system at several roots, asserts no
overlaps, and writes SVG and text renderings to disk to open in a browser. Hot paths are timed
with zero-cost-when-off scopes, and every error path logs once per key. Mechanism: a
`_validationDumped` latch in the `[StarMapAfterGui]` handler, compile-time `#if DEBUG` flags in
`DebugConfig`, a `readonly ref struct` timing scope, and a `HashSet<string>` log dedupe.[^dvmap-mod][^dvmap-debugconfig]

## How it works

**One-shot dumps.** After drawing the window, `Mod.Draw` returns early unless
`DebugConfig.ValidationDump` is true, the dump has not run yet, and `Universe.CurrentSystem` is not
null. It then sets the latch and runs, inside a try/catch that logs once and never unwinds into the
render path:

| Dump | Output |
|---|---|
| `DvValidationDump.Run()` | For every `Celestial` in `system.All`: ladder rungs, SOI, key edge delta-v; the controlled vehicle's classified state; spot checks of well-known routes; cache size[^dvmap-dvvalidationdump] |
| `VisualTreeDump.Run()` | Builds the graph and logs the visual tree re-rooted at a planet, a moon, a gas giant and the live ego root, plus each further star system's root and first planet; capped at 600 lines per tree[^dvmap-visualtreedump] |
| `LayoutDump.Run()` | Lays out the synthetic 100+ node tree and the real system at Earth, Luna, a gas giant, the ego root and each other system; logs `OverlapCheck` verdicts and writes SVG and text files (with several systems also each root's whole scene)[^dvmap-layoutdump] |

Files go to `DebugConfig.LayoutDumpDir`, a hardcoded source-tree path the author calls acceptable
for debug-only scaffolding; a failed `Directory.CreateDirectory` downgrades to log-only. The
in-game layout dump calls `LayoutEngine.Run(tree, cfg)` without a text measurer, so it uses the same
character-count estimate as the offline tests. `LayoutDumpFormat` is pure string building (SVG,
fit-preview SVG, text tree), shared with the offline and harness tests.[^dvmap-layoutdumpformat]

**Perf scopes.** `PerfTracker` exists only under `#if DEBUG`. `PerfTracker.Scope` is a `readonly
ref struct` whose constructor stamps `Stopwatch.GetTimestamp()` when `DebugConfig.Performance` is
on (else -1) and whose `Dispose` records the elapsed ticks, so call sites write
`using var perf = new PerfTracker.Scope("DvMap.Draw.Canvas");` inside `#if DEBUG`. Every 5 s the
tracker logs avg/min/max ms and call count per key at debug level and clears. Scopes cover the
canvas and panel draw, vessel follow, visual-tree build, ego and part layout, scene composition and
route recompute in `MapWindow`, and three spots in `InterstellarSection`.[^dvmap-perftracker][^dvmap-mapwindow]

**Once-only logging.** `LogHelper.WarnOnce(key, message)` / `ErrorOnce` log through
`Brutal.Logging.DefaultCategory.Log` only the first time a key is seen; keys carry context
(`"map-build-" + rootId`, `"twindow-" + sibling.Id`), so one bad body logs once without hiding a
different one. `Mod.Unload` calls `LogHelper.Reset()`.[^dvmap-loghelper][^brutal-defaultcategory]
Combined with try/catch at every boundary into the draw path, a failing feature degrades to a
single log line rather than a log flood or a broken ImGui stack.

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (Harmony postfix on `Program.OnDrawUiViewports`), main thread,
  `frame:draw-ui`. The dumps run after `MapWindow.DrawActive` on the first frame at which
  `Universe.CurrentSystem` exists, whether or not the map window is open. Running inside the UI
  draw means the celestial data is loaded and stable; the dumps only read.
- Release builds compile `ValidationDump` and `Performance` to `false`, and the `PerfTracker` type
  and its scopes do not exist, so a release build pays one boolean check per frame.
- `Mod.Unload` resets `_validationDumped`, `PerfTracker` (DEBUG only) and the log dedupe sets, so
  a reload dumps again.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L46-L72) | One-shot dump trigger in the AfterGui handler |
| [DeltaVMap/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/DebugConfig.cs) | `#if DEBUG` flags and dump directory |
| [DeltaVMap/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/PerfTracker.cs) | Ref-struct timing scopes, periodic report |
| [DeltaVMap/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Core/LogHelper.cs) | Keyed once-only warning and error logging |
| [DeltaVMap/Dv/DvValidationDump.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DvValidationDump.cs) | Delta-v engine dump |
| [DeltaVMap/Model/VisualTreeDump.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTreeDump.cs) | Re-rooted tree dump |
| [DeltaVMap/Layout/LayoutDump.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutDump.cs) | Layout dump with SVG/text files |
| [DeltaVMap/Layout/LayoutDumpFormat.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutDumpFormat.cs) | SVG and text rendering of a layout |

## Snippets

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Brutal.Logging.DefaultCategory.Log` (`Info`, `Warning`, `Error`, `Debug`) | [current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs) | The game's log |
| `Universe.CurrentSystem`, `CelestialSystem.All` | (see [Live celestial system graph](./live-celestial-system-graph.md)) | What the dumps iterate |

## Pitfalls and version notes

- `DebugConfig.LayoutDumpDir` is an absolute Windows path on the author's machine; on any other
  machine the directory creation fails and the dump is log-only (the offline tests fall back to
  `./layout-dumps`).
- `using var` works on a `ref struct` with a public `Dispose` through the disposable pattern; the
  struct cannot be captured in lambdas or async methods, which is fine for synchronous scopes.
- Keep dump code behind a compile-time flag rather than a runtime setting, so it costs nothing
  and cannot be enabled by accident in a release.

## See also

- [Map window and menu entry](./map-window-and-menu-entry.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Headless harness tests](./headless-harness-tests.md)
- [Three layout modes](./three-layout-modes.md)
- [Latched dumps from StarMapAfterGui behind a DebugConfig flag](../../../howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-3.md) - how-to article demonstrating this feature
- [Keep dumps and perf scopes out of release builds with a DEBUG flag and a runtime switch](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-5.md) - how-to article demonstrating this feature
- [Time hot code with a DEBUG-only readonly ref struct scope and a periodic report](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-1.md) - how-to article demonstrating this feature

[^dvmap-mod]: DeltaVMap/Mod.cs
[^dvmap-debugconfig]: DeltaVMap/Core/DebugConfig.cs
[^dvmap-perftracker]: DeltaVMap/Core/PerfTracker.cs
[^dvmap-loghelper]: DeltaVMap/Core/LogHelper.cs
[^dvmap-dvvalidationdump]: DeltaVMap/Dv/DvValidationDump.cs
[^dvmap-visualtreedump]: DeltaVMap/Model/VisualTreeDump.cs
[^dvmap-layoutdump]: DeltaVMap/Layout/LayoutDump.cs
[^dvmap-layoutdumpformat]: DeltaVMap/Layout/LayoutDumpFormat.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^brutal-defaultcategory]: Brutal.Logging.DefaultCategory (decompiled, build 2026.10.10.5554)
