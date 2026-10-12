---
type: How-To
title: "Pick objects under the cursor: ray hit to texture UV for interactive in-world UI"
description: Turn a cursor ray hit on a world quad into texture coordinates and then widget cells so an in-world panel can be clicked and typed into
category: ui
group: input
topic: pick-objects-under-the-cursor
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/ray-pick-focus-for-world-quads]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Cursor, KSA.Ray, KSA.Camera, KSA.Program]
tags: [picking, quad, uv, in-world-ui, focus, terminal]
---
purrTTY can draw a terminal on a quad in the 3D view, anchored to a part like a touch screen in a cockpit. This article
covers the last mile of making such a panel interactive: from a hit on the quad to a position inside the panel's own
coordinate space, so the program inside receives clicks, drags and wheel events at the cell under the cursor. The
ray, quad test and click-to-focus loop are in
[cursor to world ray, approach 4](../cursor-to-world-ray/cursor-to-world-ray-4.md); this article is what happens after
the hit. Demonstrated by purrTTY.

## Background: how the game does it

The engine gives you the ray and the triangle test; everything after that is the mod's job.

- `Cursor.GetEgoRay(viewport)` returns the ray in the camera-relative ego frame.[current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27-L30)[^ksa-cursor]
- `Ray.RaycastMollerTrumbore(v0, v1, v2, out t)` returns the distance along the ray to a triangle, double-sided, with `t`
  strictly positive.[current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L67-L98)[^ksa-ray]
- Nothing in KSA maps a hit to a texture coordinate for a mod-drawn quad. The mod knows its own corner layout, so it solves
  the barycentric-style projection itself.

## Lifecycle and timing

Everything runs in `InWorldTerminalManager.OnAfterGui` from the mod's `[StarMapAfterGui]` method (`frame:draw-ui`):
`Program.PrepareImGui` stored the mouse with `Cursor.SetDesktopPosition` right after `ImGui.NewFrame`, and the cameras were
updated in `frame:viewports`, so the ray matches the picture. Picking and input processing happen before the instances
render their off-screen frames in the same call; a click's effect shows on the next texture update. The game-input gates that
read the focus (blocking hotkeys while a terminal owns the keyboard) run during `Glfw.PollEvents` of the following frame
(`frame:input`). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Test both triangles of the quad and keep the nearer `t`; the corners are in the same ego-space model used for drawing:

```csharp
// purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L187-L215
float3 v0Local = new float3(-0.5f, -0.5f, 0f);
float3 v1Local = new float3( 0.5f, -0.5f, 0f);
float3 v2Local = new float3( 0.5f,  0.5f, 0f);
float3 v3Local = new float3(-0.5f,  0.5f, 0f);

double3 v0 = ToDouble3(float3.Transform(v0Local, modelEgo));
double3 v1 = ToDouble3(float3.Transform(v1Local, modelEgo));
double3 v2 = ToDouble3(float3.Transform(v2Local, modelEgo));
double3 v3 = ToDouble3(float3.Transform(v3Local, modelEgo));

bool any = false;
if (ray.RaycastMollerTrumbore(v0, v1, v2, out double t0))
{
    t = t0;
    any = true;
}
if (ray.RaycastMollerTrumbore(v0, v2, v3, out double t1))
{
    if (!any || t1 < t)
    {
        t = t1;
    }
    any = true;
}

if (!any)
{
    return false;
}
```

2. Compute the UV of the hit by projecting the hit point onto the quad's two edge vectors. `v0` is local `(-0.5, -0.5)` with
   `uv (0, 1)`; the +X edge runs `u` 0 to 1 and the +Y edge runs `v` 1 to 0 (V flipped to match the texture):

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

3. Map UV to texture pixels and then to a cell, using the same fractional metrics the grid was drawn with so the cell matches
   the glyph under the cursor. Then synthesize the cell centre in **integer** metrics before calling the mouse encoder, so the
   engine's integer pixel-to-cell division recovers the same cell:

```csharp
// purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs L492-L522
int cols = session.Surface.Cols;
int rows = session.Surface.Rows;
if (cols <= 0 || rows <= 0 || _gridPixelWidth <= 0f || _gridPixelHeight <= 0f)
{
    return;
}

int cellW = Math.Max(1, (int)_cellWidth);
int cellH = Math.Max(1, (int)_cellHeight);

bool over = hitUv.HasValue;
int col = _appMouseCol < 0 ? 0 : _appMouseCol;
int row = _appMouseRow < 0 ? 0 : _appMouseRow;
if (hitUv is { } uvHit)
{
    // UV → texture pixel → cell, using the same fractional metrics the grid
    // was drawn with so the cell matches the glyph under the cursor.
    float px = uvHit.X * _gridPixelWidth;
    float py = uvHit.Y * _gridPixelHeight;
    col = Math.Clamp((int)(px / _cellWidth), 0, cols - 1);
    row = Math.Clamp((int)(py / _cellHeight), 0, rows - 1);
}

bool cellChanged = col != _appMouseCol || row != _appMouseRow;
_appMouseCol = col;
_appMouseRow = row;

var mods = TerminalInputEncoder.ReadModifiers(io);
// Synthesize the cell-center in INTEGER metrics so the engine's integer
// pixel→cell division recovers the same cell (gotcha 11).
var pos = new float2(col * cellW + cellW * 0.5f, row * cellH + cellH * 0.5f);
```

4. Forward events only when the program has enabled mouse tracking. A press requires a hit; a release is sent only when the
   matching press was forwarded (so a drag can leave the quad); motion is sent on cell change; the wheel is sent as
   scroll-button presses, one per notch:

```csharp
// purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs L524-L576
// Press is gated on being over the quad; release fires ungated (but only
// when the matching press was forwarded), mirroring TerminalWindow.
if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
{
    _appMousePressSent[(int)MouseButton.Left] = true;
    EncodeMouseAndSend(session, MouseAction.Press, MouseButton.Left, mods, pos);
}
else if (_appMousePressSent[(int)MouseButton.Left] && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
{
    _appMousePressSent[(int)MouseButton.Left] = false;
    EncodeMouseAndSend(session, MouseAction.Release, MouseButton.Left, mods, pos);
}
else if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
{
    _appMousePressSent[(int)MouseButton.Right] = true;
    EncodeMouseAndSend(session, MouseAction.Press, MouseButton.Right, mods, pos);
}
else if (_appMousePressSent[(int)MouseButton.Right] && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
{
    _appMousePressSent[(int)MouseButton.Right] = false;
    EncodeMouseAndSend(session, MouseAction.Release, MouseButton.Right, mods, pos);
}
else if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
{
    _appMousePressSent[(int)MouseButton.Middle] = true;
    EncodeMouseAndSend(session, MouseAction.Press, MouseButton.Middle, mods, pos);
}
else if (_appMousePressSent[(int)MouseButton.Middle] && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
{
    _appMousePressSent[(int)MouseButton.Middle] = false;
    EncodeMouseAndSend(session, MouseAction.Release, MouseButton.Middle, mods, pos);
}
else if (cellChanged)
{
    // Drag / hover motion: the engine's encoder is mode-aware, so just offer
    // each cell crossing (a held button = drag, over-quad = any-event hover).
    var held = HeldMouseButton();
    if (held != MouseButton.None || over)
    {
        EncodeMouseAndSend(session, MouseAction.Motion, held, mods, pos);
    }
}

// Wheel reports as scroll-button presses, one per notch.
if (over && io.MouseWheel != 0f)
{
    var button = io.MouseWheel > 0f ? MouseButton.ScrollUp : MouseButton.ScrollDown;
    int notches = Math.Clamp((int)Math.Round(Math.Abs(io.MouseWheel)), 1, 10);
    for (int i = 0; i < notches; i++)
    {
        EncodeMouseAndSend(session, MouseAction.Press, button, mods, pos);
    }
}
```

5. Focus arbitration: the nearest hit wins focus, a click that hits nothing clears it unless ImGui wants the mouse, a focused
   2D window beats an in-world quad, and the focus state feeds the patches that block game hotkeys (see
   [block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L187-L232) | Triangle test and UV |
| [purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs#L478-L577) | UV to cell and app-mouse encoding |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L282-L309) | Per-frame picker, keyboard and mouse |

## Variations and gotchas

- **Test in the space you draw in.** Billboards are drawn in view space and are converted to ego space with the inverse view
  rotation (`VPInv.view`) before testing.
- **Billboards are not click-pickable by default**, because a screen-fixed panel would steal clicks meant for the world;
  `BillboardClickToFocus` opts in.
- **Integer cell centres.** Feeding fractional pixel positions to the encoder can land in the neighbouring cell after its
  integer division.
- **Not consumed from the game.** KSA's own click and camera handling still sees the button (inferred from the input chain,
  not observed in game).
- **Version.** KSA 5402 removed the per-frame `Cursor.InputRay`; use `Cursor.GetEgoRay(viewport)`. Checked against 2026.10.10.5554.

## See also

- [Overview of pick-objects-under-the-cursor](overview.md)
- [Cursor to world ray, approach 4: Cursor.GetEgoRay](../cursor-to-world-ray/cursor-to-world-ray-4.md)
- [Block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)
- Mod feature: [Ray pick and focus for world quads](../../../../mods/purrtty/features/ray-pick-focus-for-world-quads.md)

[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
