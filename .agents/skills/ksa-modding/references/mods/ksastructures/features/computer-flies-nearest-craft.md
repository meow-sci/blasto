---
type: Mod Feature
title: Fly the nearest craft from the office computer
description: Seated in the office chair, Enter hands the player's controls to the nearest craft by swapping Program.ControlledVehicle while the main view stays at the kitten's eyes and a borrowed secondary viewport films the craft for the monitor
mod: ksastructures
category: vehicles-and-parts
techniques: [imgui-drawlist, reflection, starmap-lifecycle, viewport-borrow]
hooks: [starmap StarMapAfterGui, reflection KSA.ViewportBase.OptionFlags (protected setter)]
lifecycle: [frame:draw-ui, frame:viewports, event:key]
game_types:
  - KSA.Program
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.ViewportRegistry
  - KSA.IGameViewport
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.Camera
  - KSA.FixedController
  - Brutal.ImGuiApi.ImGui
tags: [control-transfer, controlled-vehicle, secondary-viewport, camera, hotkeys, iva, remote-control]
---

Sit in the office chair, press **Enter**, and the player's controls go to the nearest craft: its
throttle, staging and attitude keys now fly that craft, while the main view stays where the seated
kitten is looking, at the monitor, and the monitor shows the craft from a chase camera. **Enter**
again logs off and hands control back to the chair.[^mod-computer] The mechanism is a single engine
assignment, `Program.ControlledVehicle = nearest`, wrapped in careful borrowing and returning of
cameras.

## How it works

1. **Seated means the flown craft is the chair.** Boarding the chair's EVA door makes the chair the
   controlled vehicle (see [office furniture](./office-furniture-craft.md)), so `Seated` simply checks
   `ControlledVehicle.Id == "Office chair"`.[^mod-computer][^ksa-kitteneva]
2. **Enter, read through ImGui.** `OfficeComputer.Draw` runs from `[StarMapAfterGui]` and tests
   `ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false)` or `KeypadEnter`, ignored while
   `ImGui.GetIO().WantTextInput`. It also draws a one-line hint on the foreground draw list.[^mod-computer][^mod-entry]
3. **Pick a craft.** `Start` walks every live vehicle and takes the nearest by ecliptic distance
   that is not a kitten, not the chair and not the monitor.[^mod-computer]
4. **Borrow a camera window.** `KsaWorld.TryOpenCameraWindow` calls
   `ViewportRegistry.TryOpenSecondaryViewport`, the call behind KSA's own **Add Camera** menu item,
   then makes it visible.[^mod-ksaworld][^ksa-viewportregistry][^ksa-program] `TryHideViewportWindow`
   clears `HasUi | RenderOrbitLines` through the protected `ViewportBase.OptionFlags` setter
   (reflection), so KSA renders the picture without wrapping it in its own window, and
   `ResizeViewport` asks for 768x416.[^mod-ksaworld]
5. **Hand over the controls.** The main view is recorded with `RememberMainView` before anything is
   written, and then `Program.ControlledVehicle = nearest`. The property setter only clears the old
   vehicle's held input; it does not move the camera.[^ksa-program] Keys reach the new craft because
   `Program.DispatchKeyEvent` forwards key events to `ControlledVehicle.OnKey` while
   `IsControlledVehicleActive`; the craft keeps its held input and its own `Vehicle.PrepareWorker`
   copies it into the worker state each step.[^ksa-program][^ksa-vehicle]
6. **Keep looking at the monitor.** Each step `Drive` holds the main view at the seated kitten's eye
   in the chair's own frame (`EyeAsmb`, `LookAsmb` transformed by `Body2Cce`) through
   `KsaWorld.TryLookFromMainViewport`, passing itself as the `IViewPose` so the replacement fixed
   controller re-asks the pose inside the engine's viewport pass (see
   [level horizon camera controller](./level-horizon-camera-controller.md)). The camera keeps
   following the chair, which boarding set.[^mod-computer][^ksa-kitteneva]
7. **Film the craft.** The borrowed window's camera is aimed every step from the office's side of
   the craft, 6 craft radii back (clamped 20-250 m) and 30 % of that above, with
   `KsaWorld.TryLookFromViewport`: unfollow, `CameraMode.Fixed`, `Camera.LookAt`, field of view,
   a nearby-body context recomputed for that camera, then `camera.OnFrame`, `Program.UpdateShaderData`
   and `Program.SetCameraUbo` so the window does not draw last frame's aim or sky.[^mod-ksaworld][^ksa-camera]
   The picture is put on the monitor (see [viewport image on the monitor](./viewport-image-on-monitor.md)).
8. **Log off.** `Stop` restores the monitor's textures, closes the window with
   `ViewportRegistry.ReleaseSecondaryViewport`, sets `ControlledVehicle` back to the chair and hands
   the main view back (`TryHandBackMainView`). `Drive` calls `Stop` itself if the chair or the craft
   dies, or the flight scene ends; `Unload` calls it too.[^mod-computer][^mod-entry]

## Lifecycle and timing

| Phase | Hook | Work | Why |
|---|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` -> `_computer.Draw()` | Enter, hint, ImGui-painted picture | ImGui sees the keys only between `NewFrame` and `Render` |
| `frame:draw-ui` (or the hidden-UI fallbacks) | the step -> `_computer.Drive(dtPlayer)` | Main view pose, window camera, monitor texture | Outside the flight gate so a view is still handed back when leaving; driven on player time because cameras must keep moving while paused |
| `frame:viewports` (next frame) | `LevelHorizonController.OnFrame` -> `TryPose` | Main view re-asked in phase | `StarMapAfterGui` runs after `OnFrameViewports`; a pose written there is consumed a frame late |
| `event:key` | engine (`Program.DispatchKeyEvent`) | Flight keys go to the played craft | Set by the `ControlledVehicle` swap |

The secondary viewport's camera is written after the engine's own viewport pass for this frame, so
the mod calls `camera.OnFrame(dt)` and re-uploads the viewport's camera and shader data itself.[^mod-ksaworld]

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L42-L72) | `Seated` and `Draw`: Enter handling |
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L82-L131) | `Start` and `Stop` |
| [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L134-L176) | `Drive` and the seated-eye pose |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L831-L904) | Open, close and strip a camera window |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L919-L1020) | `TryLookFromViewport` and `SetNearbyContext` |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L470-L566) | Remember and hand back the main view |

## Snippets

Enter in the chair:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L49-L66
    public void Draw()
    {
        try
        {
            bool enter = !ImGui.GetIO().WantTextInput
                         && (ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));

            if (Playing)
            {
                if (enter) { Stop(); return; }
                Paint();
                Hint($"Playing {KsaWorld.DisplayName(_played!)} - Enter logs off");
            }
            else if (Seated(out Vehicle chair))
            {
                if (enter) Start(chair);
                else Hint("Enter: play KSA on the computer");
            }
```

Taking control of the nearest craft:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L82-L106
    private void Start(Vehicle chair)
    {
        Vehicle? nearest = null;
        double best = double.MaxValue;
        double3 here = KsaWorld.PositionEcl(chair);
        foreach (Vehicle v in KsaWorld.Vehicles)
        {
            if (v is KittenEva || ReferenceEquals(v, chair) || !KsaWorld.IsAlive(v) || v.Id is Office.ChairId or Office.MonitorId) continue;

            double d = Vec.Len(KsaWorld.PositionEcl(v) - here);
            if (d < best) (best, nearest) = (d, v);
        }

        if (nearest is null) { Log.Info("office computer: there is no craft to play"); return; }
        if (!KsaWorld.TryOpenCameraWindow(out _window)) { Log.Warn("office computer: no camera window to show the game in"); return; }

        KsaWorld.TryHideViewportWindow(_window);
        KsaWorld.ResizeViewport(_window, new int2(768, 416));
        _shown = -1;
        _saved = KsaWorld.RememberMainView();
        _chair = chair;
        _played = nearest;
        Program.ControlledVehicle = nearest;
        Log.Info($"office computer: playing {KsaWorld.DisplayName(nearest)}, {best:F0} m away");
    }
```

Aiming a borrowed window after the engine's viewport pass:

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L954-L987
            if (viewport.Mode != CameraMode.Fixed) viewport.SetCameraMode(CameraMode.Fixed);

            Camera camera = viewport.BaseCamera;
            if (camera is null) return false;

            // Position and orientation together: setting one without the other leaves a frame
            // drawn from the old place looking the new way.
            camera.LookAt(eyeEcl, eyeEcl + Vec.Unit(forwardEcl) * 1000.0, upEcl);

            // SetFieldOfView does not clamp, and the projection throws outside (0, 180).
            float wanted = (float)Math.Clamp(double.IsFinite(fovDeg) ? fovDeg : DefaultFovDeg,
                                             MinFovDeg, MaxFovDeg);
            if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) >= 1e-3)
            {
                camera.SetFieldOfView(wanted);
            }

            SetNearbyContext(camera);

            // LookAt sets the rotation alone; the view matrix the render, the culling and every
            // projection read is rebuilt in Camera.OnFrame, which ran in the viewport pass before
            // this. Without it the window draws with last frame's aim: one frame of the line of
            // view's swing, a fixed angle that judders with the frame time and grows with the zoom.
            camera.OnFrame(dt);

            // Setting the fields is not enough on its own: the sky and atmosphere are shaded from
            // data the engine uploads per viewport, which by this point in the frame already holds
            // where the camera *was*. These recompute and re-upload it for the new position.
            if (Program.Instance is { } program)
            {
                program.UpdateShaderData(dt, viewport);
                program.SetCameraUbo(viewport);
            }

```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `Program.ControlledVehicle` (setter clears held input only) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515) | Control transfer |
| `Program.DispatchKeyEvent` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L2000) | Keys to `ControlledVehicle.OnKey` |
| `Program.UpdateShaderData(double, IViewport)`, `SetCameraUbo(IViewport)`, `FindNearbyCelestial` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2854) | Per-viewport re-upload after moving a camera late |
| `Vehicle.PrepareWorker` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Copies the vehicle's held input into its worker state |
| `KittenEva.BoardVehicle` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L925-L974) | Seat sets control and camera follow |
| `ViewportRegistry.TryOpenSecondaryViewport`, `ReleaseSecondaryViewport`, `GameViews` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L52-L230) | Lease one of the four spare windows |
| `GameViewport.SetCameraMode`, `GetCamera`, `FixedController` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L311) | Fixed mode on the borrowed window |
| `Camera.LookAt`, `SetFieldOfView`, `OnFrame`, `Unfollow` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L211-L500) | Aim the window |

## Pitfalls and version notes

- **`ControlledVehicle` and the camera are independent.** Setting the property does not change
  what any camera follows, which is what lets the player fly one craft while watching another.
  Anything else that assumes "the camera follows the controlled vehicle" (UI panels, gauges) shows
  the played craft.
- **Restore in order.** Record the main view before the first write: `Fixed` mode clears the follow,
  so a recording taken afterwards cannot be restored.[^mod-ksaworld]
- **Unfollow a borrowed window every frame.** KSA's next/previous-vessel keys act on the hovered
  viewport and can attach a follow to the window at any time.[^mod-ksaworld]
- **Clamp the field of view.** `Camera.SetFieldOfView` does not clamp, and the projection throws
  for zero or more than 180 degrees.[^mod-ksaworld]
- **Four spare windows exist in this build**, created at startup; `TryOpenSecondaryViewport` fails
  when the player already has them all open.

## See also

- [Office furniture as single-part craft](./office-furniture-craft.md) - the chair and the monitor
- [Viewport image on the monitor](./viewport-image-on-monitor.md) - how the picture reaches the screen mesh
- [Level horizon camera controller](./level-horizon-camera-controller.md) - how the main view is held
- [First-person kitten view](./first-person-kitten-view.md) - the same borrowing for a kitten on foot
- [Record the main view before the first write when flying another craft or a first-person kitten](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-3.md) - how-to article demonstrating this feature
- [Open a secondary viewport, make it visible and re-resolve its index by reference every frame](../../../howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-1.md) - how-to article demonstrating this feature
- [Sit in a chair to fly another craft - swap ControlledVehicle, borrow a camera window and restore everything on log-off](../../../howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-3.md) - how-to article demonstrating this feature
- [Switch the controlled vehicle and restore the main view afterwards](../../../howto/vehicles-and-parts/vehicles/take-control-of-another-vehicle/take-control-of-another-vehicle-1.md) - how-to article demonstrating this feature

[^mod-computer]: src/KSAStructures/Ksa/OfficeComputer.cs
[^mod-ksaworld]: src/KSAStructures/Ksa/KsaWorld.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
