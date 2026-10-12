---
type: How-To
title: Delegates over private editor helpers reused in a replacement drag
description: A per-axis scale drag replaces VehicleEditor.UpdateSelectedScale but keeps the game's own private QuantizeScale, ScaleBoundsFor and ForEachPartWithSymmetry by binding them as delegates, including the null check on bind and the delegate shapes for a tuple return and a nested Action
category: patching
group: internals
topic: call-private-game-members-through-reflection-and-delegates
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/dont-stifle-me-editor-free-scaling]
techniques: [method-delegate, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.VehicleEditor.UpdateSelectedScale
  - harmony-prefix KSA.VehicleEditor.QuantizeScale
  - harmony-postfix KSA.VehicleEditor.ScaleBoundsFor
lifecycle: [startup:all-mods-loaded, editor:on-frame]
game_types: [KSA.VehicleEditor, KSA.Part]
tags: [reflection, accesstools, methoddelegate, editor, scaling, instance-method, static-method]
---

Don't Stifle Me lifts the stock vehicle editor's 0.5x to 2.0x, uniform-only part scaling. A skipping prefix on `VehicleEditor.UpdateSelectedScale` runs a per-axis copy of the stock drag math. That copy does not
reimplement the game's snapping and symmetry walk: it calls the game's own private static `QuantizeScale` and `ForEachPartWithSymmetry` (and, for the unclamped bounds, the patched `ScaleBoundsFor`) through
delegates bound once at apply time. This article is about the delegate bindings: their shapes, the null checks on bind and why the replacement reuses the game's code.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in [Don't Stifle Me editor free scaling (unclamped, per-axis, unsnapped)](../../../../mods/unscience/features/dont-stifle-me-editor-free-scaling.md).

## Background: how the game does it

Three private static helpers sit beside the instance method that the mod replaces. `ScaleBoundsFor` returns a named tuple, `ForEachPartWithSymmetry` takes an `Action<Part>`, and `QuantizeScale`
snaps in 0.25 m diameter steps:

```csharp
// current/decomp/KSA/VehicleEditor.cs L4011-L4050
private static (double Min, double Max) ScaleBoundsFor(Part part)
{
	return part.IsSubPart ? (Min: 1E-06, Max: double.PositiveInfinity) : (Min: 0.5, Max: 2.0);
}

private static void ForEachPartWithSymmetry(Part part, Action<Part> action)
{
	action(part);
	Part.SymmetryLinkData? symmetryLink = part.SymmetryLink;
	if (!symmetryLink.HasValue)
	{
		return;
	}
	Part.SymmetryLinkData valueOrDefault = symmetryLink.GetValueOrDefault();
	if (1 == 0)
	{
		return;
	}
	foreach (Part.SymmetryData symmetry in valueOrDefault.Symmetries)
	{
		foreach (Part part2 in symmetry.Parts)
		{
			if (part2 != part)
			{
				action(part2);
			}
		}
	}
}

private static double QuantizeScale(Part part, double rawScale)
{
	double num = part.Template.FindLargestDiameter();
	if (num <= 0.0)
	{
		return rawScale;
	}
	double num2 = 0.25 / num;
	(double, double) tuple = ScaleBoundsFor(part);
	double num3 = Math.Clamp(Math.Round((rawScale - 1.0) / num2), Math.Ceiling((tuple.Item1 - 1.0) / num2), Math.Floor((tuple.Item2 - 1.0) / num2));
```

Source: [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L4011-L4050)[^ksa-vehicleeditor]

## Lifecycle and timing

The patches are applied at `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`). All four run on the main thread inside the vehicle editor: a drag runs from `Program.OnFrame` via `OnFrameEditor`
to `VehicleEditor.OnFrame`, `UpdateSelected` and `UpdateSelectedScale` (`editor:on-frame`), and no physics state is involved because `Program.PrepareFrame` sets the simulation speed to 0 while an editor is open.
Toggles flipped in the UI phase apply from the next frame, with no re-patching. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve every method with `AccessTools.Method` and throw if any is missing.** The delegates are bound only after all the lookups succeed:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L37-L55
public static void Apply(Harmony harmony)
{
    if (IsApplied) return;

    _scaleBoundsFor = AccessTools.Method(typeof(VehicleEditor), ScaleBoundsForName)
        ?? throw new MissingMethodException(nameof(VehicleEditor), ScaleBoundsForName);
    _updateSelectedScale = AccessTools.Method(typeof(VehicleEditor), UpdateSelectedScaleName)
        ?? throw new MissingMethodException(nameof(VehicleEditor), UpdateSelectedScaleName);
    _updateScaleGizmo = AccessTools.Method(typeof(VehicleEditor), UpdateScaleGizmoName)
        ?? throw new MissingMethodException(nameof(VehicleEditor), UpdateScaleGizmoName);

    _quantizeScaleMethod = AccessTools.Method(typeof(VehicleEditor), QuantizeScaleName)
        ?? throw new MissingMethodException(nameof(VehicleEditor), QuantizeScaleName);
    var forEach = AccessTools.Method(typeof(VehicleEditor), ForEachPartWithSymmetryName)
        ?? throw new MissingMethodException(nameof(VehicleEditor), ForEachPartWithSymmetryName);
    _quantizeScale = AccessTools.MethodDelegate<Func<Part, double, double>>(_quantizeScaleMethod);
    _scaleBoundsForDelegate = AccessTools.MethodDelegate<Func<Part, (double Min, double Max)>>(_scaleBoundsFor);
    _forEachPartWithSymmetry = AccessTools.MethodDelegate<Action<Part, Action<Part>>>(forEach);

```

Source: [dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs#L37-L55)[^unscience-editorscalepatches]

2. **Pick the delegate type from the method shape.** A static method with `(Part, double)` returning `double` is `Func<Part, double, double>`; a static method returning a *named tuple* `(double Min, double Max)` is
   `Func<Part, (double Min, double Max)>`; a static `void` taking an `Action<Part>` is `Action<Part, Action<Part>>`. A static method binds to an unbound delegate with `MethodDelegate<TDelegate>(method)`:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L29-L34, L52-L54
// Private static game helpers reused so per-axis drags keep the stock 0.25 m diameter snapping
// and symmetry propagation.
private static Func<Part, double, double>? _quantizeScale;
private static Func<Part, (double Min, double Max)>? _scaleBoundsForDelegate;
private static Action<Part, Action<Part>>? _forEachPartWithSymmetry;

// ...
    _quantizeScale = AccessTools.MethodDelegate<Func<Part, double, double>>(_quantizeScaleMethod);
    _scaleBoundsForDelegate = AccessTools.MethodDelegate<Func<Part, (double Min, double Max)>>(_scaleBoundsFor);
    _forEachPartWithSymmetry = AccessTools.MethodDelegate<Action<Part, Action<Part>>>(forEach);
```

Source: [dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs#L29-L54)

3. **Patch the siblings that hold the clamp.** A postfix on `ScaleBoundsFor` widens the bounds for every reader; a prefix on `QuantizeScale` returns the raw clamped value when snapping is off. The prefix reaches
   the bounds through the *delegate to the patched method*, which is the point: it gets the widened result:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L56-L64, L89-L109
    harmony.Patch(_scaleBoundsFor,
        postfix: new HarmonyMethod(typeof(EditorScalePatches), nameof(ScaleBoundsForPostfix)));
    harmony.Patch(_updateSelectedScale,
        prefix: new HarmonyMethod(typeof(EditorScalePatches), nameof(UpdateSelectedScalePrefix)));
    harmony.Patch(_updateScaleGizmo,
        postfix: new HarmonyMethod(typeof(EditorScalePatches), nameof(UpdateScaleGizmoPostfix)));
    harmony.Patch(_quantizeScaleMethod,
        prefix: new HarmonyMethod(typeof(EditorScalePatches), nameof(QuantizeScalePrefix)));

// ...
private static void ScaleBoundsForPostfix(ref (double Min, double Max) __result)
{
    if (!EditorScaleSettings.ClampRemovalActive) return;
    __result = (MinimumPositiveScale, double.PositiveInfinity);
}

// ---- snapping ----

/// <summary>
/// <c>VehicleEditor.QuantizeScale(Part, double)</c> snaps a raw drag value to 0.25 m diameter
/// increments. With snapping disabled, pass the raw value through (still clamped to the
/// current, possibly widened, bounds). Both the stock uniform drag and the per-axis drag route
/// through this method, so one prefix covers both.
/// </summary>
private static bool QuantizeScalePrefix(Part part, double rawScale, ref double __result)
{
    if (!EditorScaleSettings.SnapDisabledActive || _scaleBoundsForDelegate == null) return true;
    var (min, max) = _scaleBoundsForDelegate(part);
    __result = Math.Clamp(rawScale, min, max);
    return false;
}
```

Source: [dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs#L56-L109)

4. **Reuse them in the replacement.** The per-axis `Step` receives the delegates as parameters, calls `quantizeScale` for snapping and `forEachPartWithSymmetry` to apply the new scale to symmetry siblings:

```csharp
// dont-stifle-me.lib/PerAxisScaleDrag.cs L62-L76
    _raw += magnitude * sign;
    double snapped = quantizeScale(selected, _raw);
    if (snapped.Equals(selected.Scale[axis])) return;

    double3 newScale = selected.Scale;
    SetAxis(ref newScale, axis, snapped);

    forEachPartWithSymmetry(selected, part =>
    {
        part.Scale = newScale;
        part.RefreshScaleAndReposition();
    });
    // Part.Tree is nullable since KSA 5482; editor selections always belong to a tree.
    selected.Tree?.RefreshStaticMass();
}
```

Source: [dont-stifle-me.lib/PerAxisScaleDrag.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/PerAxisScaleDrag.cs#L62-L76)[^unscience-peraxisscaledrag]

5. **Null-check on bind and on use.** If any lookup returned null the apply throws before any patch is installed; at the call sites the delegates are checked (`_scaleBoundsForDelegate == null` returns `true`,
   letting stock run).

## Complete example

| File | Role |
|---|---|
| [dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs) | Lookups, delegate binding and the four patches |
| [dont-stifle-me.lib/PerAxisScaleDrag.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/PerAxisScaleDrag.cs) | The replacement drag that reuses the delegates |

## Variations and gotchas

- **Why delegates and not copies.** Copying `QuantizeScale` would freeze its behaviour at one build; calling it keeps snapping and clamping stock when the game changes it.
- **These three helpers are static.** `QuantizeScale`, `ScaleBoundsFor` and `ForEachPartWithSymmetry` are `private static` in `VehicleEditor`, so the delegates are unbound. For a private *instance* method
  `AccessTools.MethodDelegate<T>(method, instance)` binds a target instance ([AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md) notes the `instance` and `virtualCall` arguments); the instance method this mod replaces, `UpdateSelectedScale`, is reached
  through a prefix and `__instance` instead of a delegate.
- **Patched targets.** A delegate to a method you also patch calls the patched body (here that is wanted, for `ScaleBoundsFor`).
- **Connectors and mass follow the largest axis** (game limitation, `Part.RefreshScale`); the authors say a replacement of that method would be needed to fix it.
- **Name-string targets.** All five names are looked up by string; a rename throws at `Apply`, which the supermod's `TryApply` logs.

## See also

- [AccessTools static field refs bound once, replay stock's writes](call-private-game-members-through-reflection-and-delegates-1.md), [Plain GetField on internal registries and a generic holder for instance fields](call-private-game-members-through-reflection-and-delegates-2.md), [Typed FieldRefAccess, boxed private enum members and by-ref lookups](call-private-game-members-through-reflection-and-delegates-3.md), [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](call-private-game-members-through-reflection-and-delegates-4.md), [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](call-private-game-members-through-reflection-and-delegates-6.md), [Copied injector in a consumer mod with the same accessor](call-private-game-members-through-reflection-and-delegates-7.md)
- [Reading, writing and calling private game members](overview.md)
- [Don't Stifle Me editor free scaling (unclamped, per-axis, unsnapped)](../../../../mods/unscience/features/dont-stifle-me-editor-free-scaling.md)
- [Skipping Harmony prefixes](../../harmony/skip-the-original-method-with-a-prefix/overview.md), [Consolidated patcher with TryApply and ordered shared seams](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-2.md)
- Related task: scale-a-vessel-or-its-parts-at-runtime

[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^unscience-editorscalepatches]: unscience/dont-stifle-me.lib/EditorScalePatches.cs
[^unscience-peraxisscaledrag]: unscience/dont-stifle-me.lib/PerAxisScaleDrag.cs
