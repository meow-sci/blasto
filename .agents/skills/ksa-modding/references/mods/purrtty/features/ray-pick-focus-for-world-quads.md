---
type: Mod Feature
title: Click-to-focus and input routing for world-space panels
description: Pick the nearest world-space quad under the mouse with Cursor.GetEgoRay and Moller-Trumbore triangle tests, convert the hit to texture UV and terminal cells, and route keyboard and app-mouse input to that panel's shell while gating game input
mod: purrtty
category: ui
techniques: [harmony-prefix, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnKey, harmony-prefix KSA.GameSettings.OnKeyAll]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Cursor, KSA.Ray, KSA.Camera, KSA.Program, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiIOPtr]
tags: [picking, raycast, input, focus, in-world, mouse, keyboard]
---

A part-anchored terminal is focused by clicking on it in the 3D view, like a touch screen in the
cockpit; the nearest panel under the mouse wins. While focused, typing goes to that panel's shell,
game hotkeys are suppressed, and mouse-aware programs (vim, htop, tmux) receive clicks, drags and
wheel at the cell under the cursor. Screen billboards are focused from the manager list by default
and can opt into click focus.

## How it works

**Picking** (`InWorldTerminalManager.TickPicker`, on the frame the left button is clicked):

1. `Ray ray = Cursor.GetEgoRay(Program.MainViewport)`, a ray from the main camera through the mouse
   in ego space (KSA computes it on demand with `viewport.GetCamera().ScreenToEgoRay(...)`).[^ksa-cursor]
2. For each pickable instance (part mode, or billboard with `BillboardClickToFocus`),
   `InWorldQuad.TryRaycast` transforms the four unit-quad corners to ego space with the same model
   used for drawing (billboards via `model * camera.VPInv.view`) and runs
   `ray.RaycastMollerTrumbore(v0, v1, v2, out t)` on both triangles.[^inworld-quad][^ksa-ray]
3. The smallest `t` wins focus. A click that hits nothing clears focus unless ImGui wants the mouse
   (a click on a window should not unfocus).[^inworld-manager]

**Hit to cell.** The hit point is projected onto the quad's edge vectors to get `uv` (V flipped to
match the texture). `InWorldTerminalRenderer.ProcessMouse(hitUv, io)` maps `uv` to texture pixels
and then to a cell using the fractional cell size, and synthesizes the cell center in integer metrics
before calling the engine's mouse encoder, exactly as 2D windows do. It only acts when the program
enabled mouse tracking; presses require a hit, releases are sent only for forwarded presses, motion
only on cell change, wheel as scroll buttons.[^inworld-renderer]

**Keyboard.** For the focused instance, while no ImGui text field wants input, the coordinator calls
the shared `TerminalInputEncoder.ProcessKeyboard(session, io)` on the main context's IO, the same
encoder 2D windows use.[^input-encoder]

**Focus arbitration and game input.**

- A focused 2D window wins: if `GhosttyTerminalController.IsAnyTerminalActive`, in-world focus is
  cleared.
- `InWorldTerminalManager.IsInputFocused` feeds `Patch01.TerminalOwnsKeyboard` (gates
  `Program.OnKey` and mouse-bound actions) and `Patch03_HotkeyGuard` (blocks
  `GameSettings.OnKeyAll`), so typing never flies the rocket.[^patcher]
- The toggle hotkey is ignored while an in-world terminal is focused, so the key reaches the shell.[^terminal-mod]
- Each instance's `HasFocus` drives its cursor (solid when focused, hollow otherwise).

## Lifecycle and timing

- All of this runs in `InWorldTerminalManager.OnAfterGui`, called from `TerminalMod.OnAfterUi`
  (`[StarMapAfterGui]`, `frame:draw-ui`). `Program.PrepareImGui` stores the mouse position with
  `Cursor.SetDesktopPosition(ImGui.GetIO().MousePos)` right after `ImGui.NewFrame`, and the cameras
  were updated in `frame:viewports`, so the ray matches what the player sees this frame.[^ksa-program]
- Picking and input processing happen before the instances render their off-screen frames in the
  same call, so a click's effect can show on the next texture update.
- The game-input gate prefixes run during `Glfw.PollEvents` of the following frame (`frame:input`)
  and read the focus state set here.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L252-L335) | Per-frame focus, keyboard, mouse, render |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L345-L383) | `TickPicker` and the pickability rule |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L181-L233) | Ray-quad test and UV |
| [purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs#L478-L600) | UV to cell and app-mouse encoding |
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L171-L172) | `TerminalOwnsKeyboard` includes in-world focus |

## Snippets

```csharp
// purrTTY.GameMod/InWorld/InWorldTerminalManager.cs L345-L378
    private void TickPicker()
    {
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            return;
        }

        InWorldTerminalInstance? nearest = null;
        double nearestT = double.MaxValue;
        Ray inputRay = Cursor.GetEgoRay(Program.MainViewport);
        for (int i = 0; i < _instances.Count; i++)
        {
            var instance = _instances[i];
            if (instance.IsFailed || !IsClickPickable(instance))
            {
                continue;
            }

            if (instance.TryRaycast(inputRay, out double t, out _) && t < nearestT)
            {
                nearestT = t;
                nearest = instance;
            }
        }

        if (nearest != null)
        {
            _focused = nearest;
        }
        else if (!ImGui.GetIO().WantCaptureMouse)
        {
            _focused = null;
        }
    }
```

```csharp
// purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L217-L232
        // Texture UV of the hit: project the hit point onto the quad's two edge
        // vectors. v0 = local(-0.5,-0.5) with uv(0,1); the +X edge (v0→v1) runs
        // u 0→1, the +Y edge (v0→v3) runs v 1→0 (V flipped to match the texture).
        double3 hit = new double3(
            ray.Origin.X + t * ray.Direction.X,
            ray.Origin.Y + t * ray.Direction.Y,
            ray.Origin.Z + t * ray.Direction.Z);
        double3 ex = v1 - v0;
        double3 ey = v3 - v0;
        double3 d = hit - v0;
        double s = double3.Dot(d, ex) / double3.Dot(ex, ex);
        double w = double3.Dot(d, ey) / double3.Dot(ey, ey);
        uv = new float2(
            (float)Math.Clamp(s, 0.0, 1.0),
            (float)Math.Clamp(1.0 - w, 0.0, 1.0));
        return true;
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Cursor.GetEgoRay(IViewport)` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27-L30) | Mouse ray in ego space |
| `Ray.RaycastMollerTrumbore(double3, double3, double3, out double)` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L67) | Ray-triangle test |
| `Program.PrepareImGui` (`Cursor.SetDesktopPosition`) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139) | When the mouse position for the ray is captured |
| `ImGui.IsMouseClicked`, `GetIO().WantCaptureMouse`, `WantTextInput` | `Brutal.ImGuiApi.ImGui` | Click edge and UI ownership |

## Pitfalls and version notes

- KSA 5402 removed the per-frame `Cursor.InputRay`; use `Cursor.GetEgoRay(viewport)`, passing the
  viewport whose camera the quads are drawn with (purrTTY uses `Program.MainViewport`).
- Test in the same space you draw in. Billboards are drawn in view space and must be converted to
  ego space for picking.
- Clicking a quad is not consumed from the game: KSA's own click and camera handling still sees the
  button (only mouse-bound game actions are gated while a terminal owns the keyboard). Inferred from
  the input chain, not observed in game.
- Billboards are not click-pickable by default because a screen-fixed panel would steal clicks meant
  for the world; `BillboardClickToFocus` opts in (docs/how-to.md).[^how-to]

## See also

- [Anchoring a world-space quad to a part or the camera](./anchor-quad-to-part-or-camera.md)
- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [ImGui keyboard and mouse to terminal input](./imgui-keyboard-mouse-to-vt-input.md)
- [Render-to-texture with a second ImGui context](./offscreen-imgui-context-render-to-texture.md)
- [purrTTY overview](../overview.md)
- [ImGui rendered to a texture and drawn as a world-space quad](../../../howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-3.md) - how-to article demonstrating this feature
- [Cursor to world ray: Cursor.GetEgoRay and Moller-Trumbore tests against in-world quads](../../../howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-4.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: ray hit to texture UV for interactive in-world UI](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-3.md) - how-to article demonstrating this feature

[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
[^inworld-quad]: purrTTY.GameMod/InWorld/Display/InWorldQuad.cs
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^inworld-manager]: purrTTY.GameMod/InWorld/InWorldTerminalManager.cs
[^inworld-renderer]: purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs
[^input-encoder]: purrTTY.Display/Ghostty/TerminalInputEncoder.cs
[^patcher]: purrTTY.GameMod/Patcher.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^how-to]: purrTTY docs/how-to.md
