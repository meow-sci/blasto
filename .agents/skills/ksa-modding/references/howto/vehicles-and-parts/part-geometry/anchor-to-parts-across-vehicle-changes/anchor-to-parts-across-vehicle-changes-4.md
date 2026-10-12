---
type: How-To
title: Read editor parts through EditingSpace and Harmony hooks without double-counting
description: Access the craft being built and the in-hand parts with Program.Editor, EditingSpace.AllParts and UnattachedPartTrees, and choose the hook that sees live transforms
category: vehicles-and-parts
group: part-geometry
topic: anchor-to-parts-across-vehicle-changes
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/editor-measuring]
techniques: [harmony-postfix]
hooks:
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - harmony-prefix KSA.Program.OnMouseButton
  - harmony-postfix KSA.VehicleEditor.OnFrame
lifecycle: [frame:input, frame:draw-ui, editor:on-frame, editor:draw-ui]
game_types: [KSA.VehicleEditor, KSA.VehicleEditingSpace, KSA.Part, KSA.PartTree, KSA.Program]
tags: [editor, parts, unattached, harmony, transforms]
---
Outcome: from a mod, reach the craft being built and the parts currently held in the hand while the vehicle editor is open, and know which hook sees up-to-date part transforms. MeasureTools' ruler and
protractor work on exactly those parts, and the access pattern is the same for any editor-side tool.

Prerequisite: StarMap and Harmony. Demonstrated by [ksa-measuretools/editor-measuring](../../../../mods/ksa-measuretools/features/editor-measuring.md).

## Background: how the game does it

- `Program.Editor` is the live `VehicleEditor` (null in flight) and `Program.EditorFlag` is the request flag; `OnFrameEditor` creates the editor, forces simulation speed to 0 and calls `Editor?.OnFrame(MainViewport, dtPlayer)`
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578)[^ksa-program].
- `editor.EditingSpace` is a `VehicleEditingSpace`; its `Parts` is the `PartTree` being edited and `AllParts` is `Parts?.Parts` as a span [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L16)[^ksa-vehicleeditingspace] [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L40).
- `editor.UnattachedPartTrees` is a `List<PartTree>` for parts that are picked up or not yet attached [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L689)[^ksa-vehicleeditor]; `Highlighted` and `GizmoGrabbed` describe the drag state
  [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L549) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L581).
- The vehicle being edited **stays in `CelestialSystem.All`** while the editor is open, so scanning both the system and `EditingSpace` shows every part twice.
- While `Program.Editor != null`, `Part.MatrixAsmb2VehicleAsmb`, `Asmb2VehicleAsmb` and `MatrixAsmb2Ego` are recomputed on every call instead of cached [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L740-L754)[^ksa-part], which is why a held, dragged part reads correctly
  without invalidation.
- `VehicleEditingSpace.GetMatrixAsmb2Ego(camera)` is the editor's vehicle matrix; it has no centre-of-mass term (see the geometry topic).

## Lifecycle and timing

Editor frame order in this build: `Editor.OnMouseButton` runs inside `Program.OnMouseButton` (`frame:input`); `Editor.OnFrame` runs in `OnFrameEditor` and moves grabbed parts to the cursor (`editor:on-frame`);
`[StarMapAfterGui]` fires after `OnDrawUiViewports`, so part positions already include this frame's drag; `OnDrawUiThreadSafe` then draws the menu bar and `Editor.OnDrawUi` (`editor:draw-ui`)
(see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). MeasureTools patches **none** of the editor entry points: it reuses the flight hooks while the editor is open, so its own lifecycle values are `frame:input` and `frame:draw-ui`.
The `editor:*` values apply only to code that runs inside `VehicleEditor.OnFrame` / `OnDrawUi` via a patch on them (a worked patch is in the steps).

## Steps

1. **Branch on `Program.Editor` and scan the editing space plus the hand.** This is MeasureTools' picker:

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

2. **Hook the menu bar in both contexts.** There is no editor-specific hook needed for a menu: `Program.DrawProgramMenusHook` is an empty public method the game calls from `DrawMenuBar` after its own menus in flight and in the editor.
   A Harmony postfix adds the same top-level menu in both (`Program.DrawProgramMenusHook` is at [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018)):

```csharp
// MeasureTools/Patches/Patch_MenuBar.cs L14-L33
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Measure"u8))
            return;

        bool shown = MeasureWindow.IsOpen;
        if (ImGui.MenuItem("Show Window"u8, default(ImString), shown))
        {
            if (shown)
                MeasureWindow.Instance.Close();
            else
                MeasureWindow.Instance.Open();
        }
        ImGui.EndMenu();
    }
}
```

3. **Do not break drags.** `VehicleEditor` drops a grabbed part and ends a gizmo drag on the left mouse **release**; a tool that consumes clicks must let that release through:

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

4. **If you must run inside the editor's own frame**, patch its entry points. Adapted (not in MeasureTools) minimal postfix; `VehicleEditor.OnFrame(IGameViewport, double)` is public:

```csharp
// adapted: Harmony postfix on the editor frame; not from KSA-MeasureTools
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnFrame))]
internal static class Patch_EditorFrame
{
    [HarmonyPostfix]
    private static void Postfix(VehicleEditor __instance)
    {
        foreach (Part part in __instance.EditingSpace.AllParts)
            Touch(part);
        foreach (PartTree tree in __instance.UnattachedPartTrees)
            foreach (Part part in tree.Parts)
                Touch(part);
    }

    private static void Touch(Part part) { /* read part.MatrixAsmb2VehicleAsmb here */ }
}
```

   This runs once per frame in `editor:on-frame`, after grabbed parts have followed the cursor.

## Complete example

- [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L196-L214)[^ksa-measuretools-mappicker] - editor branch of the mesh-hit scan.
- [MeasureTools/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MenuBar.cs#L14-L33)[^ksa-measuretools-patch-menubar] and [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs#L59-L66)[^ksa-measuretools-patch-mousebutton] - menu and drag guards.
- [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L161-L203)[^ksa-measuretools-anchor] - `IsValidEditor`, how an anchor keeps following a part across editor trees.

## Variations and gotchas

- **Never scan the system's vehicles in the editor** (the original of the edited vehicle is still there).
- **Held parts live in `UnattachedPartTrees`**, not in `EditingSpace.Parts`; an anchor on a held part is still valid ([article 1](anchor-to-parts-across-vehicle-changes-1.md)).
- **`OnDrawUiThreadSafe` skips `Universe.OnDrawUi` while `EditorFlag` is set**, so stock part hover flags are not produced in the editor; do your own picking against `AllParts`.
- **The editor stops the simulation**, so everything is static apart from the user's edits.
- **Editor close** hands `EditingSpace.Parts` to the launched vehicle; re-home anchors ([article 1](anchor-to-parts-across-vehicle-changes-1.md)).

## See also

- [Overview](overview.md); [Article 1](anchor-to-parts-across-vehicle-changes-1.md)
- [ksa-measuretools/editor-measuring](../../../../mods/ksa-measuretools/features/editor-measuring.md)
- [Read part meshes, attach nodes and part-local points](../get-part-points-in-vehicle-and-world-frames/overview.md)

[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditingspace]: current/decomp/KSA/VehicleEditingSpace.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-measuretools-mappicker]: KSA-MeasureTools: MeasureTools/Features/Measure/MapPicker.cs
[^ksa-measuretools-patch-menubar]: KSA-MeasureTools: MeasureTools/Patches/Patch_MenuBar.cs
[^ksa-measuretools-patch-mousebutton]: KSA-MeasureTools: MeasureTools/Patches/Patch_MouseButton.cs
[^ksa-measuretools-anchor]: KSA-MeasureTools: MeasureTools/Features/Measure/Anchor.cs
