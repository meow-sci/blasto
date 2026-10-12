---
type: Mod Feature
title: Pointer lines to the selected body
description: A full-viewport, input-transparent ImGui window draws one or two lines from the nearest corners of the Compendium window to the selected body's screen position, or to the screen edge in its direction when it is off screen, using the main camera's ego-to-screen and ego-to-clip projections
mod: compendium
category: ui
techniques: [imgui-drawlist, imgui-window, world-to-screen]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.Camera, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiViewportPtr]
tags: [imgui, drawlist, overlay, camera, projection, world-to-screen]
---

With "Pointer" on, a thin line runs from the Compendium window to the body currently shown in the
side pane, wherever it is on screen, so the player can find it in the scene. Options: one line or two
(from the two nearest window corners), a fixed color from a color picker, or a rainbow that cycles
every four seconds. When the body is behind the camera or outside the view, the line points to the
screen edge in its direction. The options are saved in `settings.ini`.[^cmp-cdmsettings]

## How it works

1. **Window rect from the previous frame.** Inside the Compendium window, `CaptureWindowRectForPointer`
   stores `GetWindowPos`/`GetWindowSize` and sets a visible flag. The draw hook calls `DrawBodyPointer`
   first thing, before the window itself, and then clears the flag, so the pointer only draws if the
   window was drawn on the previous frame and uses that frame's rect
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L57-L61), [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L137),
   [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L27-L32)).[^cmp-compendiummain] [^cmp-cdmbodypointer]
2. **Target.** Only on a body or star page; the target is resolved once per selection and cached
   ([src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L34-L49)).
3. **Project.** `Program.GetMainCamera()` gives the main viewport's camera and
   `camera.GetPositionEgo(target)` the camera-relative position. If `camera.IsPointWithinFov(pos)`, the
   screen point is `camera.EgoToScreen(pos) + viewport.Pos`. Otherwise it takes `camera.EgoToClip(pos)`,
   divides x and y by w (flipping the sign when w is negative, that is behind the camera), scales the
   pair so the larger component is 1, and maps it to the viewport rectangle: a point on the screen edge
   in the target's direction ([src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L169-L207), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634),
   [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L286), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355),
   [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L540)).[^ksa-program] [^ksa-camera]
4. **Overlay window.** It sets the next window's position, size and viewport to the main ImGui
   viewport and begins `##CompendiumPointerOverlay` with `NoTitleBar | NoResize | NoMove | NoCollapse |
   NoBringToFrontOnFocus | NoBackground | NoFocusOnAppearing | NoInputs | NoNavFocus | NoSavedSettings`,
   then draws with `GetWindowDrawList().AddLine` from the nearest (and optionally second-nearest) window
   corner to the target ([src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L127-L167), [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L209-L221),
   [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5753), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8028)).[^ksa-brutal-imguiapi-imgui]
5. **Rainbow.** `ImGui.GetTime()` divided by the cycle length gives a hue; six linear wedges produce the
   RGB ([src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L239-L256)).
6. **Toggle and settings.** The Pointer button sits under the top row, right-aligned to the D button
   whose edge was recorded on the same frame; the settings live in the font-settings panel and call
   `SaveSettings` on each change ([src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L56-L125)).

## Lifecycle and timing

- **`frame:draw-ui`.** Everything runs inside `[StarMapAfterGui]`, StarMap's postfix on
  `Program.OnDrawUiViewports`. In `Program.OnFrame` that is after `OnFrameViewports`, where cameras are
  updated for the frame ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2268)), so the projection matches the image that
  will be rendered this frame. Body positions were refreshed at the frame's solver apply.
- **One frame of lag for the anchor.** The window rect is from the previous frame, which is invisible
  unless the window is being dragged.

## Key code

| Path | Role |
|---|---|
| [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L127-L167) | Overlay window and lines |
| [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L169-L207) | On-screen point or screen-edge point |
| [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L209-L256) | Nearest corners, color conversion, rainbow |
| [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs#L56-L125) | Toggle and settings UI |

## Snippets

```csharp
// src/CdmBodyPointer.cs L134-L166
Astronomical? target = GetPointerTarget();
Camera camera = Program.GetMainCamera();
ImGuiViewportPtr viewport = ImGui.GetMainViewport();
if (target == null || camera == null || viewport.IsNull())
{
    return;
}

if (!TryGetPointerScreenTarget(camera, viewport, camera.GetPositionEgo(target), out float2 targetScreen))
{
    return;
}

ImGui.SetNextWindowPos(viewport.Pos);
ImGui.SetNextWindowSize(viewport.Size);
ImGui.SetNextWindowViewport(viewport.ID);
ImGuiWindowFlags flags =
    ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground |
    ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNavFocus |
    ImGuiWindowFlags.NoSavedSettings;
if (ImGui.Begin("##CompendiumPointerOverlay", flags))
{
    ImDrawListPtr drawList = ImGui.GetWindowDrawList();
    ImColor8 lineColor = ToLineColor(pointerRainbow ? GetRainbowPointerColor() : pointerColor);
    GetTwoNearestWindowCorners(pointerWindowPosition, pointerWindowSize, targetScreen, out float2 nearest, out float2 secondNearest);
    drawList.AddLine(nearest, targetScreen, lineColor, 1.5f);
    if (pointerDualLines)
    {
        drawList.AddLine(secondNearest, targetScreen, lineColor, 1.5f);
    }
}
ImGui.End();
```

```csharp
// src/CdmBodyPointer.cs L169-L207
// On-screen position of the target, or the point on the screen edge in its direction when off-view.
private static bool TryGetPointerScreenTarget(Camera camera, ImGuiViewportPtr viewport, double3 positionEgo, out float2 target)
{
    if (camera.IsPointWithinFov(positionEgo))
    {
        target = camera.EgoToScreen(positionEgo) + viewport.Pos;
        return true;
    }

    float4 clip = camera.EgoToClip(positionEgo);
    if (MathF.Abs(clip.W) < float.Epsilon)
    {
        target = default;
        return false;
    }

    float x = clip.X / clip.W;
    float y = clip.Y / clip.W;
    if (clip.W < 0f)
    {
        x = -x;
        y = -y;
    }
    if (MathF.Abs(x) < float.Epsilon && MathF.Abs(y) < float.Epsilon)
    {
        y = -1f;
    }

    float maximum = MathF.Max(MathF.Abs(x), MathF.Abs(y));
    if (maximum < float.Epsilon)
    {
        target = default;
        return false;
    }

    float2 center = viewport.Pos + viewport.Size * 0.5f;
    target = center + new float2(x / maximum * viewport.Size.X * 0.5f, y / maximum * viewport.Size.Y * 0.5f);
    return true;
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.GetMainCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) | Camera of the main viewport |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236) | Camera-relative target position |
| `Camera.IsPointWithinFov` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L540) | On-screen test |
| `Camera.EgoToScreen`, `EgoToClip` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L355) | Projection |
| `ImGui.GetMainViewport`, `SetNextWindowViewport`, `GetWindowDrawList`, `GetTime` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8028) | Overlay window |

## Pitfalls and version notes

- **A window, not the foreground list.** Because the lines live in a normal ImGui window, other windows
  can draw over them; `ImGui.GetForegroundDrawList()` would put them above everything.
- **`EgoToScreen` returns NaN behind the camera** when `ignoreBehind` is true (the default), which is
  why the code only calls it after the field-of-view test ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355)).
- **Main viewport only.** The projection uses the main camera; secondary viewports are ignored.

## See also

- [Celestial compendium window](celestial-info-window.md)
- [settings.ini persistence](settings-ini-persistence.md)
- [Previous-frame window rectangle plus edge-clamped projection (body target)](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-4.md) - how-to article demonstrating this feature
- [Clip-space edge clamp for off-screen and behind-camera targets](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-4.md) - how-to article demonstrating this feature
- [Full-screen overlay: a NoInputs, NoBackground window sized to the main viewport](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-1.md) - how-to article demonstrating this feature

[^cmp-cdmsettings]: Compendium: src/CdmSettings.cs
[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-cdmbodypointer]: Compendium: src/CdmBodyPointer.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
