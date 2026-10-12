---
type: How-To
title: Idempotent shared installs with a registration API
description: Install a seam once (a part render filter, a PrepareFrame handoff hook) and let any number of features register against it, so applying the same seam twice is harmless and the last owner removes it
category: patching
group: harmony
topic: coexist-with-other-mods-patching-the-same-method
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/lib-part-render-filter
  - mods/unscience/features/unscience-consolidated-patcher
  - mods/unscience/features/lib-physics-frame-hook
  - mods/unscience/features/lib-hotkey-guard
techniques: [harmony-prefix, harmony-postfix, harmony-transpiler, inter-mod-api]
hooks:
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-postfix KSA.PartTreeRenderData.Compose
  - harmony-transpiler KSA.Program.PrepareFrame
  - harmony-prefix KSA.GameSettings.OnKeyAll
lifecycle: [startup:all-mods-loaded, frame:render, frame:dispatch-solvers, frame:input]
game_types: [KSA.PartTreeRenderData, KSA.Program, KSA.Universe, KSA.GameSettings]
tags: [harmony, idempotent, registration, shared-seam, coexistence]
---

Thirty features in one mod, or several mods that each need the same seam, should not each install their own patch
on the same method: two independent compactions of a draw list would shift each other's ranges, and two
transpilers on `PrepareFrame` would each consume the same anchor. unscience solves it with a small static
library per seam. The first `Register` installs the patches; later callers only add a predicate or a
callback; `Apply` returns early when already applied; the last `Unregister` removes the patches.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated in [Hide individual part meshes by compacting the per-frame instance lists](../../../../mods/unscience/features/lib-part-render-filter.md),
[Physics-safe vessel mutation at the PrepareFrame solver handoff](../../../../mods/unscience/features/lib-physics-frame-hook.md), [Block game hotkeys while an ImGui text field has focus](../../../../mods/unscience/features/lib-hotkey-guard.md) and
[One Harmony instance applying 30-plus feature patch classes in isolation](../../../../mods/unscience/features/unscience-consolidated-patcher.md).

## Background: how the game does it

KSA 5482 moved part rendering into a per-tree cache, `PartTreeRenderData`. Each frame `Compose`, `ComposeDynamic`
and `ComposeGlass` append one contiguous range per model batch to that model's per-viewport instance list, which is
uploaded later in the frame. A filter that wants to hide parts has to remove entries from the range the game just
appended, and it can only do that safely if exactly one piece of code does the compaction. `Compose` has the
signature `(ref readonly double4x4, bool, IViewport, int)`, and the three compose methods share it
([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1264)[^ksa-parttreerenderdata]).

For the physics handoff, `Program.PrepareFrame` calls `Universe.GetJobSimStep` exactly once, between the apply and
dispatch calls ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221)[^ksa-program]). That single call is the seam for `PhysicsFrameHook`.

## Lifecycle and timing

- Install: unscience's `Patcher.Patch()` runs from `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) with each lib's
  `Apply` wrapped in `TryApply`; shared helpers are idempotent, so features that apply them again are harmless.
- `PartRenderFilter` prefixes and postfixes run in `frame:render`, on the main thread, once per part model instance per
  viewport per frame, so predicates must be cheap.
- `PhysicsFrameHook` runs at the `PrepareFrame` handoff in `frame:dispatch-solvers`, where state is settled and
  nothing is snapshotted ([Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md), [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
- `HotkeyGuard` runs in `frame:input` as a prefix on the first function the key callback consults.

## Steps

1. **Keep owners and installed patches in static state.** The filter holds one dictionary of predicates per
   owner and a list of the patches it installed. `IsInstalled` is simply "the list is not empty":

```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L28-L35
private static readonly Dictionary<string, Func<PartModel, bool>> StaticModelOwners = new(StringComparer.Ordinal);
private static readonly List<(MethodBase Original, MethodInfo Patch)> Installed = new();
private static Func<Part, bool>[] _filters = Array.Empty<Func<Part, bool>>();
private static Func<PartModel, bool>[] _staticModelFilters = Array.Empty<Func<PartModel, bool>>();

/// <summary>True while the shared compaction patches are attached.</summary>
public static bool IsInstalled => Installed.Count > 0;
public static bool IsOperational => IsInstalled && !_faulted;
```

Source: [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs#L28-L35)[^unscience-partrenderfilter]

2. **Install on first registration, only add on later ones.** `Register` stores the owner's predicate and refreshes a
   snapshot array that the hot path reads:

```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L37-L48
/// <summary>
/// Adds or replaces an owner's predicate. <paramref name="shouldHide"/> receives each full part
/// (<c>Part.FullPart</c>) once per model instance per viewport per frame on the main thread.
/// The first registration installs the shared patches with <paramref name="harmony"/>; it
/// throws when the game no longer exposes the render-data members this filter relies on.
/// </summary>
public static void Register(Harmony harmony, string owner, Func<Part, bool> shouldHide)
{
    if (!IsInstalled) Install(harmony);
    Owners[owner] = shouldHide;
    _filters = Owners.Values.ToArray();
}
```

Source: [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs#L37-L48)

3. **Make install all-or-nothing.** If any compose method is missing, the partial install is rolled back and the
   exception propagates, so the filter never hides only some model kinds:

```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L88-L103
private static void Install(Harmony harmony)
{
    ResolveBatchAccessors();
    try
    {
        Patch(harmony, nameof(PartTreeRenderData.Compose), nameof(ComposePrefix), nameof(ComposePostfix));
        Patch(harmony, nameof(PartTreeRenderData.ComposeDynamic), nameof(ComposeDynamicPrefix), nameof(ComposeDynamicPostfix));
        Patch(harmony, nameof(PartTreeRenderData.ComposeGlass), nameof(ComposeGlassPrefix), nameof(ComposeGlassPostfix));
    }
    catch
    {
        Uninstall(harmony); // Never leave a partial filter that hides some model kinds only.
        throw;
    }
    Console.WriteLine("ksa-abstractions: PartRenderFilter patches applied");
}
```

Source: [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs#L88-L103)

4. **Remove when the last owner leaves.** `Unregister` removes the owner's entries and uninstalls the patches only
   when no owner remains:

```csharp
// ksa-abstractions.lib/PartRenderFilter.cs L62-L71
/// <summary>Removes an owner's predicate; the last owner removes the shared patches.</summary>
public static void Unregister(Harmony harmony, string owner)
{
    bool removed = Owners.Remove(owner);
    removed |= StaticModelOwners.Remove(owner);
    if (!removed) return;
    _filters = Owners.Values.ToArray();
    _staticModelFilters = StaticModelOwners.Values.ToArray();
    if (Owners.Count == 0 && StaticModelOwners.Count == 0) Uninstall(harmony);
}
```

Source: [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs#L62-L71)

5. **Make `Apply` idempotent.** The PrepareFrame hook is applied by the scene-save hooks and by every feature that
   needs the handoff; the second and later calls return immediately. The transpiler validates seven `Universe` calls
   by exact count and order and throws if the shape is wrong, so dependants can check `IsApplied` and disable
   themselves instead of falling back to an unsafe path:

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L50-L56
public static void Apply(Harmony harmony)
{
    if (IsApplied) return;
    harmony.Patch(PrepareFrameMethod(), transpiler: new HarmonyMethod(TranspilerMethod()));
    IsApplied = true;
    Console.WriteLine("unscience physics: PrepareFrame handoff hook applied");
}
```

Source: [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs#L50-L56)[^unscience-physicsframehook]

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L73-L104
internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
{
    var codes = instructions.ToList();
    // Patch the caller: a prefix on a small solver method can miss a call already inlined
    // into PrepareFrame. Replacing this one call preserves its stack, labels and blocks.
    // Reject missing, duplicate or reordered seams instead of falling back to UI teleports.
    string[] seams =
    {
        nameof(Universe.ApplyOrbitSolvers),
        nameof(Universe.ApplyVehicleSolvers),
        nameof(Universe.ApplyClothSolvers),
        nameof(Universe.GetJobSimStep),
        nameof(Universe.ExecuteNextClothSolvers),
        nameof(Universe.ExecuteNextVehicleSolvers),
        nameof(Universe.ExecuteNextOrbitSolvers)
    };
    int previousIndex = -1;
    int stepIndex = -1;
    foreach (string seam in seams)
    {
        var method = AccessTools.Method(typeof(Universe), seam)
            ?? throw new MissingMethodException(typeof(Universe).FullName, seam);
        int index = codes.FindIndex(code => code.Calls(method));
        if (index <= previousIndex || codes.Count(code => code.Calls(method)) != 1)
            throw new InvalidOperationException($"unscience physics: unexpected PrepareFrame solver sequence at {seam}");
        previousIndex = index;
        if (seam == nameof(Universe.GetJobSimStep)) stepIndex = index;
    }

    codes[stepIndex].operand = AccessTools.Method(typeof(PhysicsFrameHook), nameof(GetStepAndDispatch));
    return codes;
}
```

Source: [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs#L73-L104)

6. **Fan out to callbacks from the one patched call.** `GetStepAndDispatch` calls the real `GetJobSimStep`, drains
   the queued mutations present at entry and invokes each `BeforePhysics` subscriber in its own `try`/`catch`:

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L106-L140
private static SimStep GetStepAndDispatch(double dtPlayer)
{
    // The previous ImGui frame has finished and worker results have committed. A
    // console load requested after ImGui.Image must never dispose its textures inline.
    // Replay the whole transaction here, before computing any time from the old world.
    _orbitReadersJoined = false;
    Action? worldChange = _pendingWorldChange;
    _pendingWorldChange = null;
    if (worldChange != null)
    {
        JoinOrbitReaders();
        IsReplayingWorldChange = true;
        try { worldChange(); }
        catch (Exception ex) { Console.WriteLine($"unscience saves: deferred world change failed: {ex}"); }
        finally { IsReplayingWorldChange = false; }
    }
    SimStep step = Universe.GetJobSimStep(dtPlayer);
    if (Universe.CurrentSystem == null) { Pending.Clear(); return step; }

    // Drain only the actions present at the handoff; actions queued by callbacks wait a frame.
    int count = Pending.Count;
    if (count > 0) JoinOrbitReaders();
    for (int i = 0; i < count; i++)
    {
        try { Pending.Dequeue()(); }
        catch (Exception ex) { Console.WriteLine($"unscience physics: queued mutation failed: {ex}"); }
    }
    if (BeforePhysics != null)
        foreach (Action<double, UniverseTime> callback in BeforePhysics.GetInvocationList())
        {
            try { callback(dtPlayer, step.PreviousTime); }
            catch (Exception ex) { Console.WriteLine($"unscience physics: callback failed: {ex}"); }
        }
    return step;
}
```

Source: [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs#L106-L140)

7. **Order the consolidated patcher by shared seam.** The supermod applies `HotkeyGuard` first, then the hidden-HUD
   hook, the menu-bar entry, then `NativeSaveHooks` and `PhysicsFrameHook` before any feature that subscribes to
   them, each in `TryApply`:

```csharp
// unscience/Patcher.cs L53-L70
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

Source: [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L53-L70)[^unscience-patcher]

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/PartRenderFilter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartRenderFilter.cs) | Registration API and shared compaction patches |
| [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs) | Idempotent `PrepareFrame` handoff with `Enqueue` and `BeforePhysics` |
| [ksa-abstractions.lib/HotkeyGuard.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HotkeyGuard.cs)[^unscience-hotkeyguard] | One prefix on `GameSettings.OnKeyAll` applied once by the host |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L41-L115) | Ordered, isolated application |

## Variations and gotchas

- **One shared patch set is required.** Two independent compactions would each shift the ranges the other measured.
- **Fail open.** Any exception in the filter sets a fault flag, logs once and makes it inert (everything renders),
  rather than breaking the render loop.
- **Partial installs must clean up.** `TryApply` does not undo anything; a class that patches several targets rolls
  back itself, as `PartRenderFilter.Install` does.
- **Across mods.** This pattern works inside one assembly. Separate mods that want the same seam need a published
  API that one of them owns, or must choose different seams (approach 2).
- **Drain only what was queued at entry.** Actions queued by callbacks wait one frame, which avoids infinite loops.

## See also

- [Priority.First prefixes and Priority.Last finalizers around shared seams](coexist-with-other-mods-patching-the-same-method-1.md), [Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md), [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md), [One worker-side postfix merging every writer](coexist-with-other-mods-patching-the-same-method-5.md)
- [Coexisting with other mods that patch the same method](overview.md)
- [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) explains the settled window that `PhysicsFrameHook` exposes; the topic mutate-vehicles-at-a-physics-safe-point builds a task on it

[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^unscience-partrenderfilter]: unscience/ksa-abstractions.lib/PartRenderFilter.cs
[^unscience-physicsframehook]: unscience/ksa-abstractions.lib/PhysicsFrameHook.cs
[^unscience-patcher]: unscience/Patcher.cs
[^unscience-hotkeyguard]: unscience/ksa-abstractions.lib/HotkeyGuard.cs
