---
type: How-To
title: Lift the vehicle editor's part-scale limits with skipping prefixes and a bounds postfix
description: Widen VehicleEditor.ScaleBoundsFor, replace UpdateSelectedScale with a per-axis drag, and bypass QuantizeScale, reusing the game's private helpers through AccessTools.MethodDelegate
category: vehicles-and-parts
group: vehicles
topic: scale-a-vessel-or-its-parts-at-runtime
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/dont-stifle-me-editor-free-scaling]
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-postfix KSA.VehicleEditor.ScaleBoundsFor
  - harmony-prefix KSA.VehicleEditor.UpdateSelectedScale
  - harmony-prefix KSA.VehicleEditor.QuantizeScale
  - harmony-postfix KSA.VehicleEditor.UpdateScaleGizmo
lifecycle: [editor:on-frame, frame:pre-render, frame:input, frame:draw-ui]
game_types: [KSA.VehicleEditor, KSA.Part, KSA.PartTree, KSA.GenericGizmo]
tags: [editor, scale-limits, gizmo, snapping, per-axis]
---
Outcome: in the vehicle editor any positive scale is accepted, each scale-gizmo arrow (X, Y, Z) stretches only
its own axis, and snapping can be switched off for continuous values. Stock behaviour is uniform scaling between
0.5x and 2x, snapped so the part's largest diameter moves in 0.25 m steps. unscience's Don't Stifle Me undoes all
three; everything is gated by static toggles read on every call, so flipping them needs no re-patching.

Prerequisites: StarMap, Harmony, reflection into private statics. Demonstrated by
[unscience/dont-stifle-me-editor-free-scaling](../../../../mods/unscience/features/dont-stifle-me-editor-free-scaling.md).

## Background: how the game does it

All of this is `KSA.VehicleEditor` ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3975-L4052)[^ksa-vehicleeditor]):

| Member | Stock behaviour |
|---|---|
| `private static (double Min, double Max) ScaleBoundsFor(Part)` | `(1e-6, +inf)` for sub-parts, `(0.5, 2.0)` for top-level parts |
| `private void UpdateSelectedScale(ref readonly double4x4, IViewport)` | projects the cursor delta onto the highlighted gizmo segment, accumulates in `_scaleDragRaw` clamped to the bounds, snaps with `QuantizeScale`, writes `new double3(s, s, s)` to the part and symmetry siblings, then `Selected.Tree.RefreshStaticMass()` |
| `private static double QuantizeScale(Part, double)` | snaps to `0.25 / Template.FindLargestDiameter()` steps clamped to `ScaleBoundsFor` |
| `private static void ForEachPartWithSymmetry(Part, Action<Part>)` | runs an action on the part and its symmetry siblings |

Both the drag accumulator and `QuantizeScale` read `ScaleBoundsFor`, so widening that one method lifts the clamp
everywhere it is applied; the per-axis drag routes through the real `QuantizeScale`, so one prefix there covers
snapping. Mouse release is stock: `OnMouseButton` still refreshes tank contents, resource managers and
`RecomputeAllDerivedData`, so tank volumes catch up after a per-axis drag as after a uniform one.

## Lifecycle and timing

All patches run on the main thread inside the editor; no physics state is involved because `PrepareFrame` sets
simulation speed to 0 while the editor is open.

| Moment | Lifecycle | What the mod does |
|---|---|---|
| mouse press on a gizmo | `frame:input` | nothing; the per-axis session starts lazily |
| drag, every frame | `editor:on-frame` (`VehicleEditor.OnFrame` -> `UpdateSelected` -> `UpdateSelectedScale`) | prefix runs the per-axis step |
| gizmo layout, same frame | `editor:on-frame` (`UpdateScaleGizmo`) | postfix ends the session if released |
| before rendering | `frame:pre-render` (`Editor.OnPreRender` -> `UpdateScaleGizmo`) | postfix runs again; `End` is idempotent |
| toolbox / menu | `frame:draw-ui` | flips the static toggles |

## Steps

1. Resolve the private members by name and get delegates for the helpers you reuse, so snapping and symmetry
   stay the game's own code. Throw if any is missing:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L37-67
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

        harmony.Patch(_scaleBoundsFor,
            postfix: new HarmonyMethod(typeof(EditorScalePatches), nameof(ScaleBoundsForPostfix)));
        harmony.Patch(_updateSelectedScale,
            prefix: new HarmonyMethod(typeof(EditorScalePatches), nameof(UpdateSelectedScalePrefix)));
        harmony.Patch(_updateScaleGizmo,
            postfix: new HarmonyMethod(typeof(EditorScalePatches), nameof(UpdateScaleGizmoPostfix)));
        harmony.Patch(_quantizeScaleMethod,
            prefix: new HarmonyMethod(typeof(EditorScalePatches), nameof(QuantizeScalePrefix)));

        IsApplied = true;
        Console.WriteLine("dont-stifle-me: editor scale patches applied");
    }
```

2. Widen the bounds with a postfix, gated by a toggle:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L89-93
    private static void ScaleBoundsForPostfix(ref (double Min, double Max) __result)
    {
        if (!EditorScaleSettings.ClampRemovalActive) return;
        __result = (MinimumPositiveScale, double.PositiveInfinity);
    }
```

3. Skip the stock snapping when it is off, still clamping to the (widened) bounds:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L103-109
    private static bool QuantizeScalePrefix(Part part, double rawScale, ref double __result)
    {
        if (!EditorScaleSettings.SnapDisabledActive || _scaleBoundsForDelegate == null) return true;
        var (min, max) = _scaleBoundsForDelegate(part);
        __result = Math.Clamp(rawScale, min, max);
        return false;
    }
```

4. Replace the stock drag with a per-axis copy; fall back to stock on any exception. The gizmo segment index is
   the axis (0 X, 1 Y, 2 Z):

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L124-139
    private static bool UpdateSelectedScalePrefix(VehicleEditor __instance, ref double4x4 matrixVehicleAsmb2Ego, IViewport inViewport)
    {
        if (!EditorScaleSettings.PerAxisScalingActive) return true;
        if (_quantizeScale == null || _forEachPartWithSymmetry == null) return true;

        try
        {
            PerAxisScaleDrag.Step(__instance, in matrixVehicleAsmb2Ego, inViewport, _quantizeScale, _forEachPartWithSymmetry);
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"dont-stifle-me: per-axis scale drag failed, falling back to stock: {ex.Message}");
            return true;
        }
    }
```

5. End the drag session when the gizmo is released so the next grab re-seeds from the real scale:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L113-118
    private static void UpdateScaleGizmoPostfix(VehicleEditor __instance)
    {
        // Runs every editor frame; a released gizmo ends the current drag session so the next
        // grab re-seeds its raw value from the part's actual scale.
        if (!__instance.GizmoGrabbed) PerAxisScaleDrag.End();
    }
```

6. The per-axis step repeats the stock math using public editor state (`Selected`, `HighlightedGizmoSegmentIndex`,
   `CursorPositionScreen`, `CursorPositionScreenLastFrame`, `ScaleGizmo.GetSegmentDataByViewport`) but writes only
   `Scale[axis]`, keeps its own raw accumulator per (part, axis) because the stock `_scaleDragRaw` is seeded from
   the largest axis, and uses `selected.Tree?.RefreshStaticMass()` because `Part.Tree` is nullable.
7. Test in the editor: drag one arrow, confirm only that axis changes; switch snapping off and confirm
   continuous values; leave and re-enter flight to confirm the part saved.

## Complete example

- [dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs)[^unscience-editorscalepatches] - patches and delegates
- [dont-stifle-me.lib/PerAxisScaleDrag.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/PerAxisScaleDrag.cs)[^unscience-peraxisscaledrag] - the per-axis drag step

## Variations and gotchas

- **Physics collapses the axes.** `RefreshScale()` takes the largest axis (`ScaleFactors`), and a saved vessel is
  rebuilt through it for parts with a parent [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2426-L2433)[^ksa-part], so connectors, mass and `IRescale`
  modules do not see the per-axis ratio; the drawn shape does.
- **Fall back to stock.** The prefix returns `true` (run the original) on exception or when the delegates are
  missing, so a game update degrades to the stock behaviour instead of breaking the editor.
- **Toggles are static.** `Enabled` and `Snap` take effect on the next editor frame; the edited geometry itself is
  native vehicle data.
- **Parachute diameters have their own limits.** See [Customise parachutes: swap canopy materials and lift the diameter limits](../../modules-and-resources/customize-parachute-materials-and-limits/overview.md).
- **Updates.** Four private members are resolved by name; the gizmo-index-as-axis invariant is an assumption
  about how `UpdateScaleGizmo` lays out its segments.

## See also

- [Overview](overview.md), [Article 3](scale-a-vessel-or-its-parts-at-runtime-3.md)
- [unscience/dont-stifle-me-editor-free-scaling](../../../../mods/unscience/features/dont-stifle-me-editor-free-scaling.md)
- [Customise parachutes: swap canopy materials and lift the diameter limits](../../modules-and-resources/customize-parachute-materials-and-limits/overview.md)

[^unscience-peraxisscaledrag]: unscience: dont-stifle-me.lib/PerAxisScaleDrag.cs
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^unscience-editorscalepatches]: unscience: dont-stifle-me.lib/EditorScalePatches.cs
