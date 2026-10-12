---
type: Mod Feature
title: Star pointer
description: Draw a line from the mod window to the selected star, pushing off-screen and behind-camera targets to the screen edge through the clip-space direction
mod: ohmystars
category: ui
techniques: [imgui-drawlist, offscreen-indicator]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, Brutal.ImGuiApi.ImGui]
tags: [sky, pointer, imgui, drawlist, offscreen-indicator, camera]
---

With "Star Pointer" switched on in the Information tab, a line runs from the Oh My Stars window to
the selected star on the sky. When the star is off screen, or even behind the camera, the line
ends at the screen edge in the star's direction, so the player can follow it. Options: one line
from the nearest window corner or two from the two nearest corners, a fixed colour, or a "rainbow"
that cycles hue every 4 s.[^oms-stellariumrenderer-cs]

## How it works

1. **Inputs.** `OhMyStarsWindow.TryGetStarPointer` returns the selected star, the window position
   and size captured with `ImGui.GetWindowPos/GetWindowSize` while the window was being drawn
   earlier in the frame, and whether the pointer is on and the window shown
   ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1301-L1306), [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L269-L270)).[^oms-ohmystarswindow-cs]
2. **Target in ego space.** A catalog star is `ApplyAlignment(direction) * 19.5`; the central star
   (Sol) is `camera.GetPositionEgo(centralStarBody)`
   ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L116-L148)).
3. **On-screen case.** If `camera.IsPointWithinFov(position)`, the target is the projected point.
4. **Off-screen case: push to the edge.** Otherwise the mod takes `camera.EgoToClip(position)`,
   divides by `W`, flips the sign when `W < 0` (behind the camera the perspective divide mirrors
   the direction), and scales the 2D vector so its larger component is 1. That point on the NDC
   square is mapped to the viewport rectangle around its centre
   ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L150-L185), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L286)).[^ksa-camera-cs]
   A star exactly behind the camera (`x = y = 0`) is sent to `y = -1`, which `NdcToScreen`-style
   mapping (`(ndc + 1) / 2 * size`) puts on the top edge of the screen.
5. **Anchor and draw.** The nearest window corner (or the two nearest) is chosen by squared
   distance and `ImDrawListExtensions.AddLine(..., 1.5f)` draws into the same overlay draw list as
   the constellations ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L187-L234)).
6. **Rainbow.** Hue is `frac(ImGui.GetTime() / 4)`, converted with a six-sector HSV ramp at full
   saturation and value ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L236-L254)).

## Lifecycle and timing

Drawn by `StellariumRenderer.Draw` from `[StarMapAfterGui]` (`frame:draw-ui`). The window geometry
it anchors to was recorded by the window code in `[StarMapBeforeGui]` (prefix on
`Program.OnDrawUiFrame`) in the same frame, so the line follows a dragged window without lag
([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L36)).[^starmap-patches-programpatcher-cs] The
pointer vanishes with the rest of the HUD when `Program.DrawUI` is false.

## Key code

| Path | Role |
|---|---|
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L116-L148) | Target selection and line drawing |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L150-L185) | Off-screen edge projection from clip space |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L187-L254) | Corner choice and rainbow colour |

## Snippets

```csharp
// src/StellariumRenderer.cs L150-L185
    private static bool TryGetStarPointerTarget(Camera camera, double3 position, out float2 target) {
        if(camera.IsPointWithinFov(position)) {
            target = EgoToOverlayScreen(camera, position);
            return true;
        }

        float4 clip = camera.EgoToClip(position);
        if(System.MathF.Abs(clip.W) < float.Epsilon) {
            target = default;
            return false;
        }

        float x = clip.X / clip.W;
        float y = clip.Y / clip.W;
        if(clip.W < 0f) {
            x = -x;
            y = -y;
        }

        if(System.MathF.Abs(x) < float.Epsilon && System.MathF.Abs(y) < float.Epsilon) {
            y = -1f;
        }

        float maximum = System.MathF.Max(System.MathF.Abs(x), System.MathF.Abs(y));
        ImGuiViewport* viewport = ImGui.GetMainViewport();
        if(viewport == null || maximum < float.Epsilon) {
            target = default;
            return false;
        }

        float2 center = viewport->Pos + viewport->Size * 0.5f;
        target = center + new float2(
            x / maximum * viewport->Size.X * 0.5f,
            y / maximum * viewport->Size.Y * 0.5f);
        return true;
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Camera.IsPointWithinFov(double3)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L545) | On-screen test |
| `Camera.EgoToClip(double3)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L286) | Direction for off-screen targets |
| `Camera.EgoToScreen(double3, bool)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355) | On-screen target pixels |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L235-L250) | Central star position |
| `ImGui.GetMainViewport`, `GetTime` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8028-L8030) | Viewport rectangle, animation clock |

## Pitfalls and version notes

- **Edge point is exact in front, approximate behind.** For a target in front of the camera the
  NDC-to-pixel mapping is affine about the screen centre, so the edge point lies exactly on the
  projected direction. For a target behind the camera the sign-flipped clip direction is only a
  "turn this way" hint.
- **Occlusion is ignored.** Unlike the constellation layers, the pointer is drawn even when a planet
  is in the way, which is what a pointer should do.
- **No window, no pointer.** `TryGetStarPointer` returns false when the Oh My Stars window is
  hidden.

## See also

- [AT-HYG star catalog and star information](hyg-star-catalog.md)
- [Constellation lines and labels on the sky](sky-overlay-lines-and-labels.md)
- [Navball star marker](navball-star-marker.md)
- [Same technique for a catalog star on the sky sphere](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-5.md) - how-to article demonstrating this feature
- [Clip-space edge clamp for off-screen and behind-camera targets](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-4.md) - how-to article demonstrating this feature

[^oms-stellariumrenderer-cs]: OhMyStars src/StellariumRenderer.cs
[^oms-ohmystarswindow-cs]: OhMyStars src/OhMyStarsWindow.cs
[^ksa-camera-cs]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^starmap-patches-programpatcher-cs]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
