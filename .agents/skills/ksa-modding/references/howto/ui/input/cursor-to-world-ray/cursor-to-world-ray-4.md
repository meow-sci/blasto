---
type: How-To
title: "Cursor to world ray: Cursor.GetEgoRay and Moller-Trumbore tests against in-world quads"
description: Use the game's own Cursor.GetEgoRay helper and Ray.RaycastMollerTrumbore to find which camera-relative quad the mouse is over
category: ui
group: input
topic: cursor-to-world-ray
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/ray-pick-focus-for-world-quads]
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Cursor, KSA.Ray, KSA.Camera, KSA.IViewport, KSA.Program, KSA.ViewportRegistry]
tags: [cursor, raycast, ego, moller-trumbore, quad, in-world-ui]
---
KSA ships a one-line helper that turns the cursor into an ego-space ray for a viewport: `Cursor.GetEgoRay(viewport)`.
purrTTY uses it to click on terminals drawn as quads in the 3D view: build the quad's four corners in the same
camera-relative frame, test two triangles with the engine's `Ray.RaycastMollerTrumbore`, keep the nearest hit.
This article is the minimal way to get a ray without writing any of the viewport math yourself; turning the hit
into texture coordinates is covered in
[pick objects under the cursor](../pick-objects-under-the-cursor/overview.md). Demonstrated by purrTTY.

## Background: how the game does it

- `Cursor.DesktopPosition` is set by `Program.PrepareImGui` from `ImGui.GetIO().MousePos` right after `ImGui.NewFrame`
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2139)[^ksa-program].
- The helper subtracts the viewport position and unprojects with that viewport's camera:

```csharp
// current/decomp/KSA/Cursor.cs L17-L30
public static float2 ToViewportLocal(IViewport inViewport, float2 inDesktopPosition)
{
    return inDesktopPosition - inViewport.Position;
}

public static float2 GetPosition(IViewport inViewport)
{
    return ToViewportLocal(inViewport, _desktopPosition);
}

public static Ray GetEgoRay(IViewport inViewport)
{
    return inViewport.GetCamera().ScreenToEgoRay(GetPosition(inViewport));
}
```

- `ScreenToEgoRay` returns a perspective ray with origin `(0, 0, 0)` in the camera-relative ego frame [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L713)[^ksa-camera];
  a quad to be tested has to be expressed in ego space too.
- `Ray.RaycastMollerTrumbore(v0, v1, v2, out t)` is double-sided (it only rejects a near-zero determinant), and accepts
  only positive `t` [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L67-L98)[^ksa-ray].
- The older per-frame `Cursor.InputRay` no longer exists; purrTTY's comment records it was dropped in KSA 5402.

## Lifecycle and timing

Picking runs in `InWorldTerminalManager.OnAfterGui`, called from the mod's `[StarMapAfterGui]` method (`frame:draw-ui`):

```csharp
// purrTTY.GameMod/TerminalMod.cs L68-L69
[StarMapAfterGui]
public void OnAfterUi(double dt)
```
```csharp
// purrTTY.GameMod/TerminalMod.cs L115-L117
// In-world render-to-texture loop (prototype; gated by PURRTTY_INWORLD).
// No-op until the renderer/quad subsystem has been initialized.
_inWorld?.OnAfterGui(dt);
```

`PrepareImGui` stored the mouse position this frame and the cameras were updated in `frame:viewports`, so the ray
matches what the player sees. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The picker runs
before the instances render their off-screen frames in the same call, so the effect of a click shows on the next texture
update. The game-input gates that read the resulting focus run during `Glfw.PollEvents` of the following frame
(`frame:input`).

## Steps

1. On the frame the left button is clicked, get the ray once and test every pickable quad. The nearest `t` wins and a
   click that hits nothing clears focus unless ImGui wants the mouse:

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

2. Write the quad test in ego space. Billboards are drawn in view space, so the model is converted with the inverse view
   rotation (`VPInv.view`) to the same space as the ray; part-anchored quads are already in ego space:

```csharp
// purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L181-L215
public bool TryRaycast(Ray ray, out double t, out float2 uv)
{
    t = double.MaxValue;
    uv = default;
    if (!TryComputeEgoModel(out float4x4 modelEgo)) return false;

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

3. Choose which quads participate. Part-anchored quads always do; a screen-fixed billboard would steal clicks meant for
   the world, so it must opt in:

```csharp
// purrTTY.GameMod/InWorld/InWorldTerminalManager.cs L380-L383
// Whether an instance participates in ego-space ray picking (click-to-focus +
// app-mouse forwarding): part mode always; a billboard only when it opted in.
private static bool IsClickPickable(InWorldTerminalInstance instance)
    => !instance.IsBillboard || instance.Record.BillboardClickToFocus;
```

4. Pass the viewport whose camera draws the quads. purrTTY draws with the main camera, so it passes
   `Program.MainViewport`; a mod that draws into another viewport must pass that one.

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L345-L378) | `TickPicker` |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L181-L215) | `TryRaycast` (two triangles) |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L68-L117) | The StarMap hook that drives the picker |

## Variations and gotchas

- **Test in the space you draw in.** Billboards live in view space and must be converted to ego space before testing.
- **Use the right viewport.** `GetEgoRay` takes a viewport; a quad rendered through another camera is not hit by the main
  viewport's ray.
- **No framebuffer scale.** `Cursor.GetEgoRay` does not scale to `FramebufferSize`; that is correct today because viewport
  and framebuffer sizes are equal (see [approach 3](cursor-to-world-ray-3.md)). It also does not decide which viewport the
  cursor is over, so the caller must.
- **Clicking a quad is not consumed from the game.** KSA's own click and camera handling still sees the button; only
  mouse-bound game actions are gated while a terminal owns the keyboard (inferred from the input chain, not observed in game).
- **Version.** Built for builds after 5402; checked against 2026.10.10.5554.

## See also

- [Overview of cursor-to-world-ray](overview.md)
- [Approach 1: ScreenToEgoRay with viewport offset and framebuffer scale](cursor-to-world-ray-1.md)
- [Approach 5: ego frame and construction plane](cursor-to-world-ray-5.md)
- Mod feature: [Ray pick and focus for world quads](../../../../mods/purrtty/features/ray-pick-focus-for-world-quads.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
