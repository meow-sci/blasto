---
type: How-To
title: "Live vehicle data: Harmony postfix on VehicleEditor.OnDrawUi for the part tree being built"
description: Add a panel to the vehicle editor by postfixing the editor's own UI method and reading the craft's PartTree for dry, propellant and wet mass, since the editor has no StarMap hook and no flight state
category: ui
group: windows
topic: show-live-vehicle-data-in-a-window
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/editor-panel-injection]
techniques: [harmony-postfix, imgui-window]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [editor:draw-ui, editor:on-frame]
game_types: [KSA.VehicleEditor, KSA.VehicleEditingSpace, KSA.PartTree, KSA.Program, KSA.CelestialSystem]
tags: [editor, part-tree, mass, harmony-postfix, kitten-engineer-redux]
---
In the vehicle editor there is no controlled vehicle and no flight physics, only a `PartTree` the player is editing. Kitten Engineer Redux (KER) adds a "Kitten Engineer Redux" panel to the editor - total delta-v, wet mass, part count, dry and propellant mass, an analysis-conditions section and a per-stage table - by postfixing the editor's own `OnDrawUi`. The panel therefore exists only while the editor exists and always sees the same `PartTree` the editor is mutating. Demonstrated by Kitten Engineer Redux. Prerequisites: StarMap and Harmony.

## Background: how the game does it

- **The editor is an object.** `Program.Editor` is a static `VehicleEditor?`; when `EditorFlag` is set and there is no editor, `OnFrameEditor` creates one, sets `ControlledVehicle = null` and pauses the simulation (`Universe.SetSimulationSpeed(0.0)`), then calls `Editor?.OnFrame(MainViewport, dtPlayer)` every frame.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2576)[^ksa-program]
- **UI call.** `Program.OnDrawUiThreadSafe` calls `Editor?.OnDrawUi(MainViewport)` near its end, after the menu bar, gauges and the transfer planner window and just before the launch menu.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3238-L3240)[^ksa-program] While `EditorFlag` is true, `Universe.OnDrawUi` is skipped, so no vehicle's `OnDrawUi` runs (and the flight HUD of approach 2 is absent).
- **The craft.** `VehicleEditor.EditingSpace` is a `VehicleEditingSpace` whose nullable `Parts` field is the `PartTree`; `VehicleEditor.IsEmpty` means `EditingSpace.Parts == null`.[current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L16-L16)[^ksa-vehicleeditingspace]
- **Mass.** `PartTree.ComputeInertMassPropertiesAsmb()` and `ComputePropellantMassPropertiesAsmb()` return `OffsetMassProperties`; the editor itself adds the two together when launching a new vehicle.[current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2280-L2280)[^ksa-vehicleeditor]

## Lifecycle and timing

The postfix runs in `editor:draw-ui` (`VehicleEditor.OnDrawUi`), a phase of the same `frame:draw-ui` block and only when `DrawUI` is true; the editor's own state was already updated earlier in the frame by `VehicleEditor.OnFrame` (`editor:on-frame`, called from `OnFrameEditor` after `PrepareImGui`). The editor `PartTree` is main-thread state with no vehicle solver step. The only background consumer is the staging performance job: `Program.OnFrame` queues it after the draw-UI block when the list is dirty,[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2277-L2284)[^ksa-program] and `PrepareFrame` waits for the worker at its start, so during the postfix nothing else reads the tree (per the mod feature page). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Postfix the editor's UI method.** `Mod.OnFullyLoaded` runs `PatchAll` on the assembly (see [approach 2](show-live-vehicle-data-in-a-window-2.md)), which picks this up:

```csharp
// Patch_EditorPanel.cs L6-L13
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnDrawUi))]
internal static class Patch_EditorPanel
{
    private static void Postfix(VehicleEditor __instance, GameViewport inViewport)
    {
        EditorPanel.Draw(__instance, inViewport);
    }
}
```

2. **Guard, then read the part tree.** An empty editor has no `Parts`. Resolve the analysis environment, because there is no vehicle; KER uses the home body of the current system:

```csharp
// EditorPanel.cs L25-L43
public static void Draw(VehicleEditor editor, GameViewport viewport)
{
    SettingsStore.Tick();

    if (!Visible)
        return;

    PartTree? parts = editor.EditingSpace.Parts;
    if (parts == null)
        return;

    VehicleMassSummary mass = MassAnalyzer.Analyze(parts);
    _home = Universe.CurrentSystem?.HomeBody;
    _maxAnalysisAltitude = EnvironmentHelpers.GetAtmosphereHeight(_home);
    _analysisAltitude = Math.Clamp(_analysisAltitude, 0f, _maxAnalysisAltitude);
    float ambientPressure = _useAtmosphere
        ? EnvironmentHelpers.GetAtmosphericPressureAtAltitude(_home, _analysisAltitude)
        : 0f;
    float surfaceGravity = EnvironmentHelpers.ComputeSurfaceGravity(_home, _analysisAltitude);
```

3. **Compute dry, propellant and wet mass** exactly as the editor does:

```csharp
// MassAnalyzer.cs L9-L16
public static VehicleMassSummary Analyze(PartTree parts)
{
    float dry = parts.ComputeInertMassPropertiesAsmb().Props.Mass;
    float prop = parts.ComputePropellantMassPropertiesAsmb().Props.Mass;
    int partCount = parts.Parts.Length - 1;

    return new VehicleMassSummary(dry, prop, dry + prop, partCount);
}
```

4. **Open the window with a first-use position and a height estimated from the stage count**, so ImGui's saved layout wins after the first frame, then draw with the draw-list widget kit:

```csharp
// EditorPanel.cs L57-L67
float estimatedHeight = 380f + burn.Sequences.Count * 22f;
float maxHeight = Math.Max(300f, viewport.Size.Y - 120f);
float defaultHeight = Math.Min(Math.Max(300f, estimatedHeight), maxHeight);
float2 defaultPos = viewport.Position + new float2(viewport.Size.X - DefaultWidth - SidePadding, 60f);
bool open = PanelKit.BeginWindow("Kitten Engineer Redux###KerEditorPanel"u8, defaultPos, new float2(DefaultWidth, defaultHeight));
if (open)
{
    float contentWidth = ImGui.GetContentRegionAvail().X;
    ImDrawListPtr drawList = ImGui.GetWindowDrawList();
    float2 origin = ImGui.GetCursorScreenPos();
    PanelKit.DrawAccentStrip(drawList, origin, ImGui.GetWindowSize().Y);
```

## Complete example

| File | Role |
|---|---|
| [Patch_EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_EditorPanel.cs#L6-L13) | Postfix on `VehicleEditor.OnDrawUi` |
| [EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L25-L90) | Reads parts and environment, draws the window and pop-out section |
| [MassAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MassAnalyzer.cs#L9-L16) | Dry, propellant and wet mass and part count |

## Variations and gotchas

- **`GameViewport` versus `IGameViewport`.** As in approach 2, the postfix declares `GameViewport inViewport` while the target takes `IGameViewport`; declare the interface in your own patch.
- **Editor environment is the home body's.** Gravity and pressure always use `Universe.CurrentSystem?.HomeBody`, even when the craft is edited from flight around another body (per the mod feature page).
- **Part count is `Parts.Length - 1`.** `PartTree.Parts` includes the root part; what the subtracted entry is meant to be is not documented in the mod (unverified).
- **Not persisted.** The editor panel's analysis toggles are session-only; the flight HUD's copies are saved.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of show-live-vehicle-data-in-a-window](overview.md)
- [Approach 1: plain ImGui window from StarMapAfterGui](show-live-vehicle-data-in-a-window-1.md)
- [Approach 2: flight HUD postfix](show-live-vehicle-data-in-a-window-2.md)
- [Keep UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../menus/inject-ui-into-a-stock-window/overview.md)
- Mod feature: [Editor panel injected into the vehicle editor](../../../../mods/kitten-engineer-redux/features/editor-panel-injection.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditingspace]: KSA.VehicleEditingSpace (decompiled, build 2026.10.10.5554)
