---
type: Mod Feature
title: Measuring inside the vehicle editor, including parts held in the hand
description: In the vehicle editor the tool scans the editing space and every unattached (in-hand) part tree, anchors to the editing space, lets editor drags keep their left release, and carries part measurements across editor entry and exit
mod: ksa-measuretools
category: vehicles-and-parts
techniques: [harmony-postfix, harmony-prefix, imgui-menu]
hooks:
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - harmony-prefix KSA.Program.OnMouseButton
  - starmap StarMapAfterGui
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.PartTree
  - KSA.Part
  - KSA.Program
  - KSA.Camera
  - KSA.CameraMode
tags: [editor, vehicle-editor, parts, menu-bar, lifecycle, in-hand, unattached-trees]
---

Ruler and protractor work while building: the same part-level snapping (attach nodes, part centers,
mesh vertices, hull points) applies to the craft in the editor and to parts currently held in the
hand. Anchors follow parts as they are dragged, so a measurement updates live, and part measurements
carry across the editor boundary in both directions. Points on a deleted part disappear; free points
placed in the editor clear when it closes.[^mt-readme]

## How it works

### Getting into the editor menu bar

There is no editor-specific hook. `Patch_MenuBar` postfixes `Program.DrawProgramMenusHook`, an empty
public method that `Program.DrawMenuBar` calls after its own menus and before the version text. In
the editor, `DrawMenuBar` draws File and an "Editor" menu instead of Universe, View and HUD, and then
still calls the hook, so the postfix adds the same top-level **Measure** menu in both
contexts.[^mt-patch-menubar][^ksa-program] The menu item toggles the tool window (see the window
concept).

### Arming in the editor

`VehicleEditor.Build` puts the main viewport in `CameraMode.Orbit`, one of the two modes the tool
arms in. The editor's free camera (`CameraMode.Free`) disarms it, like flight's free camera.[^ksa-vehicleeditor]
While armed the tool owns unmodified left clicks, so part placement is suspended; the user pauses
with a short right click or closes the window to build. The README states this trade-off directly.

Two guards keep the editor usable while armed:[^mt-patch-mouse]

- `EditorHoldsDrag()`: if `Program.Editor.GizmoGrabbed` or `Editor.Highlighted?.Grabbed`, the left
  click passes to the game. `VehicleEditor.OnMouseButton` ends gizmo drags and drops grabbed parts on
  the left **release**, so eating it would leave the part stuck to the cursor.
- Alt-click passes (the editor duplicates the highlighted part on Alt + release), and a short right
  click over `Editor.Highlighted` opens the stock part menu instead of pausing.

### What gets scanned and how points are anchored

With `Program.Editor` set, `MapPicker.TryGetMeshHit` builds the matrix with
`editor.EditingSpace.GetMatrixAsmb2Ego(camera)` and scans `EditingSpace.AllParts` plus every
`PartTree` in `editor.UnattachedPartTrees`, the trees for parts that are picked up or not yet
attached. The system's vehicles are skipped entirely: the vehicle being edited stays in
`CelestialSystem.All`, and scanning it too would offer every part twice.[^mt-mappicker]

Picks produce owner-free editor anchors (`EditorPartPoint`) that reach the editing space through
`Program.Editor` at resolve time. `VehicleEditingSpace` has a fixed `PositionEcl` and `Asmb2Ecl` for
the session and no center-of-mass term, and the part transforms (`Part.MatrixAsmb2VehicleAsmb`,
`Asmb2VehicleAsmb`, `MatrixAsmb2Ego`) are recomputed uncached on every call while
`Program.Editor != null`, which is why a dragged part's anchors move with it without any
invalidation.[^ksa-vehicleeditingspace][^ksa-part]

Free clicks in the editor land on a plane through the previous point or `EditingSpace.PositionEcl`
and are stored as `EditorFreePoint` offsets; the window's Reference combo is replaced by "Reference:
edited vehicle".[^mt-window] Rulers between two editor part anchors get axial and radial components
in the editing space's frame (`EditingSpace.Asmb2Ecl`).[^mt-measurement] The editor's orthographic
projection toggle is handled by taking the ray origin from `ScreenToEgoRay`.

### Crossing the boundary

`Anchor.IsValidEditor` keeps an editor anchor valid while the editor exists and the part is in the
editing tree or any unattached tree. When the editor closes, `Rehome` finds the same `Part` object in
the launched or restored vehicle (`VehicleEditor.Dispose` hands `EditingSpace.Parts` to the vehicle)
and swaps in a flight `PartPoint`; editor free points are dropped. Going the other way, a flight
anchor becomes an editor anchor the moment its part leaves the vehicle's tree (picked up into an
unattached tree).[^mt-anchor][^ksa-vehicleeditor]

## Lifecycle and timing

Editor frame order in build 2026.10.10.5554:[^ksa-program]

| Step | Editor | MeasureTools |
|---|---|---|
| `PrepareFrame`, `Glfw.PollEvents` | `Editor.OnMouseButton` runs inside `Program.OnMouseButton` | The prefix runs first; it consumes placement clicks or passes drags (`frame:input`) |
| `PrepareFrame`, after input | Editor disposed if `EditorFlag` was cleared; sim speed forced to 0 while an editor exists | Next `Prune` re-homes editor anchors |
| `OnFrameEditor` | `Editor.OnFrame`: grabbed parts follow the cursor, transforms change (`editor:on-frame`) | Nothing |
| `OnDrawUiViewports` postfix | | `[StarMapAfterGui]`: prune, window, overlay, preview picks against this frame's part positions |
| `OnDrawUiThreadSafe` | Menu bar (with `DrawProgramMenusHook`), then `Editor.OnDrawUi` (`editor:draw-ui`) | Measure menu postfix |

None of the mod's code runs inside `Editor.OnFrame` or `Editor.OnDrawUi`; the feature reuses the
flight hooks while the editor is open, which is why its lifecycle values are `frame:input` and
`frame:draw-ui` rather than `editor:*`. AfterGui sits between the two editor phases: part positions
already include this frame's drag (`Editor.OnFrame` ran in `OnFrameEditor`), and the editor's own UI
and the menu bar are submitted after the overlay. `OnDrawUiThreadSafe` skips `Universe.OnDrawUi`
while `EditorFlag` is set, so no stock part hover or body hover flags are produced in the editor: the
part picker takes its editor branch before the stock-hover shortcut, and the body center snap is left
with stars only.

Because the editor stops the simulation, everything the mod reads in the editor is static apart from
the user's own edits.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MenuBar.cs) | Top-level Measure menu in flight and editor |
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L59-L66) | `EditorHoldsDrag` |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L181-L214) | Editor branch of the mesh-hit scan |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L181-L203) | `IsValidEditor` |

## Snippets

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L201-L214
if (Program.Editor is VehicleEditor editor)
{
    double4x4 matrixVehicleAsmb2Ego = editor.EditingSpace.GetMatrixAsmb2Ego(camera);
    if (scanFeatures)
    {
        ScanPartFeatures(camera, mouseViewport, editor.EditingSpace.AllParts, in matrixVehicleAsmb2Ego, ref feature);
        foreach (PartTree tree in editor.UnattachedPartTrees)
            ScanPartFeatures(camera, mouseViewport, tree.Parts, in matrixVehicleAsmb2Ego, ref feature);
    }
    RaycastPartSpan(editor.EditingSpace.AllParts, in matrixVehicleAsmb2Ego, ray, ref hit);
    foreach (PartTree tree in editor.UnattachedPartTrees)
        RaycastPartSpan(tree.Parts, in matrixVehicleAsmb2Ego, ray, ref hit);
    return hit.SubPart != null;
}
```

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L59-L66
// VehicleEditor drops a grabbed part and ends a gizmo drag on the left RELEASE,
// so eating those clicks strands whatever is in hand: it follows the cursor with
// no way to put it down while the tool is armed.
private static bool EditorHoldsDrag()
{
    VehicleEditor? editor = Program.Editor;
    return editor != null && (editor.GizmoGrabbed || (editor.Highlighted?.Grabbed ?? false));
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.DrawMenuBar`, `DrawProgramMenusHook`, `Editor`, `EditorFlag` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3549-L3572) | Editor menu branch; the shared hook at L4005 |
| `VehicleEditor.EditingSpace`, `UnattachedPartTrees`, `GizmoGrabbed`, `Highlighted`, `OnMouseButton`, `OnFrame`, `OnDrawUi`, `Dispose`, `OrthographicCamera` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs) | Trees to scan, drag state, lifecycle |
| `VehicleEditingSpace.GetMatrixAsmb2Ego`, `AllParts`, `PositionEcl`, `Asmb2Ecl` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L8-L114) | Editor frame |
| `Part.Grabbed`, `MatrixAsmb2VehicleAsmb` (uncached in the editor) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L740-L754) | Live tracking during drags |
| `Camera.ScreenToEgoRay` (orthographic branch) | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L740) | Editor projection toggle |

## Pitfalls and version notes

- An armed tool and normal building conflict over the left button. Pass every click that continues
  an editor interaction (grab, gizmo, Alt duplicate) and make pausing cheap.
- Do not scan `CelestialSystem.All` vehicles while the editor is open; the edited vehicle is still
  there and its parts would be picked twice.
- The window instance is created lazily from the menu postfix and survives entering and leaving the
  editor; measurements also survive (re-homed), only editor free points are dropped.
- The README's Usage paragraph says to open the tool from **View -> Measure** in flight, but the code
  at this ref adds only the top-level **Measure** menu (in flight and editor); there is no View-menu
  patch.

## See also

- [Overview](../overview.md)
- [Measure menu and tool window](./measure-menu-and-tool-window.md)
- [Part picking](./part-mesh-raycast-picking.md)
- [Part-local anchors that survive staging, docking and the editor](./part-anchor-rehoming.md)
- [Click capture](./click-capture-mouse-prefix.md)
- [Game loop and lifecycle reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Read editor parts through EditingSpace and Harmony hooks without double-counting](../../../howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-4.md) - how-to article demonstrating this feature

[^mt-patch-menubar]: MeasureTools/Patches/Patch_MenuBar.cs
[^mt-patch-mouse]: MeasureTools/Patches/Patch_MouseButton.cs
[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-measurement]: MeasureTools/Features/Measure/Measurement.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditingspace]: KSA.VehicleEditingSpace (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
