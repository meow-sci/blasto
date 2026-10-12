---
type: How-To
title: BindlessTextures.SetTexture on the monitor's material to a secondary viewport's colour image
description: Show a live camera feed on a monitor part by pointing its bindless diffuse and emissive slots at a secondary viewport's colour image, with a projected-quad ImGui fallback
category: rendering
group: textures-and-decals
topic: render-imgui-into-a-texture
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/viewport-image-on-monitor]
techniques: [vulkan-texture, imgui-drawlist]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - reflection KSA.ViewportBase.OptionFlags
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.ViewportRegistry
  - KSA.Part
  - KSA.IViewport
  - KSA.Rendering.RenderTarget
  - RenderCore.Systems.BindlessTextureLibrary
tags: [live-screen, viewport, bindless, monitor, camera-feed, setTexture]
---

Outcome: a monitor mesh in the scene shows a live view of another camera, in perspective and hidden behind whatever stands in front of it, and it stays when the UI is hidden. No Vulkan pipeline is needed: one of KSA's spare camera windows already renders the picture into its own colour image, and the monitor material's bindless slots are pointed at that image. A projected ImGui overlay paints a
brighter copy while the UI is visible. This is the live-screen sub-step (bindless re-point, and the no-pipeline projected quad). Demonstrated by KSAStructures ([Live viewport image on an in-world monitor](../../../../mods/ksastructures/features/viewport-image-on-monitor.md)). Prerequisites: StarMap.

## Background: how the game does it

Secondary viewports are built at startup and leased with `ViewportRegistry.TryOpenSecondaryViewport` [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L180) (the call behind View > Add Camera). A viewport's final tonemapped picture is its `MainTarget` colour image, with a `Generation` that increments whenever the engine rebuilds the image at a new size, destroying the old view [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L175-L191).
`BindlessTextureLibrary.SetTexture(handle, view)` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L201) writes the descriptor immediately (the set is update-after-bind). In `RenderGame` every visible secondary viewport is recorded before the main one [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4683), and its composite leaves the colour in `ShaderReadOnlyOptimal`, the layout `SetTexture` declares by default, so the main view's opaque pass samples this frame's
camera picture. A monitor must be a **part**: static-object shaders have no emissive term, while part models are compiled with `ENABLE_EMISSIVE` [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L133-L136).

## Lifecycle and timing

| Step | Where |
|---|---|
| Open/hide/resize the camera window | on log-on; hide the window chrome by clearing `ViewportOptionFlags.HasUi` and `RenderOrbitLines` through the protected `ViewportBase.OptionFlags` setter (reflection); `RequestResize` to 768 x 416 |
| Re-point the slots (`Drive`) | the mod's per-frame step: whichever reaches it first of `[StarMapAfterGui]`, a prefix on `OnFrameCelestials` (UI hidden), or `[StarMapAfterOnFrame]`; all but the last are before `UpdateRenderingResources` and recording |
| Paint the overlay | `[StarMapAfterGui]` (`frame:draw-ui`), between `ImGui.NewFrame` and `ImGui.Render`; drawn after the tonemap composite, so over the scene and under ImGui windows; not drawn while the UI is hidden |

The bindless write is immediate and affects every command buffer recorded after it, so a re-point lands in the same frame's render when the step ran before the render. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Lease a camera window** and remove its chrome; ask for the size you need.

   ```csharp
   // src/KSAStructures/Ksa/KsaWorld.cs L875-L876, L1044-L1060
   private static readonly MethodInfo? OptionFlagsSetter =
       typeof(ViewportBase).GetProperty(nameof(ViewportBase.OptionFlags))?.GetSetMethod(nonPublic: true);
   // ...
   public static bool TryViewportImage(int index, out Brutal.VulkanApi.VkImageView view, out int generation)
   {
       view = default;
       generation = -1;
       try
       {
           if (!TryViewport(index, out IGameViewport viewport) || viewport is not ViewportBase { MainTarget.ColorImage: { } image }) return false;

           view = image.ImageView;
           generation = image.Generation;
           return true;
       }
       catch
       {
           return false;
       }
   }
   ```

2. **Re-point the monitor material's slots.** The material's `DiffuseReference` and `EmissiveMap` are `TextureReference`s with a `BindlessHandle`. When the viewport's `Generation` differs from the last bound one, call `SetTexture(handle, viewportView)` for both; on log-off restore each to the texture's own `ImageView`.

   ```csharp
   // src/KSAStructures/Ksa/OfficeComputer.cs L182-L219
   private void ShowOnTheMesh(bool live)
   {
       try
       {
           PbrMaterialReference material = ModLibrary.Get<PbrMaterialReference>("KSAOffice_MonitorMaterial");
           if (material.DiffuseReference is not { } diffuse) return;

           var textures = Program.Instance.BindlessTextures;
           if (!live)
           {
               if (_onTheMesh)
               {
                   textures.SetTexture(diffuse.BindlessHandle, diffuse.ImageView);
                   if (material.EmissiveMap is { } still) textures.SetTexture(still.BindlessHandle, still.ImageView);
               }

               _onTheMesh = false;
               _shown = -1;
               return;
           }

           if (!KsaWorld.TryViewportImage(_window, out var view, out int generation)) return;
           if (generation == _shown && _onTheMesh) return;

           textures.SetTexture(diffuse.BindlessHandle, view);

           // The glow too, or the still desktop goes on glowing through the game.
           if (material.EmissiveMap is { } glow) textures.SetTexture(glow.BindlessHandle, view);
           _shown = generation;
           _onTheMesh = true;
       }
       catch (Exception e)
       {
           if (_onTheMesh || _shown != -2) Log.Warn($"office computer: the monitor cannot show the game itself ({e.Message}); painting it over instead");
           _onTheMesh = false;
           _shown = -2;
       }
   }
   ```

3. **Paint a bright overlay (no pipeline).** Get the viewport's `ImGuiTexture`, take the monitor's `GetMatrixAsmb2Ego(camera)`, interpolate a lattice of points between the four screen corners (stored in the monitor's frame), project each through `camera.EgoToEcl` and `camera.EclToScreen(ecl, ignoreBehind: true)` plus the viewport position, skip the whole paint if any point is not finite, and draw each cell with `AddImageQuad`:
   small cells keep the perspective one affine quad would lose.

   ```csharp
   // src/KSAStructures/Ksa/OfficeComputer.cs L236-L263
   double4x4 asmb2Ego = monitor.GetMatrixAsmb2Ego(camera);

   const int Across = 8, Down = 5;
   Span<float2> grid = stackalloc float2[(Across + 1) * (Down + 1)];
   for (int j = 0; j <= Down; j++)
   {
       for (int i = 0; i <= Across; i++)
       {
           double u = (double)i / Across, v = (double)j / Down;
           double3 top = Corners[0] + ((Corners[1] - Corners[0]) * u);
           double3 bottom = Corners[3] + ((Corners[2] - Corners[3]) * u);
           double3 ecl = camera.EgoToEcl((top + ((bottom - top) * v)).Transform(asmb2Ego));

           float2 s = camera.EclToScreen(ecl, ignoreBehind: true);
           if (!float.IsFinite(s.X) || !float.IsFinite(s.Y)) return;
           grid[(j * (Across + 1)) + i] = new float2(view.Position.X + s.X, view.Position.Y + s.Y);
       }
   }

   ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
   for (int j = 0; j < Down; j++)
   {
       for (int i = 0; i < Across; i++)
       {
           int a = (j * (Across + 1)) + i, b = a + Across + 1;
           float u0 = (float)i / Across, u1 = (float)(i + 1) / Across, v0 = (float)j / Down, v1 = (float)(j + 1) / Down;
           float2? tl = new float2(u0, v0), tr = new float2(u1, v0), br = new float2(u1, v1), bl = new float2(u0, v1);
           draw.AddImageQuad(texture, in grid[a], in grid[a + 1], in grid[b + 1], in grid[b], in tl, in tr, in br, in bl, null);
   ```

4. **Fall back on failure:** if `ShowOnTheMesh` throws, log once that it is "painting it over instead" and retry on later steps.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs) | Open/close, `ShowOnTheMesh`, `Paint` |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L831-L904) | Camera window helpers |

## Variations and gotchas

- **A bindless slot belongs to a texture, not a material:** re-pointing `diffuse.BindlessHandle` changes every material that shares that `TextureReference`. Give the monitor material texture files of its own.
- **A rebuild leaves a dangling slot until the next step** (the engine destroys the old view when it rebuilds a viewport surface). With the step in `[StarMapAfterGui]` or the `OnFrameCelestials` prefix the slot is re-pointed before the same frame renders; with the `[StarMapAfterOnFrame]` fallback one frame samples the destroyed view.
- **The overlay ignores depth:** a paw or cup in front of the monitor is painted over while the UI is visible; the mesh path is the occluded one.
- **Emissive glow is the red channel only,** hence the greyscale glow and the overlay's job of brightness and full colour.

## See also

- [Overview](overview.md)
- [Approach 1: secondary ImGui context and fence ring](render-imgui-into-a-texture-1.md)
- [Approach 3: world-space quad](render-imgui-into-a-texture-3.md)
- [Live viewport image on an in-world monitor](../../../../mods/ksastructures/features/viewport-image-on-monitor.md)
- [Replace a game texture or model texture with a user PNG at runtime](../replace-a-game-texture-at-runtime/overview.md), [Project world and sky positions to screen pixels and point at off-screen targets](../../overlays/project-world-positions-to-screen/overview.md)
