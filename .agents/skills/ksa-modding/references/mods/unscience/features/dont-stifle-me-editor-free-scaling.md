---
type: Mod Feature
title: Don't Stifle Me editor free scaling (unclamped, per-axis, unsnapped)
description: Don't Stifle Me widens VehicleEditor.ScaleBoundsFor with a postfix, replaces the uniform UpdateSelectedScale drag with a per-axis one that reuses the game's private QuantizeScale and ForEachPartWithSymmetry through delegates, and can bypass the 0.25 m snapping
mod: unscience
submod: dont-stifle-me
category: vehicles-and-parts
techniques: [harmony-prefix, harmony-postfix, reflection, method-delegate, imgui-menu]
hooks:
  - harmony-postfix KSA.VehicleEditor.ScaleBoundsFor
  - harmony-prefix KSA.VehicleEditor.UpdateSelectedScale
  - harmony-prefix KSA.VehicleEditor.QuantizeScale
  - harmony-postfix KSA.VehicleEditor.UpdateScaleGizmo
  - reflection KSA.VehicleEditor.QuantizeScale (AccessTools.MethodDelegate)
  - reflection KSA.VehicleEditor.ScaleBoundsFor (AccessTools.MethodDelegate)
  - reflection KSA.VehicleEditor.ForEachPartWithSymmetry (AccessTools.MethodDelegate)
  - harmony-postfix KSA.Program.DrawProgramMenusHook (standalone host only)
lifecycle: [editor:on-frame, editor:pre-render, frame:input, frame:draw-ui]
game_types:
  - KSA.VehicleEditor
  - KSA.Part
  - KSA.PartTree
  - KSA.ScaleFactors
  - KSA.GenericGizmo
  - KSA.Camera
  - KSA.IViewport
  - KSA.Program
tags: [vehicle-editor, part-scaling, gizmo, snapping, symmetry, harmony, method-delegate, menu-bar]
---

Since rev 5348 the stock vehicle editor scales top-level parts uniformly and only between 0.5x and
2.0x, snapped so the part's largest diameter moves in 0.25 m steps. Don't Stifle Me undoes all three:
any positive scale is accepted, each scale-gizmo arrow (X, Y, Z) stretches only its own axis, and
snapping can be switched off for continuous values. Everything is gated by two static toggles read on
every call, so flipping **Enabled** or **Snap scaling** takes effect on the next editor frame with no
re-patching.[^dsm-readme][^dsm-scale-settings]

Mechanism in one line: a postfix on the private static `VehicleEditor.ScaleBoundsFor` returns
`(1e-6, +inf)`, a skipping prefix on `VehicleEditor.UpdateSelectedScale` runs a per-axis copy of the
stock drag math, and that copy calls the game's own private `QuantizeScale` and
`ForEachPartWithSymmetry` through `AccessTools.MethodDelegate` so snapping and symmetry stay stock.

## How it works

Four Harmony patches and three delegates, all resolved by name in
[dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs#L37-L67):

| Target (`KSA.VehicleEditor`) | Patch | Effect while `Enabled` |
|---|---|---|
| `private static (double Min, double Max) ScaleBoundsFor(Part)` | postfix | `__result = (1e-6, +inf)` instead of the stock `(0.5, 2.0)` for top-level parts (sub-parts already get `(1e-6, +inf)` from the game) |
| `private void UpdateSelectedScale(ref readonly double4x4, IViewport)` | prefix, returns `false` | Runs `PerAxisScaleDrag.Step` instead of the stock body, which writes `new double3(s, s, s)`; falls back to stock on any exception |
| `private static double QuantizeScale(Part, double rawScale)` | prefix, returns `false` when `Snap` is off | Returns `rawScale` clamped to the (widened) bounds instead of the 0.25 m diameter step |
| `public void UpdateScaleGizmo(ref readonly double4x4, doubleQuat, IViewport, double)` | postfix | When `GizmoGrabbed` is false, ends the per-axis drag session so the next grab re-seeds from the real scale |

**Stock behavior being replaced** ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3975-L4052)).
`UpdateSelectedScale` projects the near-plane cursor delta onto the highlighted gizmo segment's axis,
scales it by the part's depth, accumulates it in `_scaleDragRaw` clamped to `ScaleBoundsFor`, snaps
it with `QuantizeScale`, and, if the snapped value differs from `new ScaleFactors(Selected.Scale).Scale`,
writes the same value to all three axes of the part and its symmetry siblings, then calls
`PartTree.RefreshStaticMass`. `QuantizeScale` turns the 0.25 m diameter increment into a scale step
of `0.25 / Template.FindLargestDiameter()` and clamps the step index to `ScaleBoundsFor`.[^ksa-vehicleeditor]

**Per-axis drag**
([dont-stifle-me.lib/PerAxisScaleDrag.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/PerAxisScaleDrag.cs#L25-L76)).
`Step` repeats the stock math line for line using public editor state (`Selected`,
`HighlightedGizmoSegmentIndex`, `CursorPositionScreen`, `CursorPositionScreenLastFrame`,
`ScaleGizmo.GetSegmentDataByViewport(viewport)[axis].Body2Cce`, `Part.PositionEgo`). The gizmo segment
index is the axis: segment 0 is X, 1 is Y, 2 is Z, an invariant of how `UpdateScaleGizmo` lays out the
three segments of the `ScaleGizmo` built at L1179.[^ksa-vehicleeditor] Differences from stock:

- It keeps its own raw accumulator `_raw` per (part, axis) session, seeded from `selected.Scale[axis]`,
  because the stock `_scaleDragRaw` is seeded from the largest axis on mouse press.
- It writes only `Scale[axis]` and leaves the other two components alone.
- It calls `quantizeScale` and `forEachPartWithSymmetry`, delegates bound to the game's private static
  methods, so the 0.25 m snapping and the symmetry walk are the game's own code rather than copies.
- It uses `selected.Tree?.RefreshStaticMass()` because `Part.Tree` became nullable in 5482; stock still
  dereferences it unguarded.[^scope-part-editor]

**Snapping off.** The `QuantizeScale` prefix clamps the raw value with a third delegate,
`_scaleBoundsForDelegate`, which goes through the patched `ScaleBoundsFor`, so it clamps to
`(1e-6, +inf)` rather than to 0.5-2.0.

**One patch per concern.** Because both the drag accumulator and `QuantizeScale` read
`ScaleBoundsFor`, widening that one method lifts the clamp everywhere it is applied; because the
per-axis drag routes through the real `QuantizeScale`, one prefix there covers snapping.

**Release path stays stock.** `VehicleEditor.OnMouseButton` is not patched. On release of the scale
gizmo it still walks the symmetry set calling `Part.RefreshTankContents`, recreates resource managers if
tank volumes changed, and calls `PartTree.RecomputeAllDerivedData` and
`PerformanceSequences.SetDirty`, so tank volumes and derived data catch up after a per-axis drag exactly
as after a uniform one.[^ksa-vehicleeditor]

**Controls.** In unscience the controls are the "Don't Stifle Me - Editor Limits" section of the
toolbox ([dont-stifle-me.lib/DontStifleMeSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeSubmod.cs)),
with a red notice when `EditorScalePatches.IsApplied` is false. The standalone development host
draws the same three checkboxes as a top-level "Don't Stifle Me" menu
([dont-stifle-me.lib/DontStifleMeMenu.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeMenu.cs))
from a postfix on `Program.DrawProgramMenusHook`
([dont-stifle-me/MenuBarPatch.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me/MenuBarPatch.cs));
that hook and its timing are documented in [the menu-bar entry feature](./unscience-menu-bar-entry.md).
The third checkbox, "jpl said no clamps", belongs to
[the parachute diameter unclamp](./dont-stifle-me-parachute-diameter-unclamp.md).

**Hosting.** The standalone [dont-stifle-me/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me/Patcher.cs)
applies `HotkeyGuard`, `EditorScalePatches`, `EditorValueLimitPatches` and `MenuBarPatch` from
`[StarMapAllModsLoaded]`. The shipped supermod applies only the two patch groups, each in its own
`TryApply` so a stale target skips this feature without blocking others
([unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L92-L93)).[^unscience-patcher]

**Saves.** The two toggles travel in the `dont-stifle-me` scene record together with the parachute
switch (see [the parachute feature](./dont-stifle-me-parachute-diameter-unclamp.md)); the edited
geometry itself is native vehicle data. A drag in progress is not saved.[^dsm-saves]

## Lifecycle and timing

All four patches run on the main thread inside the vehicle editor; no physics state is involved
because `Program.PrepareFrame` sets the simulation speed to 0 while `Editor` is non-null.[^ksa-program]

| Moment | Game call chain (build 5554) | Lifecycle | What the mod does |
|---|---|---|---|
| Mouse press on a gizmo | `Glfw.PollEvents` (`Program.cs` L2179) -> `Program.OnMouseButton` -> `Editor.OnMouseButton` (L2028) -> `VehicleEditor.OnMouseButton` sets `GizmoGrabbed` and seeds stock `_scaleDragRaw` (L3491-L3499) | `frame:input` | Nothing; the per-axis session starts lazily on the first `Step` |
| Drag, every frame | `Program.OnFrame` -> `OnFrameEditor` (L2262, body L2567-L2578) -> `VehicleEditor.OnFrame` (L2429) -> `UpdateSelected` (L2497) -> `UpdateSelectedScale` (L3799) | `editor:on-frame` | Prefix runs `Step`, which calls the `QuantizeScale` and `ScaleBoundsFor` delegates and writes `Part.Scale` |
| Gizmo layout, same frame | `VehicleEditor.OnFrame` -> `UpdateScaleGizmo` (L2500) | `editor:on-frame` | Postfix ends the session if the gizmo was released |
| Before rendering | `Program.OnPreRender` -> `Editor.OnPreRender` (L2437) -> `UpdateScaleGizmo` (`VehicleEditor.cs` L5483) | `editor:pre-render` | Postfix runs a second time; harmless because `End` is idempotent |
| Mouse release | Same input path, release branch (L3500-L3524) | `frame:input` | Stock tank and derived-data refresh |
| Toolbox or standalone menu | `[StarMapAfterGui]` for the toolbox; `Program.DrawMenuBar` -> `DrawProgramMenusHook` (L4005) for the standalone menu | `frame:draw-ui` | Flips `EditorScaleSettings.Enabled` / `Snap` |

`OnFrameEditor` runs after `PrepareImGui` and before `OnFrameViewports`, so the scale written in
`Step` is in place before the editor computes this frame's camera and before `Editor.OnPreRender` and
`Program.Render` draw the part. Toggles changed in the UI phase apply from the next frame's
`OnFrameEditor`.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [dont-stifle-me.lib/EditorScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScalePatches.cs) | Name-string target resolution, delegates, four patches, `IsApplied` |
| [dont-stifle-me.lib/PerAxisScaleDrag.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/PerAxisScaleDrag.cs) | Per-axis copy of the stock drag with its own raw accumulator |
| [dont-stifle-me.lib/EditorScaleSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/EditorScaleSettings.cs) | `Enabled`, `Snap` and the derived `*Active` gates |
| [dont-stifle-me.lib/DontStifleMeSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeSubmod.cs) | Toolbox section in unscience |
| [dont-stifle-me.lib/DontStifleMeMenu.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/DontStifleMeMenu.cs) | Standalone top-level menu body |
| [dont-stifle-me/MenuBarPatch.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me/MenuBarPatch.cs) | `DrawProgramMenusHook` postfix (standalone only) |
| [dont-stifle-me.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dont-stifle-me.lib/README.md) | Patch table and game-update watchlist |
| [scope/part-editor-and-robotics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/part-editor-and-robotics.md) | Authors' per-target integration table and upgrade notes |

## Snippets

Targets are resolved by name; the private helpers become typed delegates before the patches go in:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L48-L63
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
```

The clamp and snap patches:

```csharp
// dont-stifle-me.lib/EditorScalePatches.cs L89-L109
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

The per-axis write, through the game's own snapping and symmetry helpers:

```csharp
// dont-stifle-me.lib/PerAxisScaleDrag.cs L54-L76
        if (!_active || _part != selected || _axis != axis)
        {
            _active = true;
            _part = selected;
            _axis = axis;
            _raw = selected.Scale[axis];
        }

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

The stock tail it replaces (game, build 5554): one value on all three axes, and the bounds:

```csharp
// current/decomp/KSA/VehicleEditor.cs L3997-L4014
		(double, double) tuple = ScaleBoundsFor(Selected);
		_scaleDragRaw = Math.Clamp(_scaleDragRaw + num4 * (double)num5, tuple.Item1, tuple.Item2);
		double scaled = QuantizeScale(Selected, _scaleDragRaw);
		if (!scaled.Equals(new ScaleFactors(Selected.Scale).Scale))
		{
			ForEachPartWithSymmetry(Selected, delegate(Part part)
			{
				part.Scale = new double3(scaled, scaled, scaled);
				part.RefreshScaleAndReposition();
			});
			Selected.Tree.RefreshStaticMass();
		}
	}

	private static (double Min, double Max) ScaleBoundsFor(Part part)
	{
		return part.IsSubPart ? (Min: 1E-06, Max: double.PositiveInfinity) : (Min: 0.5, Max: 2.0);
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `VehicleEditor.ScaleBoundsFor(Part)` (private static) | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L4011-L4014) | Postfix target and delegate |
| `VehicleEditor.UpdateSelectedScale(ref readonly double4x4, IViewport)` (private) | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3975-L4009) | Skipped and replaced while enabled |
| `VehicleEditor.QuantizeScale(Part, double)` (private static) | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L4041-L4052) | 0.25 m diameter snapping; prefix target and delegate |
| `VehicleEditor.ForEachPartWithSymmetry(Part, Action<Part>)` (private static) | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L4016-L4039) | Delegate for symmetry propagation |
| `VehicleEditor.UpdateScaleGizmo(...)` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3748-L3775) | Per-frame postfix; segment 0/1/2 = X/Y/Z layout |
| `VehicleEditor.OnFrame` -> `UpdateSelected` / `UpdateScaleGizmo` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2429-L2500) | Per-frame callers of the patched methods (`OnPreRender` calls `UpdateScaleGizmo` again at L5483) |
| `VehicleEditor.OnMouseButton` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3485-L3524) | Grab/release; stock seeding and post-drag refresh |
| `VehicleEditor.Selected`, `ScaleGizmo`, `HighlightedGizmoSegmentIndex`, `GizmoGrabbed`, `CursorPositionScreen`, `CursorPositionScreenLastFrame` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L551-L683) | Public editor state read by `Step` |
| `GenericGizmo.GetSegmentDataByViewport(IViewport)`, `PerSegmentData.Body2Cce` | [current/decomp/KSA/GenericGizmo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GenericGizmo.cs#L170-L277) | Axis direction of the dragged segment |
| `IViewport.GetCamera()`, `Camera.ScreenToEgoNearPlane(double2)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L689) | Cursor delta on the near plane |
| `Part.Scale`, `Part.ScaleTotal`, `Part.PositionEgo`, `Part.RefreshScale`, `Part.RefreshScaleAndReposition` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1695) | Per-axis write and connector/module refresh |
| `ScaleFactors(double3)` | [current/decomp/KSA/ScaleFactors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScaleFactors.cs#L18-L21) | Collapses a `double3` scale to its largest axis |
| `PartTree.RefreshStaticMass()` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1019) | Mass update after each step |
| `Program.OnFrameEditor`, `Program.DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) | Editor frame entry; standalone menu seam |

## Pitfalls and version notes

- **Connectors and mass follow the largest axis.** `Part.RefreshScale` builds
  `new ScaleFactors(ScaleTotal)`, and `ScaleFactors(double3)` keeps only
  `Math.Max(X, Max(Y, Z))`; that single value is passed to every `IRescale` module and every
  connector. A part stretched along one axis gets a non-uniform mesh but uniformly scaled connector
  offsets, mass and inertia, so attach nodes can sit off the mesh surface. The authors call this a game
  limitation that would need a `Part.RefreshScale` replacement to fix.[^ksa-scalefactors][^ksa-part][^dsm-readme]
- **Name-string targets.** All five `VehicleEditor` members are looked up with
  `AccessTools.Method(type, name)`. A rename throws at `Apply`, is logged, and the UI shows a red
  "patches not applied" notice; a second overload of any of them would throw `AmbiguousMatchException`
  at the same point. Each still has exactly one declaration in 5554.[^dsm-lib-readme][^scope-part-editor]
- **Parameter names are part of the contract.** The prefixes bind `matrixVehicleAsmb2Ego`,
  `inViewport`, `part` and `rawScale` by name; `ref double4x4` binds to the original's
  `ref readonly double4x4`. The 5402 change from `Viewport` to `IViewport` was a compile break the authors
  fixed.[^scope-part-editor]
- **Semantic drift is invisible.** `Step` is a copy of the stock projection math; if KSA changes the depth
  heuristic, per-axis drags will feel different from uniform ones without any symbol changing.
- **The raw accumulator is not clamped.** Stock clamps `_scaleDragRaw` to the bounds every frame;
  `PerAxisScaleDrag` only clamps the snapped result (inside `QuantizeScale`). Dragging far below the
  minimum builds up overshoot that has to be dragged back before the axis grows again.
- **Snap off only matters through the per-axis path.** With `Enabled` on, stock `UpdateSelectedScale`
  is skipped, so the `QuantizeScale` prefix normally affects only the per-axis delegate call (and the
  stock fallback after an exception in `Step`). With `Enabled` off every patch is a no-op.
- **Delegates are created before patching.** Calls through them still reach the patched bodies (the
  authors rely on this for the widened clamp in the snap-off path); this was not runtime-tested here.
- **Sub-parts are unchanged.** The game already gives sub-parts `(1e-6, +inf)` and the per-axis path
  only touches the selected full part and its symmetry siblings.

## See also

- [Parachute diameter unclamp ("jpl said no clamps")](./dont-stifle-me-parachute-diameter-unclamp.md)
- [Menu bar entry through DrawProgramMenusHook](./unscience-menu-bar-entry.md)
- [Garry's Torch per-axis part scaling](./garrys-torch-per-axis-part-scaling.md) (runtime per-axis scale on live vessels)
- [Godzilla vessel scaling](./godzilla-vessel-scaling.md)
- [Scene save sidecar](./unscience-scene-save-sidecar.md)
- [Don't Stifle Me sub-mod](../submods/dont-stifle-me.md) and [mod overview](../overview.md)
- [Delegates over private editor helpers reused in a replacement drag](../../../howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-5.md) - how-to article demonstrating this feature
- [Lift the vehicle editor's part-scale limits with skipping prefixes and a bounds postfix](../../../howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-4.md) - how-to article demonstrating this feature

[^dsm-readme]: dont-stifle-me/README.md
[^dsm-scale-settings]: dont-stifle-me.lib/EditorScaleSettings.cs
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^scope-part-editor]: scope/part-editor-and-robotics.md
[^unscience-patcher]: unscience/Patcher.cs
[^dsm-saves]: dont-stifle-me.lib/DontStifleMeSubmod.Saves.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-scalefactors]: KSA.ScaleFactors (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^dsm-lib-readme]: dont-stifle-me.lib/README.md
