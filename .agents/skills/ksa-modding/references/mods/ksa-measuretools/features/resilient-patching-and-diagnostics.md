---
type: Mod Feature
title: Per-patch Harmony application, drift checks, once-only logging and debug perf scopes
description: The entry class applies each Harmony patch class separately, type-checks private reflection targets at load, logs each per-frame exception type once, and in DEBUG builds times hot paths with Stopwatch scopes
mod: ksa-measuretools
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix, reflection]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:input, event:starmap-unload]
game_types: [KSA.VersionInfo, KSA.Program, KSA.BurnContextMenu]
tags: [getting-started, harmony, logging, diagnostics, version-check, reflection, perf, unload]
---

MeasureTools patches three game methods and reads three private fields. Its entry class is built so
that a game update which breaks one of them degrades one feature instead of the whole mod, and so a
per-frame failure produces one log line with a stack instead of a log flood. The scaffolding is
small and copyable into any StarMap mod.[^mt-mod]

## How it works

### Entry class

`[StarMapMod] public sealed class Mod` with four members:[^mt-mod]

| Member | Attribute | Work |
|---|---|---|
| `OnFullyLoaded()` | `[StarMapAllModsLoaded]` | Compare `VersionInfo.Current.VersionString` with `TestedGameVersion = "v2026.10.10.5554"` and warn on mismatch; `GameReflection.LogDriftIfUnavailable()`; `MeasureColors.Load()`; create `new Harmony("com.maxi.measuretools")` and apply `Patch_MenuBar`, `Patch_MouseButton`, `Patch_LoadSave` one by one |
| `ApplyPatch(Type, string)` | | `_harmony.CreateClassProcessor(patchClass).Patch()` in a `try/catch` that logs a failed patch once and continues |
| `Draw(double dt)` | `[StarMapAfterGui]` | `Prune`, window, overlay inside one `try/catch` keyed by exception type |
| `Unload()` | `[StarMapUnload]` | `UnpatchAll(_harmony.Id)` (a no-op by then, see below), save colors, then reset every static (state, window instance, overlay cache, mesh cache, mouse-patch flags, debug flags, log dedupe, perf counters) |

`TestedGameVersion` is also read by the SpaceDock publishing workflow with a `grep` on
`private const string TestedGameVersion`, so bumping it in code updates the compatible build on the
listing.[^mt-spacedock]

### Patch-level isolation

Applying all patches with `harmony.PatchAll()` stops at the first target that no longer exists.
`CreateClassProcessor(type).Patch()` per class means a renamed `DrawProgramMenusHook` loses the menu
but keeps the mouse intercept and the save-load reset. Two of the three patch bodies have their own
`try/catch`: the mouse prefix returns `true` (pass the click through) on any exception, and the
save-load postfix swallows its exception because a postfix that throws propagates into the game's
load. The menu postfix has none; it only makes ImGui menu calls and opens or closes the window.

### Reflection drift checks

`GameReflection` caches `AccessTools.Field` handles for `Program._heldMouseButtons`,
`Program._inputViewport` and `BurnContextMenu._rightClickValid` once, and checks field types, not
just presence (`int`, assignable from `IGameViewport`, `bool`). `CanReleaseMouseLatch` requires both
latch fields, because releasing half the latch is worse than not consuming the click. At load,
`LogDriftIfUnavailable` warns once per missing field and states the user-visible consequence ("a
cancelled right-click can leave the stock burn menu armed").[^mt-gamereflection]

### Logging

`LogHelper.WarnOnce(key, msg)` and `ErrorOnce(key, msg)` keep two separate `HashSet<string>` key
spaces (so a warning never silences a later error with the same key) and write through
`Brutal.Logging.DefaultCategory.Log`. Per-frame catch blocks use keys like
`"aftergui-" + ex.GetType().Name`, so each distinct exception type logs one stack per session. The
sets are main-thread only and cleared on unload.[^mt-loghelper]

### Debug-only instrumentation

`DebugConfig.Measure` and `DebugConfig.Performance` default to true in DEBUG builds and false in
Release, and are mutable fields rather than constants so a "Debug logging" header in the window (DEBUG
only) can toggle them at runtime.[^mt-debugconfig][^mt-window] `PerfTracker.Scope` is a `readonly ref
struct` used as `using var _ = new PerfTracker.Scope("MapPicker.PickPart");` under `#if DEBUG`. It
records `Stopwatch` ticks per name and every 5 seconds logs average, min, max and call count per scope
in first-seen order, then resets. Scopes wrap the hot paths: `Mod.Draw`, `MeasureState.Prune`, the
overlay draw and preview, the part, circle, body and orbit pickers and their part sub-scans (feature scan, raycast span, vertex and edge tiers), and the measurement list.[^mt-perftracker]

## Lifecycle and timing

| Hook | Where StarMap fires it | Notes |
|---|---|---|
| `[StarMapAllModsLoaded]` | Harmony postfix on `ModLibrary.LoadAll`[^starmap-modlibrarypatches] | No universe, no ImGui frame; only file IO and patching happen here |
| `[StarMapAfterGui]` | Harmony postfix on `Program.OnDrawUiViewports`[^starmap-programpatcher] | Main thread, inside the ImGui frame |
| `[StarMapUnload]` | `ModLoader.Dispose` at StarMap shutdown, after the game's `Main` has returned and after `StarMapCore.DeInit` called `Harmony.UnpatchAll()` with no id[^starmap-modloader][^starmap-core] | Must not touch ImGui; every `Reset()` in the mod is written to avoid it. Every Harmony patch in the process is already gone, so the mod's own `UnpatchAll(_harmony.Id)` finds nothing to remove |

`AllModsLoaded` is early enough for all three patches: their targets (`DrawProgramMenusHook`,
`OnMouseButton`, `DeserializeSave`) only run once the game loop or a save load starts.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs) | Entry class, patch application, guarded draw, unload |
| [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs) | Typed reflection handles and drift warning |
| [MeasureTools/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/LogHelper.cs) | Once-only warnings and errors |
| [MeasureTools/Core/PerfTracker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/PerfTracker.cs) | DEBUG perf scopes |
| [MeasureTools/Core/DebugConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/DebugConfig.cs) | Runtime-mutable debug flags |

## Snippets

```csharp
// MeasureTools/Mod.cs L19-L55
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    string gameVersion = VersionInfo.Current.VersionString;
    DefaultCategory.Log.Info($"[MeasureTools] Game version: {gameVersion}");
    if (gameVersion != TestedGameVersion)
        DefaultCategory.Log.Warning(
            $"[MeasureTools] Tested against {TestedGameVersion}, current is {gameVersion}. " +
            "Some features may not work correctly.");

    GameReflection.LogDriftIfUnavailable();
    MeasureColors.Load();

    _harmony = new Harmony("com.maxi.measuretools");
    // Apply each patch on its own so a future game change to one target does not
    // stop the other from being patched.
    ApplyPatch(typeof(Patch_MenuBar), "Measure menu");
    ApplyPatch(typeof(Patch_MouseButton), "mouse intercept");
    ApplyPatch(typeof(Patch_LoadSave), "save load reset");

    DefaultCategory.Log.Info("[MeasureTools] Loaded.");
}

private static void ApplyPatch(Type patchClass, string description)
{
    try
    {
        _harmony!.CreateClassProcessor(patchClass).Patch();
        // ...
    }
    catch (Exception ex)
    {
        // A missing hook should not unload the mod or block the other patch.
        LogHelper.ErrorOnce("patch-" + patchClass.Name, $"[MeasureTools] Failed to apply {description} patch: {ex}");
    }
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `VersionInfo.Current.VersionString` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs) | Tested-build warning |

## Pitfalls and version notes

- `PatchAll` throws at the first failing patch class, so every class after it in iteration order stays
  unpatched while the ones before it remain applied; per-class processors in their own `try/catch`
  isolate each target.
- Check reflected field types as well as names; a field that changed type would otherwise throw from
  inside an input callback.
- The version check only warns; nothing is disabled on a mismatch.
- Static state is only cleared if the mod clears it; this mod resets every static in
  `[StarMapUnload]` (the source comments mention mod reloads) and keeps those resets free of ImGui
  calls. With StarMap 4dc60f8 the only unload is the one at process exit, after the game loop and its
  ImGui context are gone, so the resets matter only for a future in-process reload.

## See also

- [Overview](../overview.md)
- [Click capture](./click-capture-mouse-prefix.md)
- [Shared deploy targets and SpaceDock publishing](./mod-deploy-targets-and-release.md)
- [Compare VersionInfo.Current with a tested-version constant at AllModsLoaded](../../../howto/getting-started/lifecycle/check-the-game-version/check-the-game-version-1.md) - how-to article demonstrating this feature
- [Clean up with StarMapUnload and ProcessExit, and reset static state](../../../howto/getting-started/lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-1.md) - how-to article demonstrating this feature
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature
- [Log with HashSet-keyed WarnOnce and ErrorOnce that include the exception type](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-7.md) - how-to article demonstrating this feature
- [CreateClassProcessor per patch class, required versus optional](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md) - how-to article demonstrating this feature
- [Time hot code with a DEBUG-only readonly ref struct scope and a periodic report](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-1.md) - how-to article demonstrating this feature

[^mt-mod]: MeasureTools/Mod.cs
[^mt-gamereflection]: MeasureTools/Core/GameReflection.cs
[^mt-loghelper]: MeasureTools/Core/LogHelper.cs
[^mt-perftracker]: MeasureTools/Core/PerfTracker.cs
[^mt-debugconfig]: MeasureTools/Core/DebugConfig.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-spacedock]: .github/workflows/spacedock-publish.yml
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
