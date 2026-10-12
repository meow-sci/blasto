---
type: Mod Feature
title: Live viewport image on an in-world monitor
description: A secondary camera viewport's finished image is shown on a monitor part by repointing the part material's bindless diffuse and emissive slots at it, with an ImGui background-draw-list quad grid painted over the screen for full brightness
mod: ksastructures
category: rendering
techniques: [imgui-drawlist, vulkan-texture, reflection, xml-asset]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - reflection KSA.ViewportBase.OptionFlags
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.ViewportBase
  - KSA.ViewportRegistry
  - KSA.IViewport
  - KSA.IGameViewport
  - KSA.Rendering.RenderImage
  - KSA.Rendering.RenderTarget
  - KSA.Vehicle
  - KSA.Camera
  - Brutal.ImGuiApi.ImDrawListExtensions
tags: [viewports, bindless-textures, render-to-texture, imgui, emissive, monitor, parts]
---

When the seated kitten "plays KSA on the computer", the office monitor shows a live chase-camera
view of the craft being flown. The picture is in the scene itself, in perspective and hidden behind
whatever stands in front of the monitor, and it stays when the UI is hidden. While the UI is visible,
a screen-bright copy is painted over it.

Mechanism in one line: one of KSA's spare camera windows renders the craft. Its window chrome is
removed, and each step the monitor material's bindless diffuse and emissive slots are pointed at that
viewport's `MainTarget.ColorImage.ImageView` with `Program.Instance.BindlessTextures.SetTexture`. In
the GUI pass the same picture is drawn on the background draw list as an 8 x 5 grid of
`AddImageQuad`s projected onto the monitor's corners. Handing the controls to the craft is a separate
feature, [Computer flies nearest craft](./computer-flies-nearest-craft.md).

## How it works

**A camera window of KSA's own.** On log-on, `KsaWorld.TryOpenCameraWindow` leases one of the
secondary viewports KSA builds at startup (`ViewportRegistry.TryOpenSecondaryViewport`, the call behind
`View > Add Camera`) and makes it visible. `TryHideViewportWindow` clears `ViewportOptionFlags.HasUi`
and `RenderOrbitLines` through the protected setter of `ViewportBase.OptionFlags`, found by
reflection, so KSA keeps rendering the view but draws no window for it. `ResizeViewport` asks for
768 x 416 with `RequestResize` ([src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L82-L106),
[src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L831-L904), [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L1062-L1073)).
On log-off, `CloseCameraWindow` releases it, and KSA restores the viewport's default flags
([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L226-L234), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L75)).

**The picture on the mesh** (`ShowOnTheMesh`, [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L178-L219)):

1. `ModLibrary.Get<PbrMaterialReference>("KSAOffice_MonitorMaterial")` gives the material's
   `DiffuseReference` and `EmissiveMap`. Each is a `TextureReference` holding the `BindlessHandle` it
   was registered at and its own `ImageView` ([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L67-L70),
   [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L13-L22)).
2. `KsaWorld.TryViewportImage` casts the viewport to `ViewportBase` and reads
   `MainTarget.ColorImage.ImageView` and `.Generation`
   ([src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L1040-L1060)). `MainTarget` is the viewport's final,
   tonemapped picture, the target `RenderViewport` composites into
   ([current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L40)).
3. When the generation differs from the one last bound, both slots are pointed at that view with
   `BindlessTextures.SetTexture(handle, view)`. `RenderImage.Generation` goes up whenever the engine
   rebuilds the image at a new size, and the rebuild destroys the old view
   ([current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L175-L191)).
4. On log-off both slots are pointed back at the textures' own `ImageView`s.

**Why it is legal and current.** `SetTexture` writes the descriptor straight away with
`vkUpdateDescriptorSets`. The bindless set is created `UpdateAfterBind | PartiallyBound`
([current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L100-L115),
[current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L201-L240)).[^ksa-bindless] In
`RenderGame` every visible secondary viewport is recorded before the main one
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4683)). `RenderViewport` ends with the composite into
`MainTarget`, whose default `EndRendering` leaves the colour in `ShaderReadOnlyOptimal`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4611-L4626),
[current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L310-L313)). That is the layout `SetTexture`
declares by default, so the main view's opaque pass samples this frame's camera picture.

**Why the monitor is a part.** The static-object fragment shader has no emissive term
([current/Content/Core/Shaders/Mesh/StaticObject.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/StaticObject.frag#L270-L370)). Part models are compiled with
`ENABLE_EMISSIVE` ([current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L133-L136)), and `MeshIndirect.frag`
then adds `gammaToLinear(vec3(emissive.r) * 1.25)` from the emissive texture
([current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L271-L288)).[^ksa-meshindirect-frag] So
the monitor is a one-part craft whose screen material has an emissive map
([src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6189-L6229)). With that slot rebound to the camera
picture, the screen glows by the picture's red channel, which is why the glow is greyscale, and takes
colour from the diffuse slot where light falls on it.

**The bright overlay** (`Paint`, [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L221-L266)):

1. `KsaWorld.TryViewportTexture` returns the viewport's `ImGuiTexture` (`ImTextureRef`)
   ([src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L1022-L1038)).
2. The monitor craft is found by its id, and `monitor.GetMatrixAsmb2Ego(camera)` gives the same
   assembly-to-camera matrix `Vehicle.UpdateRenderData` draws its parts with
   ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1273), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3719)).
3. A 9 x 6 lattice is interpolated between the four screen corners (stored in the monitor's own
   frame). Each point goes through the matrix, `camera.EgoToEcl` and `camera.EclToScreen(ecl, ignoreBehind: true)`,
   offset by the viewport's screen position. If any point is not finite the whole paint is skipped.
4. Each cell becomes `ImGui.GetBackgroundDrawList().AddImageQuad(texture, p1..p4, uv1..uv4)`
   ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L625)). Small cells keep the
   perspective that one affine quad would lose.

`Paint` runs every GUI pass while playing, not only when the mesh path failed. If anything in
`ShowOnTheMesh` throws, it logs once that it is "painting it over instead", and it retries on later
steps. Until a retry works, the overlay is all the player sees.

## Lifecycle and timing

- **`Drive()` -> `ShowOnTheMesh(live: true)`** runs in the mod's per-frame step, on the main thread.
  The step runs from whichever reaches it first of `[StarMapAfterGui]`, a prefix on
  `Program.OnFrameCelestials` (UI hidden), or `[StarMapAfterOnFrame]`
  ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L106-L137)). All three are before
  `UpdateRenderingResources` and command recording, except the last, which only takes effect next
  frame.
- **`Draw()` -> `Paint()`** runs in `[StarMapAfterGui]` (`frame:draw-ui`), between
  `ImGui.NewFrame` and `ImGui.Render` ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L152-L172)).
  ImGui is drawn after the tonemap composite
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4947-L4950)), so the quads sit over the 3D scene and under
  every ImGui window. They are not drawn while the UI is hidden.
- **The bindless write is immediate.** It affects every command buffer recorded after it. A
  re-point therefore lands in the same frame's render when the step ran before the render.
- **Why these points:** viewport images exist and are stable between frames, and descriptor writes
  are legal outside recording. ImGui calls are only legal inside the GUI window, which is why the
  overlay sits in `Draw()` and the rebinding in `Drive()`.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L178-L219) | `ShowOnTheMesh`: bindless re-point and restore |
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L221-L266) | `Paint`: projected 8 x 5 `AddImageQuad` grid |
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L19-L29) | Screen corners in the monitor's frame |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L1022-L1073) | `TryViewportTexture`, `TryViewportImage`, `ResizeViewport` |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L872-L904) | `TryHideViewportWindow`: reflective `OptionFlags` setter |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6189-L6229) | Monitor material with its own emissive map, and the monitor part |

## Snippets

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

```glsl
// current/Content/Core/Shaders/Mesh/MeshIndirect.frag L271-L288
    #ifdef ENABLE_EMISSIVE
    if (emissive && drawData.emissiveTextureIndex >= 0)
    {
        float sampledEmissive = texture(sampler2D(globalTextures[drawData.emissiveTextureIndex], textureSampler), inUv).x;
        if (sampledEmissive.x != 0.0)
        {
            if (addEmissiveColor) {
                vec3 unpacked = unpackRGB(inEmissiveColor);
                unpacked = gammaToLinear(unpacked * EMISSIVE_MULTIPLIER);
                lightColor += unpacked;
            }
            else
            {
                vec3 emissive = gammaToLinear(vec3(sampledEmissive) * EMISSIVE_MULTIPLIER);
                lightColor += emissive;
            }
        }
    }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Program.BindlessTextures`, `BindlessTextureLibrary.SetTexture` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L201-L210) | Re-pointing a bindless slot at another image view |
| `TextureReference.BindlessHandle`, `.ImageView` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L67-L70) | The slot and the original view to restore |
| `PbrMaterialReference.DiffuseReference`, `.EmissiveMap` | [current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L13-L22) | The monitor material's two textures |
| `ViewportBase.MainTarget`, `.OptionFlags`, `.ImGuiTexture` | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L28-L44) | Final picture, window flags (protected setter), ImGui texture |
| `RenderImage.Generation` | [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L68) | Detects a rebuilt image |
| `ViewportRegistry.TryOpenSecondaryViewport`, `.ReleaseSecondaryViewport`, `.GameViews` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L52-L226) | Leasing a camera window |
| `IViewport.RequestResize` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L53) | Fixed picture size |
| `Vehicle.GetMatrixAsmb2Ego(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1273) | The monitor's draw matrix |
| `Camera.EgoToEcl`, `Camera.EclToScreen` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L344) | Projection of the screen corners |
| `ImDrawListPtr.AddImageQuad`, `ImGui.GetBackgroundDrawList` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L625), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8033) | Overlay quads |

## Pitfalls and version notes

- **A bindless slot belongs to a texture, not a material.** Re-pointing `diffuse.BindlessHandle`
  changes every material that shares that `TextureReference`. The monitor's material has texture files
  of its own for that reason, and the mod's validator fails a texture path shared between materials
  ([tools/validate-parts.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/validate-parts.py#L293-L296)).
- **A rebuild leaves a dangling slot until the next step.** The engine destroys the old view when it
  rebuilds the image, and the mod re-points only when it next sees the new `Generation`. A secondary
  viewport's surface is rebuilt in `Program.OnFrameViewports` (`RebuildViewport`, after a
  `Device.WaitIdle`, when `NeedsOwnedSurfaceRebuild` sees a pending size or a sample-count change),
  which is before every hook the step can run from except the frame postfix. So with the step in
  `[StarMapAfterGui]` or the `OnFrameCelestials` prefix the slot is re-pointed before the same frame
  renders; only when the step falls back to `[StarMapAfterOnFrame]` does one frame sample the
  destroyed view ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2620),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5086-L5089)).
  The mod's own `RequestResize` to 768 x 416 is applied this way on the frame after the window
  opens.
- **The overlay ignores depth.** It is drawn over the scene, so a paw or a cup in front of the monitor
  is painted over while the UI is visible; the mesh path is the correctly occluded one.
- **Stale comment.** `Paint`'s comment says "a static object's shader has no emissive term". The
  monitor is a part, and the overlay's real job is brightness and full colour, since the part's
  emissive glow is the red channel only.
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554. `BindlessTextureLibrary` changed between them only in its device-limit clamp, not
  in `SetTexture`.

## See also

- [Computer flies nearest craft](./computer-flies-nearest-craft.md): control transfer and the camera driving
- [Office furniture as craft](./office-furniture-craft.md): how the monitor part is set down
- [Level horizon camera controller](./level-horizon-camera-controller.md): how the borrowed views are posed
- [Frame step plumbing](./frame-step-plumbing.md): which hook runs `Drive()`
- [Asset and mesh validation tools](./asset-and-mesh-validation-tools.md): the shared-texture check
- [BindlessTextures.SetTexture on the monitor's material to a secondary viewport's colour image](../../../howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-2.md) - how-to article demonstrating this feature

[^ksa-bindless]: RenderCore.Systems.BindlessTextureLibrary (decompiled)
[^ksa-meshindirect-frag]: Core shader Mesh/MeshIndirect.frag
