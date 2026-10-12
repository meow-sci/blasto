---
type: Mod Feature
title: Editor panel injected into the vehicle editor
description: A Harmony postfix on VehicleEditor.OnDrawUi draws a KER-style analysis window every editor frame, reading the craft under construction from EditingSpace.Parts and the home body's gravity and atmosphere
mod: kitten-engineer-redux
category: ui
techniques: [harmony-postfix, imgui-window, imgui-drawlist]
hooks: [harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.PartTree
  - KSA.OffsetMassProperties
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.PhysicalAtmosphereReference
  - KSA.GameViewport
  - KSA.Program
tags: [editor, vehicle-editor, imgui, harmony, postfix, delta-v, mass, staging]
---

In the vehicle editor the player gets a "Kitten Engineer Redux" window docked near the right edge of
the screen: three stat tiles (total delta-v, wet mass, part count), dry and propellant mass, an
"Analysis conditions" section and a per-stage table (see
[Native stage performance readout](native-stage-performance-readout.md)). The mechanism is a single
Harmony postfix on the editor's own UI method, so the panel only exists while the editor exists and
always sees the same `PartTree` the editor is mutating.

## How it works

1. `Mod.OnFullyLoaded` runs `Harmony.PatchAll` on the mod assembly, which picks up
   `[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.OnDrawUi))]` on
   [Patch_EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_EditorPanel.cs#L6-L13).[^ker-patch-editorpanel] [^ker-mod]
2. The postfix forwards the editor instance and viewport to `EditorPanel.Draw`
   ([EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L25-L90)).[^ker-editorpanel]
3. The craft is `editor.EditingSpace.Parts`, a nullable `PartTree` on `VehicleEditingSpace`; an empty
   editor returns early.[^ksa-editingspace]
4. Mass: `MassAnalyzer.Analyze` sums `PartTree.ComputeInertMassPropertiesAsmb().Props.Mass` (dry) and
   `ComputePropellantMassPropertiesAsmb().Props.Mass` (propellant), the same two calls the editor
   itself adds together when it launches a new vehicle
   ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2280)).
   Part count is `Parts.Length - 1` ([MassAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MassAnalyzer.cs#L9-L16)).[^ker-massanalyzer] [^ksa-vehicleeditor] [^ksa-parttree]
5. Environment: the analysis body is `Universe.CurrentSystem?.HomeBody`. Gravity is recomputed as
   `G * M / (MeanRadius + altitude)^2` with `Constants.GRAVITATIONAL_CONSTANT`; atmosphere height and
   pressure come from `Astronomical.GetAtmosphereReference().Physical`
   (`Height`, `GetAtmosphericPressureAtAltitude`) ([EnvironmentHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EnvironmentHelpers.cs#L7-L35)).[^ker-envhelpers] [^ksa-celestialsystem] [^ksa-physatmo] [^ksa-constants]
6. Stage numbers come either from the game's staging simulator (default, "KSA simulation") or from
   the mod's own estimator ("Altitude estimate"), chosen with a two-way toggle in the conditions
   section; the estimate mode adds a vacuum/atmosphere toggle and an altitude slider clamped to the
   home body's atmosphere height ([EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L92-L131)).
7. The window is drawn with the mod's draw-list widget kit: `PanelKit.BeginWindow` with
   `ImGuiCond.FirstUseEver` placement at `viewport.Position + (Size.X - 340 - 16, 60)` and a height
   estimated from the stage count, so ImGui's saved layout wins after the first frame.

## Lifecycle and timing

- KSA calls `Editor?.OnDrawUi(MainViewport)` near the end of `Program.OnDrawUiThreadSafe`, after the
  menu bar, gauges and transfer planner window, and only when `DrawUI` is true
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3241)). That is
  `frame:draw-ui`, inside the ImGui frame, so the postfix may open windows directly.[^ksa-program]
- Running as a postfix puts the panel after the editor's own toolbars and part windows in submission
  order, with the editor's per-frame state already updated by `VehicleEditor.OnFrame`, which
  `Program.OnFrameEditor` calls earlier in the frame (`editor:on-frame`, before `OnFrameViewports`).
- The editor `PartTree` is main-thread state; there is no vehicle solver step for it. The only
  background consumer is the game's sequence-performance job, which `Program.OnFrame` queues after
  the draw-UI block (only while the list is dirty) and waits for at the top of the next `PrepareFrame`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2277-L2284),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2151-L2152)), so during
  `OnDrawUi` the part tree is not being read on another thread. Because the panel's default "KSA
  simulation" mode calls `RecomputeIfDirty()` here, the list is already clean when the game checks
  it, and the game's job is skipped (see
  [Native stage performance readout](native-stage-performance-readout.md)).
- When the HUD is hidden (`DrawUI == false`), `OnDrawUiThreadSafe` returns early and the panel does
  not draw. `SettingsStore.Tick()` is called before the `Visible` check, so settings still load and
  autosave while the panel is toggled off from the menu.

## Key code

| Path | Role |
|---|---|
| [Patch_EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_EditorPanel.cs) | Harmony postfix on `VehicleEditor.OnDrawUi` |
| [EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs) | Reads parts, environment, chooses analyzer, draws the window |
| [MassAnalyzer.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/MassAnalyzer.cs) | Dry, propellant and wet mass plus part count |
| [EnvironmentHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EnvironmentHelpers.cs) | Gravity, atmosphere height and pressure for a body and altitude |

## Snippets

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

```csharp
// EditorPanel.cs L25-L55
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

        VehicleBurnAnalysis burn;
        if (_useNativeStaging)
        {
            burn = SequenceAnalyzer.AnalyzeNative(parts, surfaceGravity, recomputeIfDirty: true, out bool usedNativeResults);
            _usedNativeResults = usedNativeResults;
        }
        else
        {
            burn = SequenceAnalyzer.Analyze(parts, mass.WetMass, ambientPressure, surfaceGravity);
            _usedNativeResults = false;
        }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `VehicleEditor.OnDrawUi(IGameViewport)` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L5747) | Postfix target |
| `VehicleEditor.EditingSpace` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L545) | The `VehicleEditingSpace` being edited |
| `VehicleEditingSpace.Parts` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L16) | The craft's `PartTree`, null when empty |
| `PartTree.ComputeInertMassPropertiesAsmb()` / `ComputePropellantMassPropertiesAsmb()` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1153-L1200) | Dry and propellant mass |
| `PartTree.Parts` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Part count |
| `CelestialSystem.HomeBody` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64) | Body used for editor gravity and pressure |
| `IParentBody.Mass`, `IRadius.MeanRadius`, `Astronomical.GetAtmosphereReference()` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs), [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L392) | Gravity and atmosphere lookup |
| `PhysicalAtmosphereReference.Height` / `GetAtmosphericPressureAtAltitude(double)` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L23-L80) | Slider range and ambient pressure |
| `Constants.GRAVITATIONAL_CONSTANT` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L25) | `G * M / r^2` |

## Pitfalls and version notes

- The postfix declares `GameViewport inViewport`, but every decompiled build from 2026.9.7.5402 to
  2026.10.10.5554 declares `VehicleEditor.OnDrawUi(IGameViewport inViewport)` (earlier builds took
  `Viewport`, which the postfix declared before commit `9ad3975`). Harmony binds injected parameters
  by name, and Lib.Harmony 2.4.2 (the version StarMap ships) emits a plain `ldarg` for two reference
  types with no type check or cast (`MethodCreatorTools.EmitCallParameter` in the decompiled 2.4.2
  assembly), so the patch binds. The editor always passes `Program.MainViewport`, and `GameViewport` is
  the only `IGameViewport` implementation in build 5554, so the object really is a `GameViewport`.
  Declare `IGameViewport` in your own patches; a second implementation would turn this into silent
  type confusion rather than an exception.
- `MassAnalyzer` subtracts one from `PartTree.Parts.Length` (since commit `8635580`, without
  explanation), while the per-stage "Parts remaining" values (`CountAttachedParts`,
  `CountRemainingParts`) count every part; commit `e32071b` "Fixed part count bug" only stopped those
  stage counts from adding `SubParts`. `PartTree.Parts` includes the root part, so the headline count
  is one lower than a stage row covering the whole craft; what the subtracted entry is meant to be
  is not documented (unverified).
- Editor gravity and pressure always use the home body, even if the craft is being edited from flight
  around another body (`Program.Editor` can be created for an existing vehicle).
- The editor's `_useNativeStaging` and `_useAtmosphere` toggles are not persisted; only the flight
  HUD's copies are saved (see [JSON settings with autosave](json-settings-autosave.md)).
- The editor already has a native delta-v debug window (`Program.ShowDeltaVDebug` makes
  `VehicleEditor.OnDrawUi` call `SequencePerformanceList.DrawDebugWindow`), which is a useful
  reference for what the staging simulator exposes.[^ksa-seqperflist]

## See also

- [Native stage performance readout](native-stage-performance-readout.md)
- [Custom stage delta-v estimator](custom-stage-delta-v-estimator.md)
- [Draw-list widget kit with pop-out sections](draw-list-widget-kit.md)
- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Live vehicle data: Harmony postfix on VehicleEditor.OnDrawUi for the part tree being built](../../../howto/ui/windows/show-live-vehicle-data-in-a-window/show-live-vehicle-data-in-a-window-3.md) - how-to article demonstrating this feature

[^ker-patch-editorpanel]: Patch_EditorPanel.cs
[^ker-mod]: Mod.cs
[^ker-editorpanel]: EditorPanel.cs
[^ker-massanalyzer]: MassAnalyzer.cs
[^ker-envhelpers]: EnvironmentHelpers.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-editingspace]: KSA.VehicleEditingSpace (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-physatmo]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^ksa-seqperflist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
