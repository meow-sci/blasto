---
type: Mod Feature
title: One Harmony instance applying 30-plus feature patch classes in isolation
description: The supermod's Patcher creates a single Harmony instance and applies every feature library's static patch class through a TryApply wrapper, so one stale game target disables only its own feature, then removes them all on unload
mod: unscience
submod: unscience
category: patching
techniques: [harmony-prefix, harmony-postfix, harmony-transpiler, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded (Patcher.Patch)
  - starmap StarMapUnload (Patcher.Unload)
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers (inline EternalFlamePatches)
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Universe]
tags: [harmony, architecture, error-isolation, unload, supermod]
---

With 30-odd features each patching the game, the supermod needs one place that decides patch
order, owns the Harmony id, survives partial breakage after a game update, and cleans everything up
on unload. `unscience/Patcher.cs` is that place: a static class that creates
`new Harmony("MeowSci.Unscience")` and calls each feature library's `Apply(Harmony)` through a
small isolation wrapper.[^unscience-patcher]

Mechanism in one line: `TryApply(name, action)` / `TryRemove(name, action)` wrap each feature's
static patch class in its own try/catch and log failures, so a missing or renamed game member only
disables the feature that needed it.

## How it works

- `Patch()` runs once from `Mod.OnFullyLoaded` after every sub-mod is initialized and after the host
  has injected the objects some patch classes need (`IFeelSeenTracker`, `CameraSequencePlayer`,
  `PebblesController`, `MenuBarToggle`).[^unscience-mod]
- Order matters only for shared seams: `HotkeyGuard` first, then `HiddenUiFrameHook`, the Thug Life
  render postfix, the menu-bar entry, then `NativeSaveHooks` and `PhysicsFrameHook` before any
  feature that subscribes to them. Shared helpers are idempotent (`PhysicsFrameHook.Apply` returns
  early when applied; the first `PartRenderFilter.Register` installs and later owners only add
  predicates), so features that apply them again (Garry's Torch, the save hooks) are harmless.
- Each lib's patch class resolves its own targets and throws on a mismatch; `TryApply` logs
  `unscience: Failed to apply <feature> patches: <message>` and moves on.
- `Unload()` removes in a separate, explicitly ordered list (scene saves first so no save callback
  runs during teardown), then runs non-Harmony cleanup (`VehiclePaint.Cleanup`,
  `EngineEmissive.Cleanup`) and drops the injected references.
- One patch class lives inline: `EternalFlamePatches` adds a `Priority.First` prefix on
  `Universe.ExecuteNextVehicleSolvers` that calls `EternalFlameSubmod.Instance?.UpdateBeforeVehicleSolvers()` in the lib
  (see [Eternal Flame resource refill](./eternal-flame-resource-refill.md)).

The patch classes applied, grouped by integration seam (full per-target table in the authors'
scope docs):[^scope-architecture][^scope-game-integration]

| Seam | Patch classes |
|---|---|
| Input / UI shell | `HotkeyGuard`, `HiddenUiFrameHook`, `MenuBarPatch` |
| Saves and physics handoff | `NativeSaveHooks`, `PhysicsFrameHook`, `GarrysTorchPatches`, `KittenScalePatches` |
| Solver-dispatch prefixes | `EternalFlamePatches`, `KiwisMarblesPatches` |
| Vehicle physics / destruction | `ColliderScalePatches`, `VisualScalePatches`, `GLoadProtectionPatches`, `TheTickPatches`, `IronManPatches`, `SphinxPatches`, Pebbles (`ClutterController.ApplyPatches`) |
| Camera | `CameraControllerOverridePatches`, `GlassPatches`, `HotPursuitPatches`, `IvaCameraUnlock` |
| Part render data | `BlinkyPatches`, `ShinyPatches` (shared `PartRenderFilter`), `IvaForceRender`, `CapsuleGlassExperiment`, `VehiclePaintPatches`, `EngineEmissivePatches`, `IFeelSeenPatches` |
| Render passes and shaders | `ThugLifeRenderPatches`, `GraffitiPatches`, `PyroPatches`, `FreeFallinPatches`, `KittenVisorPatches` |
| Editor | `EditorScalePatches`, `EditorValueLimitPatches` |
| Animation | `KittenAnimationPatches` |

## Lifecycle and timing

`Patch()` runs in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`): after StarMap's postfix on
`ModLibrary.LoadAll`, so all part templates, shader references and XML-backed data are loaded, but
before any save or scene exists. Patches are therefore in place for the very first frame and the
first save load. `Unload()` runs from `[StarMapUnload]`, which StarMap only dispatches at game exit,
after its own `UnpatchAll()` without an owner id has already removed every Harmony patch in the
process, so the per-class removals find nothing left to remove.[^starmap-core] Each patch class decides its own runtime
timing (see the linked feature pages); the patcher itself only fixes the install order.

## Key code

| Path | Role |
|---|---|
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L41-L115) | `Patch`, `TryApply` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L117-L181) | `Unload`, `TryRemove` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L184-L218) | Inline `EternalFlamePatches` |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L126-L139) | Dependency injection before `Patch()` |

## Snippets

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

```csharp
// unscience/Patcher.cs L184-L197
internal static class EternalFlamePatches
{
    public static void Apply(Harmony harmony)
    {
        var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
        var prefixMethod = AccessTools.Method(typeof(EternalFlamePatches), nameof(BeforeVehicleSolvers));

        if (original == null)
            throw new MissingMethodException(typeof(Universe).FullName, nameof(Universe.ExecuteNextVehicleSolvers));
        if (prefixMethod == null)
            throw new MissingMethodException(typeof(EternalFlamePatches).FullName, nameof(BeforeVehicleSolvers));

        harmony.Patch(original, prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First });
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2140) | Target of the inline Eternal Flame prefix (looked up by name only; still a single overload in 5554)[^ksa-universe] |

All other targets belong to the feature patch classes; see each feature page.

## Pitfalls and version notes

- Isolation hides failures from players: a broken feature only logs to the console. The authors pair
  this with per-feature availability flags (for example Dent Wizard checks `PhysicsFrameHook.IsApplied`
  and shows "Launch unavailable").
- Most patch classes remove exactly their own `(original, method)` pairs, so a standalone host that
  removes one feature leaves other owners' patches on the same targets alone. The exception is
  `CameraControllerOverridePatches.Remove`, which calls `harmony.UnpatchAll(harmony.Id)`: on the shared
  `MeowSci.Unscience` instance that strips every unscience patch, so every `TryRemove` after it in
  `Unload()` is a no-op. Other mods' patches are untouched either way (the id filters them).[^unscience-patcher]
- Partial patch sets: a patch class that patches several targets must roll back itself if a later target
  fails (`PartRenderFilter.Install` does); `TryApply` does not undo anything.
- Several targets are shared with other mods (`Program.DrawProgramMenusHook`,
  `Universe.ExecuteNextVehicleSolvers`); `Priority.First` on the Eternal Flame prefix makes its refill run
  before other prefixes on the same method.

## See also

- [Supermod host](./unscience-supermod-host.md)
- [ISubmod contract](./lib-isubmod-contract.md)
- [Hotkey guard](./lib-hotkey-guard.md), [hidden-HUD hook](./lib-hidden-ui-frame-hook.md), [physics handoff](./lib-physics-frame-hook.md)
- [Native save hooks](./lib-native-save-hooks.md)
- [Mod overview](../overview.md)
- [Idempotent shared installs with a registration API](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-3.md) - how-to article demonstrating this feature
- [Consolidated patcher with TryApply and ordered shared seams](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-2.md) - how-to article demonstrating this feature
- [Unpatch by patch method on a shared Harmony instance](../../../howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-3.md) - how-to article demonstrating this feature

[^unscience-patcher]: unscience/Patcher.cs
[^unscience-mod]: unscience/Mod.cs
[^scope-architecture]: scope/00-architecture-and-abstractions.md (consolidated patch cross-reference)
[^scope-game-integration]: scope/game-integration-surface.md (master integration index)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^starmap-core]: StarMap.Core/StarMapCore.cs
