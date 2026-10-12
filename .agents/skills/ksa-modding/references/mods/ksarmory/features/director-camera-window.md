---
type: Mod Feature
title: Director camera windows on leased secondary viewports
description: An optical director leases one of KSA's four spare viewports, aims its unfollowed Fixed camera with LookAt from the GUI hook, hides KSA's own window by reflection and shows the viewport texture in a mod ImGui window with pod controls
mod: ksarmory
category: camera
techniques: [reflection, imgui-window, viewport-lease, direct-camera-write, per-viewport-shader-data]
hooks:
  - starmap StarMapAfterGui (via StepOnce)
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials (via StepOnce, HUD hidden)
  - reflection KSA.ViewportBase.OptionFlags (protected setter)
lifecycle: [frame:draw-ui, frame:celestials, frame:viewports, frame:render, render:imgui]
game_types:
  - KSA.ViewportRegistry
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.IViewport
  - KSA.ViewportBase
  - KSA.ViewportPresets
  - KSA.ViewportOptionFlags
  - KSA.ViewportType
  - KSA.Camera
  - KSA.CameraMode
  - KSA.FixedController
  - KSA.Program
  - KSA.DockingPort
  - Brutal.ImGuiApi.ImGui
tags:
  - camera-window
  - secondary-viewport
  - viewport-lease
  - imgui-image
  - multi-monitor
  - fixed-camera
  - director
  - picture-in-picture
---

Each optical director can show its picture in a camera window of its own. The panel's **Camera**
button leases one of KSA's spare secondary viewports, the same lease `View > Add Camera` takes, and
from then on the mod aims that viewport's camera from the head's eye along its line of sight every
frame, whichever craft the panel is showing.[^ksarmory-uioptic][^ksa-program] KSA's own window round
the viewport is taken off, and the mod draws the viewport's rendered texture in an ImGui window of
its own with the pod's controls over the picture: zoom (`-`, `x1`, `+` or the wheel), sensor mode
(TV, WHOT, BHOT, CLR), `TRK` and, on a head with a laser, `LASER`.[^ksarmory-directorwindow] The
window can be dragged out of the game onto another monitor, because KSA turns on ImGui's platform
viewports (`ImGuiConfigFlags.ViewportsEnable`, Program.cs L847), and its title names the head and the
craft carrying it, because on a second screen the title is all that identifies it.[^ksa-program][^ksarmory-mod]

The mechanism differs from borrowing the main view: the mod writes the window camera's pose
directly (not a controller's inputs) and rebuilds its matrices itself, so the window is aimed in the
same frame it is drawn.[^ksarmory-ksaworld]

## How it works

### Leasing a viewport

- KSA builds four `Secondary` viewports at boot (500 x 500, Program.cs L950-L953) with the
  `ViewportPresets.SECONDARY_GAME` flags: UI, input, gizmos, orbit lines, world UI, stars,
  atmosphere, vehicles, part models and terrain, but no shadows, ray tracing or audio; their light
  mode is `Forward` (`ViewportPresets.LightModeFor`).[^ksa-program][^ksa-viewportpresets]
- `ViewportRegistry.TryOpenSecondaryViewport` leases the first unleased secondary viewport and calls
  `ResetToDefaults` on it (ViewportRegistry.cs L180-L215). `KsaWorld.TryOpenCameraWindow` calls it,
  makes the viewport visible, and finds its index by reference in `ViewportRegistry.GameViews`,
  because the index is a position in that span and every caller re-resolves it each frame.[^ksa-viewportregistry][^ksarmory-ksaworld]
- The panel disables the button when `ViewportRegistry.AvailableSecondaryCount` is zero; docking-port
  cameras claim from the same pool (`DockingPort` calls `TryClaimSecondaryViewport`).[^ksarmory-uioptic][^ksa-dockingport]
- One head per window: `UiOptic.TakeWindow` takes the window off any other head, and
  `DriveCameraWindows` lets the first head keep a contested index and switches the second off.[^ksarmory-uioptic][^ksarmory-mod]
- Handing back is `ViewportRegistry.ReleaseSecondaryViewport`, exactly what KSA's own close button
  does: it hides the viewport and resets it to Orbit mode, zero `FixedController` offsets, default
  option flags and the name `Camera {Id}` (GameViewport.cs L66-L85), so nothing the mod changed needs
  restoring by hand. A window driven last frame and not this frame first gets its field of view put
  back to 50 degrees (`KsaWorld.ResetViewportFov`), because a player's zoom keys clamp at 15.[^ksa-viewportregistry][^ksa-gameviewport][^ksarmory-mod]

### Aiming the window camera (`DriveWindow`, `KsaWorld.TryLookFromViewport`)

1. Check the lease is still a visible `Secondary` viewport (`IsUsableCameraWindow`); a closed window
   switches the director off rather than driving a viewport nobody sees.[^ksarmory-mod]
2. Resolve the eye and line of sight with `OpticalHead.TryOpticViewEclAt(anchor, ..., viewIndex)`,
   anchored on the platform's centre **as this window's camera draws it**
   (`KsaWorld.TryDrawnCentreEcl` through `Vehicle.GetMatrixAsmb2Ego(camera)`). A camera following a
   craft draws its physics-bubble neighbours at their physics positions; one following nothing draws
   every craft at its analytic position (`Camera.GetPositionEgo`, Camera.cs L236-L249). The two
   differ by metres, which is shake at high zoom.[^ksarmory-opticalhead][^ksa-camera]
3. `Unfollow(changeControl: false)` every frame, then `SetCameraMode(Fixed)`. A follow can arrive at
   any time (vessel next and previous act on the hovered viewport), and with one attached the fixed
   controller would place the camera at `following + CameraOffset`. With nothing followed,
   `FixedController.OnFrame` does nothing (FixedController.cs L24-L25), so the pose the mod writes is
   the pose. Map mode would hand back the map camera instead.[^ksarmory-ksaworld][^ksa-fixedcontroller][^ksa-gameviewport]
4. `BaseCamera.LookAt(eye, eye + forward * 1000, up)`, with `up` the head's `Boresight` (local up at
   the platform), so the horizon sits level.[^ksarmory-mod][^ksa-camera]
5. Field of view `SightZoom.FovDegreesFor(50, magnification)`, clamped to 1-120 degrees because
   `Camera.SetFieldOfView` does not clamp and the projection throws outside (0, 180).[^ksarmory-sightzoom][^ksa-camera]
6. `SetNearbyContext`: nearby body, distances, terrain height and altitude for *this* camera, with the
   engine's 80,000 km cut-off, because `Program.OnFrameCelestials` derives them only for the frame
   camera (Program.cs L2701-L2731), and a window's terrain level of detail and sky read them.[^ksarmory-ksaworld][^ksa-program]
7. `camera.OnFrame(dt)` rebuilds the view matrices now (they were built in the viewport pass before
   the hook), then `Program.UpdateShaderData` and `Program.SetCameraUbo` for that viewport.[^ksarmory-ksaworld][^ksa-camera]
8. `KsaWorld.NameViewport` sets the title only when it differs: the name is the ImGui window's
   identity, and rewriting it each frame would reset where the player put the window.[^ksarmory-ksaworld]
9. `SensorPass.Want(index, policy.Sensor)` requests the TV or thermal look for that viewport; see
   [the sensor look pass](./sensor-look-pass.md).[^ksarmory-sensorpass]

### Showing the picture (`DirectorWindow.Draw`)

- `KsaWorld.TryHideViewportWindow` reflects the protected setter of `ViewportBase.OptionFlags` and
  clears `HasUi` and `RenderOrbitLines`. `Program.OnDrawUiViewports` only calls `DrawImGui` for
  non-main viewports with `HasUi` (Program.cs L3162-L3176), and `GameViewport.DrawImGui` also hosts
  the viewport's hover handling, world UI and menu bar (GameViewport.cs L193-L260), so all of that goes
  with KSA's window.[^ksarmory-ksaworld][^ksa-viewportbase][^ksa-program][^ksa-gameviewport]
- `ImGui.Begin($"{title}###KSArmoryDirector{index}")`: the visible title follows the craft's name,
  the `###` id stays fixed so a rename does not move the window.[^ksarmory-directorwindow]
- `ImGui.Image(viewport.ImGuiTexture, size)` shows the viewport's render surface. `RenderGame`
  records every visible secondary viewport (Program.cs L4672-L4682) before the ImGui draw data
  (L4950) in the same command buffer, so the image is this frame's render.[^ksa-iviewport][^ksa-program]
- `KsaWorld.PlaceViewportPicture` writes back what KSA's own window would have recorded
  (`SetPosition`, `ImGuiId` = the window's platform viewport id, GameViewport.cs L220-L221), because
  projections and the sight read the picture's place from the viewport; it requests a resize only
  once the left mouse button is up, since every size is a new set of render targets.[^ksarmory-ksaworld][^ksa-gameviewport]
- The sight is painted between the image and the buttons on the window's own draw list
  (`SightSurface.InWindow`), so windows over it cover it as they cover the picture; see
  [gunner's sight overlay](./gunner-sight-overlay.md).[^ksarmory-sightsurface]
- The window's close box calls `KsaWorld.CloseCameraWindow` and sets `OpticConfig.Viewport = -1`.[^ksarmory-directorwindow][^ksarmory-opticconfig]
- If the flags cannot be cleared, `Draw` returns false, KSA's own window stays, and the caller paints
  the sight over it through that platform viewport's foreground list instead.[^ksarmory-mod]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:viewports` | `OnFrameViewports` (Program.cs L2263) runs `GameViewport.OnFrame`: `FixedController.OnFrame` (no-op, nothing followed), then `Camera.OnFrame` (clamp to terrain, matrices) | The pose written last frame persists[^ksa-gameviewport][^ksa-fixedcontroller] |
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (L2268) runs `StepOnce`, whose `DriveCameras` calls `DriveCameraWindows` after the watch camera, the bridge and the chase | Lease bookkeeping, aim, field of view, matrices, sensor request[^ksarmory-mod] |
| `frame:celestials` | HUD hidden: Harmony prefix on `Program.OnFrameCelestials` (L2320) runs `StepOnce` | Windows stay aimed with the HUD hidden; `DirectorWindow` itself is not drawn, since it lives in the GUI hook |
| `frame:draw-ui` | Rest of `OnAfterGui`, after the panel and overlays (KSArmoryMod.cs L626-L645) | `DirectorWindow.Draw` per driven window, sight painted inside it[^ksarmory-mod] |
| `frame:render` | `Render`: `UpdateShaderData` for every viewport (L2461-L2465), then `RenderGame` records secondary viewports (L4672-L4682) | The window is rendered with the pose written in the hook |
| `render:imgui` | `ImGuiBackend.Vulkan.RenderDrawData` (L4950) | The window, its image and the sight composited |

**Latency: none.** Everything the window is drawn from is sampled in this frame: vehicles were
advanced in `PrepareFrame`, the head was stepped earlier in the same `StepOnce`, the pose is written
straight to the camera, and `Camera.OnFrame` is re-run so the matrices match before `Render` reads
them. Nothing between the hook and the render rewrites a camera that follows nothing.[^ksarmory-ksaworld][^ksa-program]

The mod's comment says the extra `UpdateShaderData` and `SetCameraUbo` calls re-upload sky and
atmosphere data already computed for the old pose. In 5554 `Render` rebuilds every viewport's shader
data after the hook anyway (L2461-L2465), and `SetCameraUbo` replaces the slot's `UboCameraData` with
a fresh struct (Program.cs L1346-L1360), clearing fields `UpdateShaderData` had just set until
`Render` writes them again (observed in the decomp, not measured).[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/DirectorWindow.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/DirectorWindow.cs#L31-L114) | Window, image, picture placement, pod buttons |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1065-L1180) | `DriveCameraWindows` and `DriveWindow` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L626-L645) | Drawing every driven window from the GUI hook |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1216-L1223) | `ViewportTitle`: part name and craft |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3625-L3908) | Viewport lookup, hide, texture, placement, close, name, usability, pose read-back, open |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5679-L5816) | `TryLookFromViewport`, `SetNearbyContext`, `ResetViewportFov` |
| [src/KSArmory/Ksa/Ui/UiOptic.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiOptic.cs#L38-L63) | Camera and Main view buttons, `TakeWindow` (L190-L201) |

## Snippets

```csharp
// src/KSArmory/Ksa/Ui/DirectorWindow.cs L31-L77
    public static bool Draw(OpticalHead head, OpticConfig policy, string title, Action<SightSurface> paint)
    {
        int index = policy.Viewport;
        if (!KsaWorld.TryHideViewportWindow(index)) return false;
        if (!KsaWorld.TryViewportTexture(index, out ImTextureRef texture, out float2 rendered)) return true;

        ImGui.SetNextWindowSize(new float2(540f, 560f), ImGuiCond.FirstUseEver);

        bool open = true;
        // The index in the id rather than the title, so renaming the craft does not move the window.
        if (ImGui.Begin($"{title}###KSArmoryDirector{index}", ref open,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                        | ImGuiWindowFlags.NoCollapse))
        {
            float2 at = ImGui.GetCursorScreenPos();
            float2 room = ImGui.GetContentRegionAvail();

            ImGui.SetNextItemAllowOverlap();
            ImGui.Image(texture, in rendered, null, null);
            // ...
            // Written before the sight paints: its projections read the picture's place back.
            KsaWorld.PlaceViewportPicture(index, at, ImGui.GetWindowViewport().ID, int2.Zero);
            paint(SightSurface.InWindow(index, at, rendered));
            // ...
            // Resized once the drag ends: every size is a new set of render targets.
            int2 wanted = ImGui.IsMouseDown(ImGuiMouseButton.Left)
                        ? int2.Zero
                        : new int2((int)room.X, (int)room.Y);
            KsaWorld.PlaceViewportPicture(index, at, ImGui.GetWindowViewport().ID, wanted);
        }
        ImGui.End();

        if (!open)
        {
            KsaWorld.CloseCameraWindow(index);
            policy.Viewport = -1;
        }
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5705-L5746
            try
            {
                if (viewport.GetCamera() is { Following: not null } followed)
                {
                    followed.Unfollow(changeControl: false);
                }
            }
            catch { /* a view that cannot be unfollowed is still worth trying to aim */ }

            if (viewport.Mode != CameraMode.Fixed) viewport.SetCameraMode(CameraMode.Fixed);

            Camera camera = viewport.BaseCamera;
            if (camera is null) return false;
            // ...
            camera.LookAt(eyeEcl, eyeEcl + Vec.Unit(forwardEcl) * 1000.0, upEcl);

            // SetFieldOfView does not clamp, and the projection throws outside (0, 180).
            float wanted = (float)Math.Clamp(double.IsFinite(fovDeg) ? fovDeg : SightZoom.DefaultFovDeg,
                                             SightZoom.MinFovDeg, SightZoom.MaxFovDeg);
            if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) >= 1e-3)
            {
                camera.SetFieldOfView(wanted);
            }

            SetNearbyContext(camera);
            // ...
            camera.OnFrame(dt);
            // ...
            if (Program.Instance is { } program)
            {
                program.UpdateShaderData(dt, viewport);
                program.SetCameraUbo(viewport);
            }
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L3651-L3674
    private static readonly MethodInfo? OptionFlagsSetter =
        typeof(ViewportBase).GetProperty(nameof(ViewportBase.OptionFlags))?.GetSetMethod(nonPublic: true);
    // ...
            if (OptionFlagsSetter is null) return false;
            if (!TryViewport(index, out IGameViewport viewport)) return false;
            if (viewport.Type != ViewportType.Secondary || !viewport.Visible) return false;

            // Orbit lines too: a sight looking past the Moon otherwise shows its orbit across the picture.
            const ViewportOptionFlags taken = ViewportOptionFlags.HasUi | ViewportOptionFlags.RenderOrbitLines;

            ViewportOptionFlags flags = viewport.OptionFlags;
            if ((flags & taken) == 0) return true;

            OptionFlagsSetter.Invoke(viewport, [flags & ~taken]);
            return (viewport.OptionFlags & taken) == 0;
```

```csharp
// current/decomp/KSA/GameViewport.cs L209-L229
		bool pOpen = base.Visible;
		bool flag = ImGui.Begin(base.Name, ref pOpen, imGuiWindowFlags);
		SetVisible(pOpen);
		if (!pOpen)
		{
			ViewportRegistry.ReleaseSecondaryViewport(this);
			ImGui.End();
			return;
		}
		if (flag)
		{
			ImGuiId = ImGui.GetWindowViewport().ID;
			base.Position = ImGui.GetCursorScreenPos();
			float2 imageSize = new float2(Size.X, Size.Y);
			float2 contentRegionAvail = ImGui.GetContentRegionAvail();
			if (ImGui.IsWindowHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && base.AllowResize)
			{
				RequestResize(new int2((int)contentRegionAvail.X, (int)contentRegionAvail.Y));
			}
			ImGui.SetNextItemAllowOverlap();
			ImGui.ImageWithBg(base.ImGuiTexture, in imageSize, (float2?)null, (float2?)null, (float4?)null, (float4?)null);
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `ViewportRegistry.TryOpenSecondaryViewport`, `ReleaseSecondaryViewport`, `AvailableSecondaryCount`, `GameViews` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L48-L240) | Lease, release, spare count, index lookup |
| `GameViewport.DrawImGui`, `ResetToDefaults`, `SetName`, `ImGuiId`, `SetCameraMode`, `BaseCamera` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L56-L312) | KSA's own window (replaced), reset on release, title, mode |
| `ViewportBase.OptionFlags` (protected setter), `SetPosition`, `RequestResize`, `ImGuiTexture` | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L28-L91) | Hide KSA's window, place and size the picture, read the texture |
| `IViewport.Width`, `Height`, `Position`, `Type`, `Visible` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs) | Picture size and usability checks |
| `ViewportPresets.SECONDARY_GAME`, `LightModeFor` | [current/decomp/KSA/ViewportPresets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportPresets.cs#L7-L28) | What a camera window renders |
| `Camera.LookAt`, `SetFieldOfView`, `GetFieldOfView`, `OnFrame`, `Unfollow`, `GetPositionEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203-L631) | Aim the window camera and rebuild its matrices |
| `FixedController.OnFrame` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L48) | No-op without a followed object, so the written pose stands |
| `Program.UpdateShaderData`, `SetCameraUbo`, `FindNearbyCelestial`, `GetCurrentAltitudeKm(Camera)`, `RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2854) | Per-viewport shader data, nearby context, render order |
| `DockingPort` (claims a secondary viewport) | [current/decomp/KSA/DockingPort.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DockingPort.cs#L188) | Competes for the same four viewports |

## Pitfalls and version notes

- **A mod cannot create a viewport.** `ViewportRegistry` seals allocation after boot (Program.cs L962); four secondaries
  is the whole budget, shared with KSA's `View > Add Camera` and docking-port cameras.[^ksa-viewportregistry][^ksa-dockingport]
- **Index is not identity.** The index is a position in `ViewportRegistry.GameViews` and is
  re-resolved every frame; the main view is found by reference to `Program.MainViewport`.[^ksarmory-ksaworld]
- **Unfollow every frame**, or a vessel switch with the cursor over the window parks the camera
  inside the craft switched to.[^ksarmory-ksaworld]
- **What a window does not draw.** `RenderViewport` records no ocean or cloud pass, and the clustered
  light pre-pass belongs to the main view; the window's terrain shadows use cascades fitted to the
  main camera (Program.cs L4331-L4333), so they shift when the player turns the main view.[^ksa-program][^ksa-viewportpresets][^ksarmory-blocked]
- **Not drawn with the HUD hidden.** The camera keeps being aimed from `StepOnce`, but the window is
  drawn from the GUI hook, which KSA skips while `DrawUI` is false.[^ksarmory-mod]
- **Zoom left on a released window.** A player's zoom keys go through `Camera.ChangeFieldOfView`,
  which clamps to 15-120 degrees (Camera.cs L455-L461), so a director let go at 1 degree would strand
  the window; the mod resets it to 50 first.[^ksa-camera][^ksarmory-ksaworld]
- **Renaming moves windows.** `SetName` changes the ImGui window name KSA's window is keyed on, so
  write it only when it differs.[^ksarmory-ksaworld][^ksa-gameviewport]
- **Reflection target.** Hiding KSA's window depends on `ViewportBase.OptionFlags` keeping a
  non-public setter; when it is missing the method returns false and KSA's window stays.[^ksarmory-ksaworld][^ksa-viewportbase]

## See also

- [Sensor look pass](./sensor-look-pass.md)
- [Optical director heads](./optical-director-heads.md)
- [Gunner's sight overlay](./gunner-sight-overlay.md)
- [Borrow the main view and hand it back](./borrow-main-view-camera.md)
- [Operator panel in ImGui](./operator-panel-imgui.md)
- [Cursor-ray picking and world click tools](./cursor-ray-world-click-tools.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Aim an unfollowed Fixed camera on a secondary viewport with LookAt and the platform's local up](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-4.md) - how-to article demonstrating this feature
- [Call SetFieldOfView in degrees, read GetFieldOfView in radians and zoom past the player clamp](../../../howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-2.md) - how-to article demonstrating this feature
- [Aim from the GUI hook with LookAt, then rebuild the matrices and per-viewport shader data and fill the nearby body yourself](../../../howto/camera/secondary-viewports/aim-a-secondary-viewport-camera/aim-a-secondary-viewport-camera-1.md) - how-to article demonstrating this feature
- [Open a secondary viewport, make it visible and re-resolve its index by reference every frame](../../../howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-1.md) - how-to article demonstrating this feature
- [Clear HasUi and RenderOrbitLines through the reflected OptionFlags setter and draw the viewport texture in your own window](../../../howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-3.md) - how-to article demonstrating this feature
- [Resolve the eye, sight line and roll from an optical head sub-part for the main view or a camera window](../../../howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-2.md) - how-to article demonstrating this feature

[^ksarmory-directorwindow]: src/KSArmory/Ksa/Ui/DirectorWindow.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-uioptic]: src/KSArmory/Ksa/Ui/UiOptic.cs
[^ksarmory-sightsurface]: src/KSArmory/Ksa/SightSurface.cs
[^ksarmory-sightzoom]: src/KSArmory/Sim/SightZoom.cs
[^ksarmory-opticconfig]: src/KSArmory/Sim/OpticConfig.cs
[^ksarmory-opticalhead]: src/KSArmory/Ksa/OpticalHead.cs
[^ksarmory-sensorpass]: src/KSArmory/Ksa/SensorPass.cs
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-viewportbase]: KSA.ViewportBase (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
[^ksa-viewportpresets]: KSA.ViewportPresets (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-dockingport]: KSA.DockingPort (decompiled, build 2026.10.10.5554)
