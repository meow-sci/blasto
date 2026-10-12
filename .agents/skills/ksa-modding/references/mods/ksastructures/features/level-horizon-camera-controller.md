---
type: Mod Feature
title: A replacement fixed camera controller with an up vector and an in-phase pose
description: Subclasses KSA's FixedController, installs it into the main viewport through the auto-property backing field, and has it ask the mod for the camera pose and roll inside the engine's own viewport pass, with right-drag look and smooth roll
mod: ksastructures
category: camera
techniques: [reflection, controller-subclass, starmap-lifecycle]
hooks:
  - reflection KSA.GameViewport.<FixedController>k__BackingField
  - override KSA.FixedController.OnFrame
  - override KSA.Controller.OnMouseButton
  - starmap StarMapUnload
lifecycle: [frame:viewports, frame:input, frame:draw-ui]
game_types:
  - KSA.FixedController
  - KSA.Controller
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.Camera
  - KSA.CameraMode
  - KSA.OrbitController
  - KSA.Vehicle
  - Brutal.GlfwApi.GlfwMouseButton
  - Brutal.GlfwApi.GlfwCursorMode
tags: [camera, fixed-controller, camera-mode, mouse-look, right-drag, roll, viewport]
---

Every view the mod takes over (the first-person kitten, the seated kitten at the computer, the
developer bridge's composed shots) goes through one piece of infrastructure: KSA's own `Fixed`
camera mode, driven by a subclass of KSA's `FixedController` that the mod swaps into the main
viewport. The subclass adds what the stock controller lacks: an explicit up vector, a pose source it
asks at the exact moment the engine builds the frame's view, and right-mouse-button look that
coexists with KSA's input handling.[^mod-level]

## How it works

1. **The stock controller.** `FixedController.OnFrame` places a following camera at
   `following.GetPositionEcl() + CameraOffset` looking along `CameraRotation`, and derives "up" by
   crossing `CameraRotation` with +Z of `GetFrame2Ecl(following, OrbitView.ReferenceFrame)`.[^ksa-fixedcontroller]
   `GetFrame2Ecl` only understands `Vehicle`, `Celestial` and `VehicleEditingSpace`; anything else
   gets `doubleQuat.Identity`. A zero `CameraRotation` (what `GameViewport.ResetToDefaults` writes)
   makes the cross product zero and its normalization undefined, so the rotation must be written
   before the mode is set.[^ksa-gameviewport][^mod-ksaworld]
2. **Subclass, do not patch.** `FixedController` is public, unsealed, with a public constructor and a
   virtual `OnFrame`; the input methods are virtual on `KSA.Controller`.[^ksa-fixedcontroller][^ksa-controller]
   `LevelHorizonController` overrides `OnFrame`, `OnMouseButton`, `OnCursorPos`, `OnScroll`,
   `IsMouseDrag` and `GetCursorMode`.[^mod-level]
3. **Install through the backing field.** `IGameViewport.FixedController` is get-only and
   `GameViewport.FixedController` has a protected setter, so `KsaWorld.LevelTheHorizon` writes
   `<FixedController>k__BackingField` by reflection, checks that the property now returns the new
   controller, keeps the stock one, and puts it back on `[StarMapUnload]`
   (`RestoreStockController`).[^ksa-igameviewport][^ksa-gameviewport][^mod-ksaworld][^mod-entry]
   Because `GameViewport.GetActiveController()` returns the `FixedController` property for
   `CameraMode.Fixed`, the engine now calls the mod's class whenever the main view is in `Fixed`.[^ksa-gameviewport]
4. **Taking and returning a view.** `TryLookFromMainViewport` writes `CameraRotation` and
   `CameraOffset` first, then `UpEcl` and the `IViewPose`, then the field of view (clamped 1-120
   degrees), and only then `SetCameraMode(CameraMode.Fixed)`. `RememberMainView` records the
   followed object, the mode and the field of view before the first write; `TryHandBackMainView`
   clears the pose and up, restores the field and mode, and re-follows the saved target only if the
   view still follows the borrower's object.[^mod-ksaworld] The camera itself keeps following the
   kitten or chair the whole time, so the offset is always relative to the followed craft.
5. **Asking the pose in phase.** StarMap's GUI hooks run after `OnFrameViewports`, where
   `GameViewport.OnFrame` calls the active controller's `OnFrame` and then `Camera.OnFrame`, which
   builds the view and view-projection matrices.[^ksa-program][^ksa-gameviewport][^ksa-camera] A pose
   written from the GUI hook is drawn a frame late. So `OnFrame` first calls `IViewPose.TryPose`
   with the followed object's position *now*, and only then computes the rotation.[^mod-level]
6. **Smooth roll.** With an up vector supplied, `StableUp.Try` corrects last frame's up toward the
   wanted one by at most pi rad/s, instead of switching rules near the vertical, then the controller
   writes `Camera.PositionEcl` and `Camera.LocalRotation = Camera.LookAtRotation(forward, up)` as the
   stock controller does. A zero `UpEcl` hands the frame to `base.OnFrame`.[^mod-level][^mod-stableup]
   `StableUp` lives in `Sim/` and is tested headlessly.[^mod-stableup-tests]
7. **Right-mouse look that behaves like KSA's.** When the pose source exposes an `IMouseDrag`
   (`Orbit`), the controller takes `GlfwMouseButton.Number2` (the right button) with no modifier or
   Shift, as `OrbitController` does, forwards cursor moves, and reports a drag from `IsMouseDrag`.
   `Vehicle.OnMouseButton` asks the active controller `IsMouseDrag()` before a right-button release
   opens the part window under the cursor, so a look-drag never opens one. `GetCursorMode` returns
   `Disabled` while dragging, as the orbit camera does.[^mod-level][^ksa-vehicle][^ksa-orbitcontroller]
   `HeadLook` uses the orbit camera's own constants: 0.003 rad per pixel and a 2 px^2 drag threshold.[^mod-headlook][^ksa-orbitcontroller]

## Lifecycle and timing

| Phase | Where | What |
|---|---|---|
| first borrow, usually `frame:draw-ui` | `KsaWorld.TryLookFromMainViewport` from the mod's step | Swap the controller in once; write offset, rotation, up, pose, FOV, mode |
| `frame:input` | `Glfw.PollEvents` in `Program.PrepareFrame` -> `Program.OnMouseButton` -> active controller | Right-drag, cursor, scroll |
| `frame:viewports` | `GameViewport.OnFrame` -> `LevelHorizonController.OnFrame` | Ask the pose source, compute roll, write the camera; `Camera.OnFrame` follows immediately |
| `StarMapUnload` | `KsaWorld.RestoreStockController` | Put KSA's controller back |

Everything inside `OnFrame` runs in the engine's frame loop, so `AskThePoseSource` catches every
exception and keeps last frame's pose.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/LevelHorizonController.cs#L38-L158) | The subclass: input overrides and `OnFrame` |
| [src/KSAStructures/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/LevelHorizonController.cs#L232-L291) | `AskThePoseSource` and the `IViewPose` contract |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L568-L664) | Backing-field install and restore |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L666-L748) | `TryLookFromMainViewport` |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L470-L566) | Remember and hand back the main view |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L778-L817) | Stop driving and restore mode and FOV |
| [src/KSAStructures/Sim/StableUp.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/StableUp.cs) | Rate-limited roll reference |
| [src/KSAStructures/Sim/IMouseDrag.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/IMouseDrag.cs) | Drag contract the controller forwards to |

## Snippets

Swapping the controller in through its backing field:

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L608-L640
    private static FixedController? LevelTheHorizon(IGameViewport viewport)
    {
        if (viewport.FixedController is LevelHorizonController already) return already;

        try
        {
            if (viewport.BaseCamera is not { } camera) return viewport.FixedController;
            if (FixedControllerField(viewport) is not { } field) return viewport.FixedController;

            var level = new LevelHorizonController(camera);

            _stockFixedController ??= viewport.FixedController;
            field.SetValue(viewport, level);

            // The write is the whole point, and a silent no-op would read exactly like a working
            // swap: everything downstream still finds *a* controller.
            if (!ReferenceEquals(viewport.FixedController, level))
            {
                Log.Warn("camera: the FixedController write did not take - keeping KSA's own");
                _stockFixedController = null;
                return viewport.FixedController;
            }

            Log.Info("camera: levelling the horizon on the main view");

            return level;
        }
        catch (Exception e)
        {
            Log.Warn($"could not level the horizon, using KSA's own controller: {e.Message}");
            return viewport.FixedController;
        }
    }
```

Taking the right button and the cursor only when the borrower wants them:

```csharp
// src/KSAStructures/Ksa/LevelHorizonController.cs L77-L115
    public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action,
                                       GlfwModifier mods)
    {
        if (Orbit is not { } orbit || button != GlfwMouseButton.Number2)
        {
            return base.OnMouseButton(window, button, action, mods);
        }

        // The modifiers KSA's orbit camera starts a drag on, so a chord it leaves alone stays alone.
        if (action == GlfwButtonAction.Press && (mods == 0 || (mods & GlfwModifier.Shift) != 0)) orbit.Press();
        else if (action == GlfwButtonAction.Release) orbit.Release();

        return true;
    }

    public override bool OnCursorPos(GlfwWindow window, double2 pos)
    {
        if (Orbit is not { } orbit) return base.OnCursorPos(window, pos);

        orbit.Move(pos.X, pos.Y);
        return orbit.Dragging;
    }

    public override bool OnScroll(GlfwWindow window, double2 offset)
    {
        if (Orbit is not { } orbit) return base.OnScroll(window, offset);

        orbit.Scroll(offset.Y);
        return true;
    }

    // Load-bearing: the engine asks the active controller this before a right-release opens the
    // window of whatever part is under the cursor, so a drag ending over a craft opens nothing.
    public override bool IsMouseDrag() => Orbit?.Dragging == true || base.IsMouseDrag();

    // Hidden and unbounded while dragging, as the orbit camera's is, so a drag does not stop at the
    // edge of the screen.
    public override GlfwCursorMode GetCursorMode()
        => Orbit?.Dragging == true ? GlfwCursorMode.Disabled : base.GetCursorMode();
```

The frame: ask the pose, then build the rotation with the supplied up:

```csharp
// src/KSAStructures/Ksa/LevelHorizonController.cs L117-L121, L137-L158
    public override void OnFrame(IViewport inViewport, double inDeltaTime)
    {
        AskThePoseSource();

        double3 forward = Vec.Unit(CameraRotation);
        // ...
        // How fast the wanted up may pull the carried one. Fast enough that levelling looks
        // immediate on any ordinary slew, slow enough that it cannot snap: at 180 deg/s a frame
        // moves it three degrees, which is below what anyone sees as a jump.
        double step = LevelRateRad * Math.Clamp(inDeltaTime, 0.0, 0.1);

        if (!StableUp.Try(forward, UpEcl, _lastUp, step, out double3 up))
        {
            base.OnFrame(inViewport, inDeltaTime);
            return;
        }

        Probe(forward, up);
        _lastUp = up;

        if (Camera.Following is not { } following) return;

        // The same two writes the engine makes, in the same order, with the up substituted.
        // LookAtRotation orthogonalises the pair itself, so up is a reference rather than a
        // constraint and need not be perpendicular to the view.
        Camera.PositionEcl = following.GetPositionEcl() + CameraOffset;
        Camera.LocalRotation = Camera.LookAtRotation(forward, up);
    }
```

Asking the borrower at the instant the engine uses the answer:

```csharp
// src/KSAStructures/Ksa/LevelHorizonController.cs L232-L259
    private void AskThePoseSource()
    {
        if (Pose is not { } source) return;
        if (Camera.Following is not { } following) return;

        try
        {
            if (!source.TryPose(following.GetPositionEcl(), out double3 offset,
                                out double3 forward, out double3 up, out double fovDeg))
            {
                return;
            }

            if (!Vec.IsFinite(offset) || Vec.Len2(forward) < 0.5) return;

            CameraOffset = offset;
            CameraRotation = forward;
            UpEcl = up;

            // Clamped rather than refused. The engine's own setter does not clamp and the
            // projection throws outside (0, pi) -- from in here, which is the engine's loop.
            KsaWorld.TrySetMainViewFov(fovDeg);
        }
        catch
        {
            // Keep whatever was written a frame ago rather than dropping the view.
        }
    }
```

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `FixedController.OnFrame`, `CameraOffset`, `CameraRotation` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L7-L48) | Base class and its two inputs |
| `Controller.OnMouseButton`, `OnCursorPos`, `OnScroll`, `IsMouseDrag`, `GetCursorMode` | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L27-L92) | Virtual input hooks |
| `GameViewport.FixedController` (protected setter), `GetActiveController`, `SetCameraMode`, `OnFrame`, `ResetToDefaults` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42-L120) | Install point and frame call |
| `IGameViewport.FixedController` | [current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L31) | Get-only interface |
| `Camera.LookAtRotation`, `LocalRotation`, `PositionEcl`, `Following`, `OnFrame` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497) | View matrices built after the controller |
| `OrbitController` right-drag constants | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L370-L386) | Behaviour copied for look |
| `Vehicle.OnMouseButton` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3680) | Part click suppressed during a drag |
| `Program.OnFrame` (`OnFrameViewports` before `OnDrawUiViewports`) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2270) | Why the pose must be asked in the controller |

## Pitfalls and version notes

- **The backing-field name is not compiler-checked.** If KSA renames the property or gives it a
  public setter, the reflection returns null; the mod logs once and keeps KSA's controller (no
  levelled horizon, aim a frame late).
- **Write `CameraRotation` before `SetCameraMode(CameraMode.Fixed)`.** A frame drawn in `Fixed` with
  a zero rotation produces a non-finite camera.
- **`FixedController.OnSwitchOn` shows KSA's "fixed camera" alert**; the subclass inherits it.
- **`FixedController.OnKey` returns false**, so keyboard input keeps flowing to the controlled
  vehicle while a view is borrowed.[^ksa-fixedcontroller]
- **The controller stays installed for the session**; with no up and no pose it behaves exactly like
  KSA's own.

## See also

- [First-person kitten view](./first-person-kitten-view.md) - a pose source with right-drag look
- [Computer flies the nearest craft](./computer-flies-nearest-craft.md) - a pose source with no look
- [Developer file bridge and MCP server](./dev-file-bridge-and-mcp.md) - composed shots through the same path
- [Headless tests and CI](./headless-tests-and-ci.md) - `StableUp` and `HeadLook` tests
- [Carry a rate-limited StableUp reference instead of switching up vectors near the vertical](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-5.md) - how-to article demonstrating this feature
- [Install a FixedController subclass by writing the auto-property backing field, check it took and restore on unload](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-1.md) - how-to article demonstrating this feature
- [Take right-drag look through the controller with orbit-camera constants and IsMouseDrag to keep part windows shut](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-4.md) - how-to article demonstrating this feature
- [Ask a pose source from inside a FixedController subclass's OnFrame so the aim matches the frame being drawn](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-1.md) - how-to article demonstrating this feature

[^mod-level]: src/KSAStructures/Ksa/LevelHorizonController.cs
[^mod-ksaworld]: src/KSAStructures/Ksa/KsaWorld.cs
[^mod-stableup]: src/KSAStructures/Sim/StableUp.cs
[^mod-headlook]: src/KSAStructures/Sim/HeadLook.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-stableup-tests]: tests/KSAStructures.Tests/StableUpTests.cs
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-controller]: KSA.Controller (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-igameviewport]: KSA.IGameViewport (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-orbitcontroller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
