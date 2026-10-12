---
type: How-To
title: "Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport"
description: Paint a HUD, markers or sky lines over the whole game view with an input-less transparent ImGui window, and compare the flag sets five mods use
category: ui
group: overlays
topic: draw-a-fullscreen-click-through-overlay
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-aircrafthud/features/fullscreen-drawlist-hud
  - mods/compendium/features/body-pointer-overlay
  - mods/ksarmory/features/world-anchored-screen-markers
  - mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays
  - mods/ohmystars/features/sky-overlay-lines-and-labels
techniques: [imgui-drawlist, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:draw-ui, frame:viewports]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGuiViewportPtr
  - KSA.Camera
  - KSA.Program
tags: [overlay, hud, imgui, window-flags, draw-list, click-through, projection]
---
The most common way to draw over the game view is a transparent ImGui window that covers the main viewport and takes no input. Clicks fall through to the game, the window's own draw list takes lines,
shapes and text, and you project 3D positions with the camera. Five mods use it (KSA_AircraftHUD, OhMyStars, Compendium, KSArmory and KSA-AdvancedFlightComputer) with slightly different flag sets. This article
is the recipe and the comparison. The other draw lists are in [approach 2](draw-a-fullscreen-click-through-overlay-2.md) and [approach 3](draw-a-fullscreen-click-through-overlay-3.md).
Demonstrated by those five mods.

## Background: how the game does it

- ImGui window flags are plain enum values: `NoInputs` is `NoMouseInputs | NoNavInputs | NoNavFocus` (0x30200), `NoDecoration` is `NoTitleBar | NoResize | NoScrollbar | NoCollapse` (0x2B), `NoNav` is
  `NoNavInputs | NoNavFocus`.[current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L5-L35)[^brutal-imguiapi-imguiwindowflags]
- A window that does not receive mouse input does not set `WantCaptureMouse`, so `Program.OnMouseButton` (which returns early only when ImGui wants the mouse over the main viewport) still handles the click.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program]
- `ImGui.GetMainViewport()` gives `Pos` and `Size` of the OS window area in ImGui's coordinates. A camera projects into **viewport-local** pixels, so add `viewport.Pos` (see
  [cursor to world ray](../../input/cursor-to-world-ray/overview.md)).
- `Camera.EclToScreen` / `EgoToScreen` return `(NaN, NaN)` for points behind the camera when `ignoreBehind` is true, which is the culling rule most overlays use.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L333-L376)[^ksa-camera]

## Lifecycle and timing

- **From `[StarMapAfterGui]` (`frame:draw-ui`).** The postfix on `Program.OnDrawUiViewports` runs after `PrepareImGui` and `OnFrameViewports`, so the cameras are current and ImGui is open. OhMyStars, Compendium, KSArmory and
  AFC use it. The window is submitted before the game's own `OnDrawUiThreadSafe` windows, so with `NoBringToFrontOnFocus` it stays behind them; it vanishes with the HUD (F2).
- **From a postfix on `Program.OnFrameEditor` (`frame:viewports`).** KSA_AircraftHUD draws earlier: `OnFrameEditor` runs after `PrepareImGui` and before the `if (DrawUI)` gate, so the HUD survives F2; on frames where
  `PrepareFrame` returns `Exit` it does not run. See [keep UI running when the HUD is hidden](../draw-ui-while-the-hud-is-hidden/overview.md).
- Vehicle and body state is the step applied in this frame's `PrepareFrame`; the next physics step is already running, so reads are safe, writes are not. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Size and place the window on the main viewport**, then begin it with an input-less, background-less flag set:

```csharp
// AircraftHUDMain.cs L997-L1013
// CREATE WINDOW
ImGuiViewport* viewport = ImGui.GetMainViewport();
ImGui.SetNextWindowPos(viewport->Pos);
ImGui.SetNextWindowSize(window_size);
ImGui.SetNextWindowViewport(viewport->ID);
ImGuiWindowFlags flags =
    ImGuiWindowFlags.NoTitleBar |
    ImGuiWindowFlags.NoResize |
    ImGuiWindowFlags.NoMove |
    ImGuiWindowFlags.NoCollapse |
    ImGuiWindowFlags.NoBringToFrontOnFocus |
    ImGuiWindowFlags.NoBackground |
    ImGuiWindowFlags.NoFocusOnAppearing |
    ImGuiWindowFlags.NoInputs |
    ImGuiWindowFlags.NoNavFocus;
ImGui.Begin("HUDFullscreenWindow", flags);
ImDrawListPtr draw_list = ImGui.GetWindowDrawList();
```

2. **If a shader or a clip depends on the window's full size, force it.** AircraftHUD draws an almost-transparent rectangle over the whole window ("It seems that ImGui window gets clipped"). The mod pairs
   the HUD with a ShaderExtensions per-window shader, which sizes its quad from the bounding box of the draw list's vertices, so without this rectangle the shader would be centred on whatever symbols happen to be drawn:

```csharp
// AircraftHUDMain.cs L1038-L1039
// Workaround: It seems that ImGui window gets clipped, draw almost transparent rect to force full size
ImDrawListExtensions.AddRectFilled(draw_list, new float2(0f, 0f), window_size, new ImColor8(0, 0, 0, 1), 0);
```

3. **Project and cull.** Project each point with the camera, add the viewport origin, skip NaN. OhMyStars' helper:

```csharp
// src/StellariumRenderer.cs L56-L63
public static float2 EgoToOverlayScreen(Camera camera, double3 position) {
    float2 screen = camera.EgoToScreen(position);
    ImGuiViewport* viewport = ImGui.GetMainViewport();

    return viewport == null
        ? screen
        : screen + viewport->Pos;
}
```

4. **Draw, then `End`.** Always pair `Begin` with `End` even when `Begin` returns false, and wrap the draw so one exception cannot leave the ImGui window stack unbalanced (AFC's `WindowEnd` ref struct disposes the
   window however the draw exits):

```csharp
// AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L25-L44
private const ImGuiWindowFlags OverlayFlags =
    ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
    ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoInputs |
    ImGuiWindowFlags.NoBackground;

// Open a full-screen, click-through overlay window and hand back its draw list. The caller holds a WindowEnd for the rest of its draw, so the window ends however that draw exits.
private static ImDrawListPtr BeginOverlayWindow(IGameViewport vp, string name)
{
    ImGui.SetNextWindowPos(new float2(0f, 0f));
    ImGui.SetNextWindowSize(new float2(vp.Width, vp.Height));
    ImGui.Begin(name, OverlayFlags);
    return ImGui.GetWindowDrawList();
}

// Ends the ImGui window it guards when its scope exits, an exception included. ImGui keeps a window stack, so a Begin left without its End does not fail where the fault is. It fails at the end of the frame as a missing End that names the wrong window.
private ref struct WindowEnd
{
    public void Dispose() => ImGui.End();
}
```

5. **Make anything clickable a separate window.** The overlay ignores input, so KSArmory submits each marker label as its own small window after the overlay's `End()` (ImGui windows do not nest), and hover on the overlay
   is computed from `ImGui.GetMousePos()` against the projected point.

## Comparing the flag sets

| Mod | Window id | Position and size | Flags |
|---|---|---|---|
| KSA_AircraftHUD [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L997-L1013) | `HUDFullscreenWindow` | `viewport->Pos`, precomputed `window_size`, `SetNextWindowViewport` | `NoTitleBar`, `NoResize`, `NoMove`, `NoCollapse`, `NoBringToFrontOnFocus`, `NoBackground`, `NoFocusOnAppearing`, `NoInputs`, `NoNavFocus` |
| OhMyStars [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L346-L367) | `HUDFullscreenWindow` | `viewport->Pos`, `viewport->Size`, `SetNextWindowViewport` | the same nine |
| Compendium [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L147-L166) | `##CompendiumPointerOverlay` | `viewport.Pos`, `viewport.Size`, `SetNextWindowViewport` | the same nine plus `NoSavedSettings` |
| KSArmory [src/KSArmory/Ksa/Markers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Markers.cs#L95-L111) | `##KSArmoryMarkers` | `main.Pos`, `main.Size`, `SetNextWindowBgAlpha(0)` | `NoDecoration`, `NoInputs`, `NoNav`, `NoFocusOnAppearing`, `NoBringToFrontOnFocus`, `NoSavedSettings`, `NoBackground` |
| AFC [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs#L25-L37) | caller-supplied | `(0, 0)`, `vp.Width x vp.Height` | `NoDecoration`, `NoMove`, `NoSavedSettings`, `NoBringToFrontOnFocus`, `NoFocusOnAppearing`, `NoInputs`, `NoBackground` |

All draw with the **window** draw list (`ImGui.GetWindowDrawList()`), not the background or foreground list, so the overlay is z-ordered like a window: it can land over or under panels. `NoBringToFrontOnFocus` keeps it from
raising itself.

## Complete example

| File | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L997-L1013) | Window and flags |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341) | The `OnFrameEditor` postfix that calls it |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs) | Per-frame entry, window, projection |
| [src/KSArmory/Ksa/Markers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Markers.cs) | Markers, edge clamp, separate label windows |

## Variations and gotchas

- **Two mods, one id.** KSA_AircraftHUD and OhMyStars both begin a window named `HUDFullscreenWindow`. ImGui identifies windows by name, so with both installed they would share one window (an inference from the names; not tested).
  Use a unique `##` id.
- **Position at the viewport, not at (0, 0).** AFC places its window at `(0, 0)`; with the main window at the desktop origin that equals `viewport.Pos`, but `SetNextWindowPos(viewport.Pos)` is the safe form.
- **Layout from the viewport size once is stale** after a resize; AircraftHUD computes it at `StarMapImmediateLoad` and never recomputes.
- **Frame cost.** A full-screen window with many vertices costs ImGui time every frame; cache label strings and use `stackalloc` buffers.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-a-fullscreen-click-through-overlay](overview.md)
- [Approach 2: ImGuiHelper.GetOverlayDrawList (no window)](draw-a-fullscreen-click-through-overlay-2.md)
- [Approach 3: ImGui.GetForegroundDrawList anchored to WorkPos and WorkSize](draw-a-fullscreen-click-through-overlay-3.md)
- [Approach 5: per-surface draw list and per-viewport ego transforms](draw-a-fullscreen-click-through-overlay-5.md)
- [Keep mod UI running when the HUD is hidden](../draw-ui-while-the-hud-is-hidden/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../../../camera/secondary-viewports/borrow-a-secondary-viewport/overview.md)
- [Draw markers, brackets and measurement annotations on world objects](../../../rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md)
- Mod features: [Fullscreen draw-list HUD](../../../../mods/ksa-aircrafthud/features/fullscreen-drawlist-hud.md),
  [Sky overlay lines and labels](../../../../mods/ohmystars/features/sky-overlay-lines-and-labels.md),
  [Body pointer overlay](../../../../mods/compendium/features/body-pointer-overlay.md),
  [World-anchored screen markers](../../../../mods/ksarmory/features/world-anchored-screen-markers.md),
  [Guidance panel and overlays](../../../../mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays.md)

[^brutal-imguiapi-imguiwindowflags]: Brutal.ImGuiApi.ImGuiWindowFlags (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
