---
type: How-To
title: ImGui rendered to a texture and drawn as a world-space quad
description: Draw the off-screen ImGui texture on a depth-tested premultiplied-alpha quad anchored to a part or the camera, and focus it by ray-picking so the UI is fully interactive in the world
category: rendering
group: textures-and-decals
topic: render-imgui-into-a-texture
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/purrtty/features/offscreen-imgui-context-render-to-texture
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/purrtty/features/ray-pick-focus-for-world-quads
techniques: [vulkan-pipeline, imgui-window]
hooks:
  - starmap StarMapAfterGui
  - harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass
  - harmony-prefix KSA.Program.OnKey
  - harmony-prefix KSA.GameSettings.OnKeyAll
lifecycle: [frame:draw-ui, frame:input, frame:render]
game_types: [KSA.Part, KSA.IViewport, KSA.Rendering.RenderTarget, KSA.ImGuiBackendVulkanImpl]
tags: [live-screen, quad, world-space, ray-pick, focus, interactive]
---

Outcome: the texture from approach 1 appears in the 3D world on a quad bolted to a part of the rocket (or held in front of the camera like a HUD billboard), blended over the scene and hidden behind opaque geometry, and it is **interactive**: clicking on it focuses it, typing goes to the UI, and mouse-aware programs get clicks, drags and wheel at the cell under the cursor.
This is the world-quad variant of the live-screen sub-step. Demonstrated by purrTTY ([Render-to-texture with a second ImGui context and Vulkan backend](../../../../mods/purrtty/features/offscreen-imgui-context-render-to-texture.md), [World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md), [Click-to-focus and input routing for world-space panels](../../../../mods/purrtty/features/ray-pick-focus-for-world-quads.md)). Prerequisites: StarMap, Harmony; approach 1 for the texture.

## Background: how the game does it

The quad is drawn by the mod's own pipeline inside the scene pass; the pipeline, the pass hook and the anchoring are covered in [draw a textured quad in the world](../draw-a-textured-quad-in-the-world/overview.md) (approach 3) and [build a Vulkan pipeline for the scene target](../../vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md) (approach 2). For picking, `Cursor.GetEgoRay(IViewport)` [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs) returns a ray from the viewport's camera through the mouse in ego space, and
`ray.RaycastMollerTrumbore(v0, v1, v2, out t)` tests triangles. Mouse position is stored by `Program.PrepareImGui` right after `ImGui.NewFrame`.

## Lifecycle and timing

| Step | Where |
|---|---|
| Render the off-screen UI | `frame:draw-ui`, `OnAfterGui` (approach 1) |
| Pick and process input | the same call, before the instances render their off-screen frames, so a click's effect shows on the next texture update |
| Record the quad | `frame:render` / `render:translucency`: a postfix on `RenderTranslucencyPass` |
| Game-input gate | prefixes on `Program.OnKey` and `GameSettings.OnKeyAll` run during `Glfw.PollEvents` of the following frame (`frame:input`) and read the focus state set earlier |

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Draw the quad** with a premultiplied-alpha pipeline (the off-screen texture is produced over a transparent clear), depth-tested without depth write, from the translucency postfix; anchor it to a part (`model = Scale * userRot * T(offset) * partEgo`, composed from `GetMatrixAsmb2Ego`, `PositionEgo`, `Asmb2Ego`) or to the camera in view space.

2. **Pick:** on the click frame cast `Cursor.GetEgoRay(Program.MainViewport)`, transform each pickable quad's four unit corners with the same model used for drawing (billboards via `model * camera.VPInv.view`) and test both triangles; the smallest `t` wins focus; a click that hits nothing clears focus unless ImGui wants the mouse.

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

3. **Hit to cell:** project the hit point onto the quad's edge vectors to get `uv` (V flipped to match the texture), map to texture pixels and then to a cell using the fractional cell size, and feed the engine's mouse encoder exactly as 2D windows do (presses require a hit, releases only for forwarded presses, motion on cell change, wheel as scroll buttons).

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

4. **Keyboard and game input:** for the focused instance, while no ImGui text field wants input, run the same key encoder as 2D windows on the main context's IO. Feed focus into prefixes that gate `Program.OnKey` and `GameSettings.OnKeyAll` so typing never flies the rocket, and ignore the toggle hotkey while an in-world UI is focused. A focused 2D window wins over in-world focus.

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L345-L378) | Ray pick, focus, input dispatch |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs) | Model matrix, raycast, draw |
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs) | Shared pipelines and buffers |

## Variations and gotchas

- **Test in the space you draw in:** billboards are drawn in view space and must be converted to ego space for picking.
- **Billboards are not click-pickable by default,** because a screen-fixed panel would steal clicks meant for the world; an option opts in.
- **Clicking a quad is not consumed from the game:** KSA's own click and camera handling still sees the button (inferred, not observed).
- **A per-frame `Cursor.InputRay` no longer exists;** use `Cursor.GetEgoRay(viewport)` with the viewport whose camera the quads are drawn with.
- **Part identity:** the quad follows the same `Part` object through decoupling and docking by reference identity; saved layouts persist ids, and only `Vehicle.Id` is stable across restarts.

## See also

- [Overview](overview.md)
- [Approach 1: secondary ImGui context and fence ring](render-imgui-into-a-texture-1.md)
- [Approach 2: monitor material re-point](render-imgui-into-a-texture-2.md)
- [Click-to-focus and input routing for world-space panels](../../../../mods/purrtty/features/ray-pick-focus-for-world-quads.md), [World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md)
- [Draw a textured quad attached to a part or the camera](../draw-a-textured-quad-in-the-world/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
