---
type: How-To
title: CreateClassProcessor per patch class, required versus optional
description: Apply each Harmony patch class on its own inside try/catch so one drifted target costs one feature, classify patches by what the player loses, log each exception type once, and warn about reflection drift at load
category: patching
group: safety
topic: isolate-patch-failures-and-fault-proof-patch-bodies
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/purrtty/features/failure-isolated-harmony-patching
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
techniques: [harmony-prefix, harmony-class-processor, reflection]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, event:starmap-unload, frame:draw-ui]
game_types: [KSA.Program, KSA.GameSettings, HarmonyLib.Harmony]
tags: [harmony, createclassprocessor, isolation, once-only-logging, safety, patchall]
---

`harmony.PatchAll()` applies every patch class in one call, and it stops at the first one that fails: classes after it
are never applied, classes before it stay applied. A game update that renames one private method can therefore
silently disable half your mod. purrTTY and KSA-MeasureTools apply each patch class separately instead, so a drift in
one target costs one feature. This article shows the structure, how to classify patches, and the once-only logging that
keeps a per-frame failure from flooding the log.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated in [Failure-isolated Harmony patching with reload-safe statics](../../../../mods/purrtty/features/failure-isolated-harmony-patching.md) and
[Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md).

## Background: what Harmony does

Local tests against Lib.Harmony 2.4.2 (the version StarMap references) used four patch classes on four target methods:
a prefix class `B` (declared first), a class `A` with a throwing transpiler, a prefix class `C`, and a class `D` that names a
method that does not exist. `PatchAll(assembly)` produced:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
PatchAll with a throwing transpiler class among others:
  PatchAll threw HarmonyException / base InvalidOperationException
  T1.Run original
  prefix on T2
  T2.Run original
  T3.Run original
```

The class declared before the failing one (`T2`) is patched; the class after it (`T3`) is not. Applying each class on its
own, in the order A, B, C, D, gave:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
per-class CreateClassProcessor, each in try/catch:
  FAILED PatchA_Bad: HarmonyException / anchor not found
  patched PatchB_Ok
  patched PatchC_Ok
  FAILED PatchD_NoSuchMethod: HarmonyException / Undefined target method for patch method static System.Void PatchD_NoSuchMethod::Prefix()
```

Classes are applied in declaration order and `PatchAll` stops at the first failure. `CreateClassProcessor(type).Patch()`
throws `HarmonyException` for a missing target, so the catch must be per class, not around a loop.

## Lifecycle and timing

Patches are applied at `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`, a postfix on `ModLibrary.LoadAll`): game
types and all mods exist and the first frame has not run, and the input, menu and render targets are first reached by the
frame loop, which starts afterwards. Per-frame work in `[StarMapAfterGui]` (`frame:draw-ui`) is wrapped separately.
`[StarMapUnload]` runs at process exit after StarMap's id-less `UnpatchAll()` has removed everything, so the unload
path is mostly defensive (see [Removing Harmony patches safely at runtime and at exit](../remove-your-patches-safely-at-runtime-and-at-exit/overview.md)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Put each target in its own class.** purrTTY keeps `Patch01` (the `OnKey` gate), `Patch03_HotkeyGuard` (`OnKeyAll`) and
   `Patch04_MouseBindingGate` (the private `DispatchKeyEvent`) separate, so a drift in the private method can only disable
   `Patch04`, never the keyboard gate:

```csharp
// purrTTY.GameMod/Patcher.cs L13-L36
internal static class Patcher
{
  private static Harmony? m_harmony;

  // Each Harmony patch is applied independently with its own try/catch so one
  // drifted target (a KSA/Brutal API rename, an IL reshuffle) can never abort
  // the whole mod — the old PatchAll path threw on the first failure and took
  // InitializeTerminal() down with it. Patches are classified by impact:
  //   required — input gating / typing guard; a failure degrades core function.
  //   optional — menu fallback + console capture; a failure leaves the mod usable.
  private static readonly Type[] s_requiredPatches =
  {
    typeof(Patch01),
    typeof(Patch03_HotkeyGuard),
    typeof(Patch04_MouseBindingGate),
  };

  private static readonly Type[] s_optionalPatches =
  {
    typeof(Patch02),
    typeof(ConsoleWindowPrintPatch),
    // In-world quad draw injection (no-op unless the in-world feature is active).
    typeof(RenderTranslucencyPassPatch),
  };
```

Source: [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L13-L36)[^purrtty-patcher]

2. **Apply each class in its own `try`/`catch` and say what is lost.** The log message states the severity and the
   consequence for the player:

```csharp
// purrTTY.GameMod/Patcher.cs L38-L71
public static void patch()
{
  // Recreate the instance if a previous unload() nulled it: a StarMap reload
  // without an ALC unload reuses these static types, and `null?.Patch…` would
  // make patch() a silent no-op (no input gating, no menu, no capture).
  var harmony = m_harmony ??= new Harmony("purrTTY");

  foreach (var type in s_requiredPatches)
  {
    ApplyPatch(harmony, type, required: true);
  }

  foreach (var type in s_optionalPatches)
  {
    ApplyPatch(harmony, type, required: false);
  }
}

private static void ApplyPatch(Harmony harmony, Type type, bool required)
{
  try
  {
    harmony.CreateClassProcessor(type).Patch();
  }
  catch (Exception ex)
  {
    string severity = required ? "REQUIRED" : "optional";
    string consequence = required
      ? "terminal input gating may be degraded; the terminal will still initialize"
      : "the mod remains usable without it";
    ModLog.Log.Error(
      $"purrTTY: {severity} Harmony patch '{type.Name}' failed to apply ({consequence}): {ex.Message}");
  }
}
```

Source: [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L38-L71)

3. **Recreate the Harmony instance lazily and reset statics on unload.** `patch()` uses `m_harmony ??= new Harmony("purrTTY")`
   so a later call cannot silently patch through a null; `unload()` nulls the instance after `UnpatchAll("purrTTY")` and resets the
   held-key and swallowed-button sets. StarMap 4dc60f8 never reloads a mod in-process, so both are defensive against a
   future loader:

```csharp
// purrTTY.GameMod/Patcher.cs L73-L85
public static void unload()
{
  m_harmony?.UnpatchAll("purrTTY");
  m_harmony = null;

  // Statics survive a StarMap reload without an ALC unload (same reason
  // m_harmony is recreated lazily above), so cached environment probes must
  // be re-evaluated by the next patch() — the mod set may have changed — and
  // the held-key model must not carry stale entries across a reload.
  Patch02.Reset();
  Patch01.Reset();
  Patch04_MouseBindingGate.Reset();
}
```

Source: [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L73-L85)

4. **Same shape in KSA-MeasureTools.** One Harmony id, three classes applied one by one with a description, and a failure that
   logs once and moves on:

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
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Applied {description} patch ({patchClass.Name}).");
    }
    catch (Exception ex)
    {
        // A missing hook should not unload the mod or block the other patch.
        LogHelper.ErrorOnce("patch-" + patchClass.Name, $"[MeasureTools] Failed to apply {description} patch: {ex}");
    }
}
```

Source: [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L19-L55)[^ksa-measuretools-mod]

5. **Check reflected members at load and state the consequence.** `GameReflection` caches `AccessTools.Field` handles and
   checks field *types*, not only presence. `LogDriftIfUnavailable` warns once per missing field in terms of what the player
   loses ("a cancelled right-click can leave the stock burn menu armed"):

```csharp
// MeasureTools/Core/GameReflection.cs L26-L31, L54-L66
// Both fields or neither: half-releasing the latch is worse than not consuming.
// Types checked so drift reads as drift instead of throwing mid-click.
public static bool CanReleaseMouseLatch =>
    Program_heldMouseButtons?.FieldType == typeof(int)
    && Program_inputViewport != null
    && Program_inputViewport.FieldType.IsAssignableFrom(typeof(IGameViewport));
// ...
public static void LogDriftIfUnavailable()
{
    if (!CanReleaseMouseLatch)
        LogHelper.WarnOnce("reflect-mouse-latch",
            "[MeasureTools] Program._heldMouseButtons / _inputViewport not found or changed shape, "
            + "game version may have changed. Right-click cancel and pause will not suppress the "
            + "stock context menus.");
    if (BurnContextMenu_rightClickValid?.FieldType != typeof(bool))
        LogHelper.WarnOnce("reflect-burn-latch",
            "[MeasureTools] BurnContextMenu._rightClickValid not found or changed shape, game "
            + "version may have changed. A cancelled right-click can leave the stock burn menu "
            + "armed until the next right-click.");
}
```

Source: [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs#L26-L66)[^ksa-measuretools-gamereflection]

6. **Log each exception type once per session.** A per-frame `catch` that logs every exception floods the log. Keep two
   separate key spaces (so a warning never silences a later error with the same key) and key by exception type:

```csharp
// MeasureTools/Core/LogHelper.cs L5-L29
// Log deduplication. Separate keyspaces so a WarnOnce(k) never silences
// a later ErrorOnce(k). Not thread-safe, call from main thread only.
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

Source: [MeasureTools/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/LogHelper.cs#L5-L29)[^ksa-measuretools-loghelper]

   The per-frame draw uses it as `LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, ...)`
   ([MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L59-L78)). Wrap each patch body that can throw in its own `try`/`catch`; the mouse prefix
   returns `true` on any exception, and a postfix on the save load swallows its exception because a postfix that throws
   propagates into the game's load ([Mouse prefix that pairs presses with releases and repairs private latches](../../harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-4.md)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L13-L85) | Required and optional arrays, `ApplyPatch`, `unload` |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs) | Entry class with per-class isolation and per-frame guard |
| [MeasureTools/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/GameReflection.cs) | Typed handles and drift warnings |
| [MeasureTools/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Core/LogHelper.cs) | Once-only logging |

## Variations and gotchas

- **Classify honestly.** If a "required" patch fails, the log line should say what breaks.
- **`PatchAll` also reapplies other classes.** If two components share an assembly, use `CreateClassProcessor` so one component's
  install does not apply the other's classes a second time (OhMyStars does this for its star patches).
- **The version check only warns.** MeasureTools compares `VersionInfo.Current.VersionString` with `TestedGameVersion` and
  warns on mismatch; nothing is disabled.
- **`LogHelper` here is main-thread only.** A worker-thread patch needs a locked version
  ([Patches on planner tasks that run on pooled threads](../../harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md)).
- **Parameter-name binding.** Harmony binds injected arguments by name and also by position; purrTTY's `RenderTranslucencyPass`
  postfix declares parameters in a different order from the game's and works for that reason.

## See also

- [Consolidated patcher with TryApply and ordered shared seams](isolate-patch-failures-and-fault-proof-patch-bodies-2.md), [Static patch classes that look up the target, log once and stand down](isolate-patch-failures-and-fault-proof-patch-bodies-3.md), [Reflection-only mod with parameter-name and field-type pre-checks](isolate-patch-failures-and-fault-proof-patch-bodies-4.md), [Fault counter that pulls every patch after a limit](isolate-patch-failures-and-fault-proof-patch-bodies-5.md)
- [Isolating patch failures and keeping patch bodies from throwing](overview.md)
- [Failure-isolated Harmony patching with reload-safe statics](../../../../mods/purrtty/features/failure-isolated-harmony-patching.md), [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md)
- [Refusing a patch and rolling back when the game changes](../refuse-a-patch-and-roll-back-when-the-game-changes/overview.md)
- Related topics: contain-exceptions-in-starmap-hooks, write-a-per-mod-log-file

[^purrtty-patcher]: purrtty/purrTTY.GameMod/Patcher.cs
[^ksa-measuretools-mod]: KSA-MeasureTools/MeasureTools/Mod.cs
[^ksa-measuretools-gamereflection]: KSA-MeasureTools/MeasureTools/Core/GameReflection.cs
[^ksa-measuretools-loghelper]: KSA-MeasureTools/MeasureTools/Core/LogHelper.cs
