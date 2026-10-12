---
type: How-To
title: Consolidated patcher with TryApply and ordered shared seams
description: One Harmony id for thirty-odd features, each library's static Apply and Remove wrapped in TryApply and TryRemove, order only where seams are shared, idempotent shared helpers and removal by patch method instead of by id
category: patching
group: safety
topic: isolate-patch-failures-and-fault-proof-patch-bodies
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/unscience-consolidated-patcher, mods/unscience/features/lib-isubmod-contract]
techniques: [harmony-prefix, harmony-unpatch-by-method]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, event:starmap-unload]
game_types: [KSA.Universe, KSA.Program, KSA.GameSettings, HarmonyLib.Harmony]
tags: [harmony, tryapply, isolation, supermod, shared-seam, unpatch]
---

unscience is about thirty independent features shipped as one mod. Each feature lives in a `<name>.lib` library with a
static `<Feature>Patches.Apply(Harmony)` and `Remove(Harmony)` pair, and is hosted twice: by a thin development mod and by the
`unscience` supermod. The supermod's `Patcher` owns one Harmony instance, applies each library through a small isolation
wrapper, and orders only the shared seams. Symmetric `Apply` and `Remove` make a feature cleanly removable and hostable by a
supermod.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in
[One Harmony instance applying 30-plus feature patch classes in isolation](../../../../mods/unscience/features/unscience-consolidated-patcher.md) and [ISubmod contract for features that run standalone or inside a supermod](../../../../mods/unscience/features/lib-isubmod-contract.md).

## Background: what the design guards against

Thirty features patching dozens of game methods will, after a game update, have a few stale targets. A single
`PatchAll()` would stop at the first and leave later features unpatched; per-feature owners as in
[One Harmony owner per feature with TryApply, validated handles and runtime flags](../refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-2.md) give every feature its own id. unscience chooses a single
shared id instead, so the library contract can be host-agnostic: any host passes in the `Harmony` instance, and a
standalone host uses its own id while the supermod runs every feature on `Harmony("MeowSci.Unscience")`. Isolation is by
`try`/`catch`, not by owner id.

## Lifecycle and timing

`Patcher.Patch()` runs from `Mod.OnFullyLoaded` (`[StarMapAllModsLoaded]`, `startup:all-mods-loaded`): after StarMap's
postfix on `ModLibrary.LoadAll`, so part templates, shader references and XML data are loaded, but before any save or scene
exists. Patches are therefore in place for the very first frame and the first save load. `Unload()` runs from
`[StarMapUnload]` at game exit, after StarMap's own id-less `UnpatchAll()` has already removed every patch, so the
per-class removals find nothing left to remove. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the host-agnostic contract.** Features implement `ISubmod` for UI and expose a static patch class that takes the
   caller's `Harmony`. `Initialize` runs from `[StarMapAllModsLoaded]`; `Update` each frame; `Dispose` at unload:

```csharp
// ksa-abstractions.lib/ISubmod.cs L7-L37
public interface ISubmod
{
    /// <summary>Display name shown in headers and menus.</summary>
    string Name { get; }

    /// <summary>Short description shown as a tooltip on the header in the host window.</summary>
    string Tooltip { get; }

    /// <summary>Called once during initialization to set up state and lib instances.</summary>
    void Initialize();

    /// <summary>Called every frame for pre-UI computation (sampling, ticking, etc.).</summary>
    void Update(double dt);

    /// <summary>
    /// Renders ImGui content. Caller is responsible for Begin/End window framing.
    /// Do NOT call ImGui.Begin/End for the main content area. Additional popup/child windows are fine.
    /// </summary>
    void RenderContent();

    /// <summary>
    /// Renders any floating/secondary ImGui windows (e.g. stand-alone editor windows).
    /// Called every frame regardless of whether the submod's section is collapsed, so
    /// windows opened by this submod remain consistent with their own visibility state.
    /// Default implementation is a no-op.
    /// </summary>
    void RenderFloatingWindows() { }

    /// <summary>Called during unload to clean up resources.</summary>
    void Dispose();
}
```

Source: [ksa-abstractions.lib/ISubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ISubmod.cs#L7-L37)[^unscience-isubmod]

2. **Write each patch class as an `Apply` and `Remove` pair that resolves its own targets and throws on a mismatch.**
   Glass is the smallest example; `Remove` unpatches exactly the methods it patched, not an id:

```csharp
// glass.lib/GlassPatches.cs L18-L47
public static void Apply(Harmony harmony)
{
    _fovRadiansField = AccessTools.Field(typeof(Camera), "_fovRadians");

    _changeFovPrefix = typeof(GlassPatches).GetMethod(nameof(ChangeFieldOfViewPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
    _updateProjectionPrefix = typeof(GlassPatches).GetMethod(nameof(UpdateProjectionPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;

    _cameraChangeFieldOfView = AccessTools.Method(typeof(Camera), "ChangeFieldOfView");
    _cameraUpdateProjection = AccessTools.Method(typeof(Camera), "UpdateProjection");

    harmony.Patch(_cameraChangeFieldOfView, prefix: new HarmonyMethod(_changeFovPrefix));
    harmony.Patch(_cameraUpdateProjection, prefix: new HarmonyMethod(_updateProjectionPrefix));

    Console.WriteLine("glass.lib: patches applied");
}

public static void Remove(Harmony harmony)
{
    if (_cameraChangeFieldOfView != null && _changeFovPrefix != null)
        harmony.Unpatch(_cameraChangeFieldOfView, _changeFovPrefix);
    if (_cameraUpdateProjection != null && _updateProjectionPrefix != null)
        harmony.Unpatch(_cameraUpdateProjection, _updateProjectionPrefix);

    _cameraChangeFieldOfView = null;
    _cameraUpdateProjection = null;
    _changeFovPrefix = null;
    _updateProjectionPrefix = null;

    Console.WriteLine("glass.lib: patches removed");
}
```

Source: [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs#L18-L47)[^unscience-glasspatches]

3. **Wrap every `Apply` in `TryApply`.** A missing or renamed game member only disables the feature that needed it; the
   failure logs `unscience: Failed to apply <feature> patches: <message>` and the next feature is applied:

```csharp
// unscience/Patcher.cs L105-L115
private static void TryApply(string feature, Action apply)
{
    try
    {
        apply();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"unscience: Failed to apply {feature} patches: {ex.Message}");
    }
}
```

Source: [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L105-L115)[^unscience-patcher]

4. **Order only the shared seams.** `HotkeyGuard` first, then the hidden-HUD frame hook, then `NativeSaveHooks` and
   `PhysicsFrameHook` before any feature that subscribes to them. Shared helpers are idempotent (see
   [Idempotent shared installs with a registration API](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-3.md)), so features applying them again are harmless:

```csharp
// unscience/Patcher.cs L41-L70
public static void Patch()
{
    try
    {
        _harmony = new Harmony("MeowSci.Unscience");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"unscience: Error creating Harmony instance: {ex.Message}");
        return;
    }

    // Each feature is applied in isolation: a single feature failing to patch (e.g. a
    // stale reflection/field target after a game update) logs and is skipped instead of
    // aborting every feature registered after it in the chain.
    TryApply("hotkey-guard", () => HotkeyGuard.Patch(_harmony!));
    // Replays Mod.UpdateSubmods while the HUD is hidden (F2), since StarMap's
    // BeforeGui/AfterGui targets are skipped by the game in that state. Callbacks are
    // registered in Mod.OnFullyLoaded before Patch() runs.
    TryApply("hidden-ui-frame-hook", () => HiddenUiFrameHook.Patch(_harmony!));
    TryApply("thug-life", () => ThugLifeRenderPatches.Apply(_harmony!));
    TryApply("menu-bar", () =>
    {
        MenuBarPatch.ToggleWindow = MenuBarToggle;
        MenuBarPatch.Apply(_harmony!);
    });
    TryApply("scene saves", () => NativeSaveHooks.Apply(_harmony!));
    TryApply("dent-wizard physics handoff", () => PhysicsFrameHook.Apply(_harmony!));
    TryApply("blinky", () => BlinkyPatches.Apply(_harmony!));
    TryApply("its-so-shiny", () => ShinyPatches.Apply(_harmony!));
```

Source: [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L41-L70)

5. **Remove in an explicit order, each in `TryRemove`.** Scene saves first so no save callback runs during teardown, then
   per-feature removals, then non-Harmony cleanup and dropped references:

```csharp
// unscience/Patcher.cs L117-L130
public static void Unload()
{
    try
    {
        if (_harmony != null)
        {
            TryRemove("scene saves", () => NativeSaveHooks.Remove(_harmony!));
            TryRemove("hotkey-guard", () => HotkeyGuard.Unpatch(_harmony!));
            TryRemove("hidden-ui-frame-hook", () => HiddenUiFrameHook.Unpatch(_harmony!));
            TryRemove("menu-bar", () => MenuBarPatch.Remove(_harmony!));
            TryRemove("blinky", () => BlinkyPatches.Remove(_harmony!));
            TryRemove("its-so-shiny", () => ShinyPatches.Remove(_harmony!));
            TryRemove("camera-controller-override", () => CameraControllerOverridePatches.Remove(_harmony!));
            TryRemove("eternal-flame", () => EternalFlamePatches.Remove(_harmony!));
```

Source: [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L117-L130)

6. **Prefer removal by patch method on a shared instance.** Most patch classes remove exactly their own
   `(original, method)` pairs, so a standalone host that removes one feature leaves other owners' patches on the same
   targets alone. The exception is `CameraControllerOverridePatches.Remove`, which calls `harmony.UnpatchAll(harmony.Id)`: on the
   shared `MeowSci.Unscience` instance that strips every unscience patch, so every `TryRemove` after it is a no-op. Other mods'
   patches are untouched either way, because the id filters them ([Unpatch by patch method on a shared Harmony instance](../remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-3.md)).

7. **Pair isolation with availability flags.** Isolation hides failures from players, so the authors pair it with per-feature
   availability: Dent Wizard checks `PhysicsFrameHook.IsApplied` and shows "Launch unavailable".

## Complete example

| File | Role |
|---|---|
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs) | `Patch`, `TryApply`, `Unload`, `TryRemove`, inline `EternalFlamePatches` |
| [ksa-abstractions.lib/ISubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ISubmod.cs) | The host-agnostic contract |
| [glass.lib/GlassPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/glass.lib/GlassPatches.cs) | A minimal `Apply` and `Remove` pair |

## Variations and gotchas

- **`TryApply` does not undo anything.** A patch class that patches several targets must roll back itself if a later target
  fails (`PartRenderFilter.Install` does).
- **Priority only where it matters.** Eternal Flame's `Priority.First` prefix on
  `Universe.ExecuteNextVehicleSolvers` runs before other prefixes on the same method
  ([Priority.First prefixes and Priority.Last finalizers around shared seams](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-1.md)).
- **Failures only log.** A broken feature only writes to the console; give each feature a visible availability state.
- **Standalone hosts never ship.** Only `unscience` is distributed; the other hosts compile to keep both integration paths
  honest.

## See also

- [CreateClassProcessor per patch class, required versus optional](isolate-patch-failures-and-fault-proof-patch-bodies-1.md), [Static patch classes that look up the target, log once and stand down](isolate-patch-failures-and-fault-proof-patch-bodies-3.md), [Reflection-only mod with parameter-name and field-type pre-checks](isolate-patch-failures-and-fault-proof-patch-bodies-4.md), [Fault counter that pulls every patch after a limit](isolate-patch-failures-and-fault-proof-patch-bodies-5.md)
- [Isolating patch failures and keeping patch bodies from throwing](overview.md)
- [One Harmony instance applying 30-plus feature patch classes in isolation](../../../../mods/unscience/features/unscience-consolidated-patcher.md), [ISubmod contract for features that run standalone or inside a supermod](../../../../mods/unscience/features/lib-isubmod-contract.md)
- [Refusing a patch and rolling back when the game changes](../refuse-a-patch-and-roll-back-when-the-game-changes/overview.md)

[^unscience-isubmod]: unscience/ksa-abstractions.lib/ISubmod.cs
[^unscience-glasspatches]: unscience/glass.lib/GlassPatches.cs
[^unscience-patcher]: unscience/Patcher.cs
