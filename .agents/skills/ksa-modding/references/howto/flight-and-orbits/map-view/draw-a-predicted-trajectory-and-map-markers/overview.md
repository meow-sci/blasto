---
type: Game Feature
title: Draw a predicted trajectory and markers in the map view, and control orbit lines
description: Preview a planned burn's trajectory, multi-pass previews and impact sights on screen, draw map-view markers, arrows and overlays, and override whether a body's orbit line is drawn and recolour it
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/planner-preview-trajectories
  - mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering
  - mods/ksarmory/features/guided-bomb-tail-kit-and-sight
  - mods/ksa-deltavmap/features/map-mode-transfer-overlay
  - mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays
  - mods/compendium/features/orbit-line-visibility-and-color
lifecycle: [frame:draw-ui, frame:pre-render, render:imgui, startup:immediate-load]
game_types:
  - KSA.TransferPlanner
  - KSA.FlightPlan
  - KSA.OrbitalTransfers
  - KSA.PatchedConic
  - KSA.CelestialSystem
  - KSA.Camera
  - KSA.IViewport
  - KSA.CameraMode
  - KSA.ImGuiHelper
  - KSA.Orbit
  - KSA.IOrbiter
  - KSA.IPosition
  - KSA.Astronomical
  - KSA.Celestial
  - KSA.OrbitUiMode
  - KSA.NearestOrbitPointJob
  - KSA.IGameViewport
  - Brutal.Numerics.byte4
tags:
  - map-view
  - trajectory-preview
  - orbit-lines
  - markers
  - overlay
  - flight-plan
  - projection
  - imgui-drawlist
  - orbit-line-color
---
A mod that predicts something (a burn's resulting orbit, a ballistic arc, a transfer window) usually wants the player to see it in the map view. The game offers two drawing routes and the mods use both. Orbit lines go through the game's own renderer: a `FlightPlan` is given line instances in the player's chosen orbit style. Everything else, such as markers, arrows, polylines and labels, is ImGui drawing at coordinates projected through the camera. A third, smaller question is whether the game draws a body's own orbit line at all and in which colour.

## Engine background

- **Orbit lines are line instances added before render.** `FlightPlan.AddLineInstances(IViewport, IOrbiter, bool isActive, bool drawVehiclePosition, TrueAnomaly startTa, TrueAnomaly nextBurnTa, bool drawDangerGroundTrack = true, bool isPostBurnOrbit = false)` reads each patch's colour from `Orbit.OrbitLineColor` and needs cached points on the plan; the markers for the same plan come from `FlightPlan.DrawUi` in the UI pass.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L908-L927) [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L834)
- **Stock builds its own previews the same way.** `OrbitalTransfers.BuildFlightPlan` propagates a plan for a burn; `TransferPlanner.OnPreRender(IViewport)` calls `DrawSelectedTransfer`, which fills missing points with `UpdateTaskUtils.GenerateSpacedPoints` and adds line instances for the selected entry.[^ksa-orbitaltransfers] [^ksa-transferplanner] [^ksa-updatetaskutils] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L298) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1679-L1689) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L2004-L2016) [current/decomp/KSA/UpdateTaskUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UpdateTaskUtils.cs#L18)
- **Where the lines are added.** `Program.OnPreRender` runs `Universe.OnPreRender(viewport)` per viewport; `CelestialSystem.OnPreRender` pre-renders every body and then `TransferPlanner.OnPreRender`.[^ksa-program] [^ksa-celestialsystem] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2423-L2433) [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L541-L551)
- **Projection.** `Camera.EclToEgo`, `EgoToScreen(posEgo, ignoreBehind = true)` and `EclToScreen(posEcl, ignoreBehind = true)` return `(NaN, NaN)` for a point behind the camera; `Camera.GetPositionEgo(IPosition)` is where the engine draws a vehicle; `Astronomical.GetPositionEclFromCce` lifts a parent-frame vector.[^ksa-camera] [^ksa-astronomical] [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L240) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340-L355) [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369)
- **Draw lists.** `ImGuiHelper.GetOverlayDrawList(viewport)` is the main viewport's background list for the main viewport and the current window's list otherwise; `ImGui.GetBackgroundDrawList` renders under all windows.[^ksa-imguihelper] [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51)
- **Orbit-line gating of bodies.** `Astronomical.ShouldDrawLines` decides whether a celestial's own line is drawn; `Orbit.OrbitLineColor` is its colour.[^ksa-orbit] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L658-L684) [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1147)
- **Timing: lines and markers run in different phases.** ImGui calls are legal from `PrepareImGui` until `ImGui.Render`; `Universe.OnPreRender` (and so line instancing) runs after `ImGui.Render`. Orbit-line instances therefore come from a pre-render hook and markers from the UI pass. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2290).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Real FlightPlan from OrbitalTransfers.BuildFlightPlan, drawn in a TransferPlanner.OnPreRender postfix](draw-a-predicted-trajectory-and-map-markers-1.md) | KSA-AdvancedFlightComputer | harmony postfix and prefix | `frame:pre-render`, `frame:draw-ui` | Stock line style, markers and apsides for free; needs a detached plan and paired prefixes that fail open to suppress the stock preview |
| [Per-pass dimmed orbit lines with markers and a cache](draw-a-predicted-trajectory-and-map-markers-2.md) | KSA-AdvancedFlightComputer | harmony postfix, overlay draw list | `frame:pre-render`, `frame:draw-ui` | Several plans on screen with a brightness ramp; the previews are expensive, so keys must be quantized |
| [ImGui polyline of a flown prediction](draw-a-predicted-trajectory-and-map-markers-3.md) | KSArmory | imgui drawlist | `frame:draw-ui`, `render:imgui` | Works for any point list, even a non-orbit; you own anchoring, clipping and thinning |
| [Projected markers and fixed-length arrows](draw-a-predicted-trajectory-and-map-markers-4.md) | KSA-DeltaVMap | world-to-screen projection | `frame:draw-ui`, `render:imgui` | Points on real orbits and constant-size gizmos; map mode only, drawn only while your window draws |
| [Click-through full-screen overlay of CCI points](draw-a-predicted-trajectory-and-map-markers-5.md) | KSA-AdvancedFlightComputer | imgui drawlist, ImGui window | `frame:draw-ui`, `render:imgui` | Plans that follow the planet in flight and map views; uses the main camera only, needs fault isolation |
| [Force or hide a body's orbit line and recolour it](draw-a-predicted-trajectory-and-map-markers-6.md) | Compendium | harmony postfix | `frame:draw-ui`, `frame:pre-render` | A few lines for a whole system; bodies only, and the table is read from a worker thread |

## Mod code

| Approach | Entry point |
|---|---|
| 1 | [AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs#L12-L31) |
| 2 | [AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs#L9-L37) |
| 3 | [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L221-L247) |
| 4 | [DeltaVMap/Render/TransferWindowMapOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowMapOverlay.cs#L59-L97) |
| 5 | [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs#L25-L109) |
| 6 | [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L40-L61) |

## Choosing

- A burn or maneuver you can express as a `FlightPlan`: approach 1, and approach 2 for several in one view.
- A prediction that is not an orbit (ballistic arc, guidance preview): approach 3.
- Markers or direction gizmos at orbit positions: approach 4.
- Many plan points, in inertial or body-fixed frames, in both flight and map views: approach 5.
- Making existing body orbit lines visible, hidden or recoloured: approach 6.

## See also

- [Anchor and pick points on bodies](../anchor-and-pick-points-on-bodies/overview.md)
- [Find the closest approach along the flight plan](../find-closest-approach-along-the-flight-plan/overview.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Compute transfer windows](../../transfers/compute-transfer-windows/overview.md)
- [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Draw world-space lines and gizmos](../../../rendering/overlays/draw-world-space-lines-and-gizmos/overview.md)
- [Draw a fullscreen click-through overlay](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-updatetaskutils]: KSA.UpdateTaskUtils (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
