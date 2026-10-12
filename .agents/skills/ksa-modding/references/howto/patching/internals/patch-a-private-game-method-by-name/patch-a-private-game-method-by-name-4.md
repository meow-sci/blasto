---
type: How-To
title: Attribute argument types that bind by assignability
description: List the parameter types in a HarmonyPatch attribute and let Harmony bind them by assignability, as Compendium's IGameViewport entry binds to the IViewport overload of Astronomical.ShouldDrawLines, and list the exact type so a new overload cannot change the target
category: patching
group: internals
topic: patch-a-private-game-method-by-name
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/orbit-line-visibility-and-color]
techniques: [harmony-postfix, attribute-argument-types]
hooks: [harmony-postfix KSA.Astronomical.ShouldDrawLines]
lifecycle: [startup:all-mods-loaded, frame:pre-render]
game_types: [KSA.Astronomical, KSA.IViewport, KSA.IGameViewport]
tags: [harmony, postfix, overload, attribute, assignability, binder]
---

A `[HarmonyPatch(typeof(T), name, new[] { ... })]` argument array does not have to match the target's parameter types exactly: Harmony resolves it with the reflection default binder, which accepts a type that is
assignable to the declared parameter. Compendium lists `IGameViewport` for a parameter that the game declares as `IViewport`, and it binds. That is convenient and also a trap: a future overload that takes
`IGameViewport` would be chosen instead, silently moving the patch.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Compendium in [Orbit line visibility and color](../../../../mods/compendium/features/orbit-line-visibility-and-color.md).

## Background: how the game does it

`Astronomical.ShouldDrawLines` has three overloads in build 2026.10.10.5554, and none takes `IGameViewport`:

```csharp
// current/decomp/KSA/Astronomical.cs L603
public static bool ShouldDrawLines(IOrbiter orbiter, IViewport inViewport, Orbit? orbit = null)
```

Source: [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L603)[^ksa-astronomical]

```csharp
// current/decomp/KSA/Astronomical.cs L612
public static bool ShouldDrawLines(IParentBody parent, IViewport inViewport, Orbit? orbit = null)
```

Source: [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L612)

```csharp
// current/decomp/KSA/Astronomical.cs L658
public static bool ShouldDrawLines(Astronomical astronomical, IViewport inViewport, Orbit? orbit = null)
```

Source: [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L658)

`IGameViewport` extends `IViewport`:

```csharp
// current/decomp/KSA/IGameViewport.cs L1-L12
using System;

namespace KSA;

public interface IGameViewport : IViewport, IDisposable
{
	ViewportPartPicker PartPicker { get; }

	bool Hovered { get; }

	bool MenuBarInUse { get; }

```

Source: [current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L1-L12)[^ksa-igameviewport]

## Lifecycle and timing

Install: `Patcher.Patch()` calls `PatchAll` with the mod's other patches, at mod load (`startup:all-mods-loaded` in the feature page's terms). Run: the postfix executes wherever the game asks whether an orbit line
should draw: in `frame:pre-render`, where `Celestial.AddLineInstances` builds line instances per viewport, and on a job worker, because `CelestialSystem.SetNearestOrbitPoint` (the orbit under the cursor) calls
`ShouldDrawLines` too. A postfix on this method therefore has to be safe on both threads. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Hook any frame point with Harmony patches on Program and Universe](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Name the type, the method and the exact parameter list, and bind by position.** Compendium's postfix targets the overload whose first parameter is the `Astronomical` itself:

```csharp
// src/Patcher.cs L40-L61
[HarmonyPatch(typeof(Astronomical), nameof(Astronomical.ShouldDrawLines), new[] { typeof(Astronomical), typeof(IGameViewport), typeof(Orbit) })]
[HarmonyPostfix]
public static void ShouldDrawLinesPostfix(Astronomical astronomical, ref bool __result)
{
    if (!Compendium.TryGetOrbitVisibilityOverride(astronomical, out OrbitVisibilityMode mode))
    {
        return;
    }

    switch (mode)
    {
        case OrbitVisibilityMode.Always:
            if (astronomical.OrbitLineMode != OrbitUiMode.Never)
            {
                __result = true;
            }
            break;
        case OrbitVisibilityMode.Off:
            __result = false;
            break;
    }
}
```

Source: [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L40-L61)[^compendium-patcher]

2. **Understand which overload binds.** The attribute lists `(Astronomical, IGameViewport, Orbit)`. The declared overload is `(Astronomical astronomical, IViewport inViewport, Orbit? orbit = null)`. `Orbit?` is the same
   reference-type `Orbit` at runtime. `Type.GetMethod` with the default binder accepts `IGameViewport` for an `IViewport` parameter, so it binds to this overload.

3. **Verify the behaviour once on a throwaway type.** A local test with Lib.Harmony 2.4.2: a type with only a `Draw(IViewport)` overload, patched through an attribute that lists the sub-interface, binds to it. With
   both `Draw(IViewport)` and `Draw(IGameViewport)` present, the attribute that lists `IGameViewport` patches the exact `IGameViewport` one and the `IViewport` one is **not** patched.

4. **List the exact declared type in your own code.** Prefer `typeof(IViewport)` so adding an `IGameViewport` overload later cannot move the patch:

```csharp
// adapted from Compendium Patcher.cs L40-61: the exact declared parameter types
[HarmonyPatch(typeof(Astronomical), nameof(Astronomical.ShouldDrawLines),
    new[] { typeof(Astronomical), typeof(IViewport), typeof(Orbit) })]
```

5. **Take the original's result by `ref bool __result` in a postfix.** The postfix signature is `ShouldDrawLinesPostfix(Astronomical astronomical, ref bool __result)`; parameters matched by name to the original's.
   (The attribute's third element, `Orbit`, names the parameter type; the postfix need not declare it.)

## Complete example

| File | Role |
|---|---|
| [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L40-L61) | Attribute with parameter array, postfix |

## Variations and gotchas

- **Install with `PatchAll`.** Compendium's `Patcher` calls `_harmony.PatchAll(typeof(Patcher).Assembly)`; a type list that matches no overload would make the attribute throw at patch time ([Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md) shows what that costs).
- **The postfix runs on a worker too.** The nearest-orbit job calls `ShouldDrawLines` off the main thread, and Compendium's override table is a plain `Dictionary` read there while the UI writes it ([Patches on planner tasks that run on pooled threads](../../harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md)).
- **Nameable only by assignability for public types.** For a private overload you still need the type list; for a private *type* use `AccessTools.TypeByName`.
- **A future overload.** If a game update adds `ShouldDrawLines(Astronomical, IGameViewport, Orbit?)`, the `IGameViewport` entry would then patch that overload and not the `IViewport` one; the patch would stop firing for callers of the old one, with no log line.
- **Re-check after each update.** [Validating patch targets and detecting game-update breakage](../../safety/validate-patch-targets-and-detect-game-update-breakage/overview.md) describes tests that count how many patches applied.

## See also

- [HarmonyPatch attribute with a string method name, applied by PatchAll](patch-a-private-game-method-by-name-1.md), [Manual AccessTools lookup with a degrade path when the method is gone](patch-a-private-game-method-by-name-2.md), [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](patch-a-private-game-method-by-name-3.md), [TargetMethods with per-name AccessTools lookups and the __args array](patch-a-private-game-method-by-name-5.md), [Reading a private instance field with a triple-underscore parameter](patch-a-private-game-method-by-name-6.md), [Prefer __instance and a public member over a field injector that matches nothing](patch-a-private-game-method-by-name-7.md)
- [Patching private and overloaded game methods by name](overview.md)
- [Orbit line visibility and color](../../../../mods/compendium/features/orbit-line-visibility-and-color.md)

[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-igameviewport]: KSA.IGameViewport (decompiled, build 2026.10.10.5554)
[^compendium-patcher]: Compendium/src/Patcher.cs
