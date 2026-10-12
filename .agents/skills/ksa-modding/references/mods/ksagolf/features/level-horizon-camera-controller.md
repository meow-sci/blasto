---
type: Mod Feature
title: Level-horizon camera controller swapped into the main viewport
description: Subclass KSA's FixedController to supply a rate-limited up vector, re-pose the view inside the viewport pass and take mouse input, installed by a checked reflection write into a get-only backing field
mod: ksagolf
category: camera
techniques: [reflection, controller-subclass, mouse-capture, starmap-lifecycle, headless-tests]
hooks:
  - reflection KSA.GameViewport.<FixedController>k__BackingField
  - subclass KSA.FixedController.OnFrame
  - subclass KSA.Controller.OnKey/OnMouseButton/OnCursorPos/OnScroll/IsMouseDrag/GetCursorMode
  - starmap StarMapAfterGui
  - starmap StarMapUnload
lifecycle: [frame:input, frame:viewports, frame:draw-ui]
game_types:
  - KSA.FixedController
  - KSA.Controller
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.Camera
  - KSA.OrbitView
  - KSA.Program
  - KSA.Vehicle
  - KSA.ImGuiBackendGlfwImpl
  - Brutal.GlfwApi.GlfwCursorMode
tags: [camera, controller, fixed-camera, roll, up-vector, viewport, reflection, input, mouse, orbit]
---

Every view KSAGolf takes over is driven through the engine's `Fixed` camera mode, which puts the
camera at the followed object plus `CameraOffset`, looking along `CameraRotation`. That covers the
putting view, first person, the course builder's top-down view and the bridge's composed shots.
The stock `FixedController` has two limits:

- It invents its own roll.
- It reads no input.

`Ksa/LevelHorizonController.cs` is an ordinary subclass that adds both. Installing it is the one
unusual step. `IGameViewport.FixedController` is get-only and `GameViewport`'s setter is
protected, so `KsaWorld` writes the compiler-generated backing field by reflection, checks that the
write took, and otherwise leaves KSA's controller in place.[^golf-levelhorizon][^ksa-gameviewport]

## How it works

**Why the stock controller is not enough.**

- `FixedController.OnFrame` builds the camera basis by crossing `CameraRotation` with the +Z axis
  of `GetFrame2Ecl(following, orbitView.ReferenceFrame)`.
- `GetFrame2Ecl` switches on the followed object's type: `Vehicle`, `Celestial`, or
  `VehicleEditingSpace`. Anything else gets `Identity`, so the roll is fixed by ecliptic +Z, and for
  a vehicle it is fixed by whatever reference frame the player's orbit view is in.
- A view along that axis makes the cross product zero, and `Normalized()` divides by
  zero.[^ksa-fixedcontroller][^golf-cameras]
- Nothing on `Camera`, `OrbitView` or the controller lets a mod state the up vector.

**What the subclass adds.**

| Member | Behaviour |
|---|---|
| `UpEcl` | The wanted up in the ecliptic. Zero means "no opinion": `Forget()` and run `base.OnFrame`, exactly the stock behaviour |
| `OnFrame` | First `AskThePoseSource()`. Then `StableUp.Try(forward, UpEcl, lastUp, step)` carries last frame's up towards the wanted one, by at most π rad/s × dt (or at once when the source says `UpIsExact`), and by less the more the view lies along it. Then it makes the same two writes the engine makes, `Camera.PositionEcl = following + CameraOffset` and `Camera.LocalRotation = LookAtRotation(forward, up)`, with the up substituted. If no up is usable it falls back to `base.OnFrame` |
| `Pose` (`IViewPose`) | The borrower is asked again for offset, forward, up and FOV **inside** the viewport pass, against `following.GetPositionEcl()` as the engine holds it at that instant. A refusal or exception keeps last frame's fields |
| `Probe` | Logs any frame whose roll or aim jumps more than about 5°. More than 20° is always logged (up to 12 times), the rest only at debug level |
| `OnMouseButton` / `OnCursorPos` / `OnScroll` | While a borrower exposes `Orbit` (an `IMouseDrag`): a right press with no modifier or with Shift starts a drag, as KSA's orbit camera does; cursor moves feed it; the wheel zooms it. While `Swing` is set: the left button is swallowed and cursor positions go to `IMouseSwing.Cursor`. While `TakesInput` (course builder): every button and scroll is swallowed |
| `OnKey` | With `TakesInput`, swallows letters, digits, Space and Insert to PageDown, so the kitten does not walk off. Escape and the function keys still reach the game |
| `IsMouseDrag` | True while dragging or taking input. `Vehicle.OnMouseButton` asks the active controller this before a right-release opens the part window under the cursor[^ksa-vehicle] |
| `GetCursorMode` | `Disabled` (hidden, unbounded) while swinging or dragging |

**Installing it (`KsaWorld.LevelTheHorizon`).** This runs lazily from
`KsaWorld.TryLookFromMainViewport`, the single entry every borrower uses.

1. If the viewport's `FixedController` is already a `LevelHorizonController`, return it.
2. Look up `<FixedController>k__BackingField` on the viewport's runtime type, once, and warn once if
   it is missing.
3. Construct the subclass on `viewport.BaseCamera`, remember the stock controller, and
   `SetValue`.
4. **Check** `ReferenceEquals(viewport.FixedController, level)`. A silent no-op would look exactly
   like success. On failure the stock controller stays, with an engine-derived roll and a view aim
   that lags a frame.

`TryLookFromMainViewport` then:

1. Writes `CameraRotation` and `CameraOffset` **before** switching the mode, because a frame drawn
   in Fixed with a zero rotation is the crash.
2. Sets `UpEcl` and `Pose` on every call. A null `Pose` takes the previous borrower off.
3. Sets the FOV, clamped to 1-120°, because `Camera.SetFieldOfView` does not clamp.
4. Calls `SetCameraMode(Fixed)`.

With the stock controller still in place, it refuses a forward within about 2.6° of ecliptic +Z.
`KSAGolfMod.Unload` calls `RestoreStockController`, and the mod's comment says this stops the
subclass outliving the mod "for the rest of the session". StarMap only invokes `[StarMapUnload]` at
process exit, after `Program.Main` has returned and disposed the game, so in practice the subclass
stays installed for the whole session and the restore runs against a disposed `Program`. The
fault-limit shutdown does not restore it either.[^golf-ksaworld][^golf-mod][^starmap-core]

**`Sim/StableUp.cs`.** This is the roll as a control loop, not a lookup.

- The carried up is always the reference.
- The wanted up only pulls on it, scaled by its "authority": the sine of its angle to the view.
- A threshold rule, switching between the wanted up and the carried one, would flip the picture
  wherever it switched. The author measured 89° of roll for 1.3° of aim.
- The headless tests check that the roll never jumps as the view creeps past its own up, that the
  result is always orthogonal to the view, and that the per-frame step limit holds.[^golf-stableup][^golf-test-stableup]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| Input virtuals (`OnMouseButton`, `OnCursorPos`, `OnScroll`, `OnKey`) | GLFW callbacks in `Glfw.PollEvents`, inside `Program.PrepareFrame`, through `ImGuiBackendGlfwImpl` into `Program.OnMouseButton` / `OnCursorPos` / `OnScroll` / `OnKey`, then to the input viewport's active controller[^ksa-imguibackend][^ksa-program] | `frame:input` |
| `OnFrame`, including `AskThePoseSource` | `Program.OnFrameViewports` → `GameViewport.OnFrame` → active controller `OnFrame`, then `Camera.OnFrame` builds the view and projection matrices | `frame:viewports` |
| Borrowers write offset, forward, up and `Pose`; the controller is installed on first use | `TryLookFromMainViewport` from `Golf.DriveView` / `CourseBuilder.Step` in the step (GUI postfix), and `SceneCamera` from the bridge | `frame:draw-ui` (or the step's fallbacks) |
| Stock controller restored | `[StarMapUnload]`, which StarMap calls only at process exit | (shutdown) |

**Why `Pose` exists.** All three StarMap per-frame hooks run after `OnFrameViewports`. A camera
aimed from the GUI postfix is therefore used on the **next** frame's matrices, while the followed
body is drawn where it is now. The error is one frame of the target's angular motion. It scales with
simulation speed: invisible while paused, a couple of pixels at 1×, a third of the picture at 16×
on a close target. Re-asking the borrower inside the controller's `OnFrame` puts the aim in phase
with the frame being drawn.[^golf-levelhorizon][^golf-frameorder] What is safe to read at that
point:

- the followed object's position, already advanced by this frame's `ApplyVehicleSolvers`;
- the borrower's own state from the previous step.

Nothing there may throw: the engine's `App.Run` loop has no catch.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L39-L191) | The subclass: up, input overrides, `OnFrame` |
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L253-L292) | `AskThePoseSource`: in-phase pose and FOV |
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L295-L346) | `IViewPose` and `IMouseSwing` contracts |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1697-L1790) | Backing-field lookup, checked install, stock restore |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1821-L1899) | `TryLookFromMainViewport` and `StopDrivingMainView` |
| [src/KSAGolf/Sim/StableUp.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StableUp.cs) | Carried, rate-limited roll reference |
| [src/KSAGolf/Sim/IMouseDrag.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IMouseDrag.cs) | The drag contract `HeadLook` and `ChaseOrbit` implement |

## Snippets

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1709-L1715
    private static FieldInfo? FixedControllerField(IGameViewport viewport)
    {
        if (_lookedForFixedControllerField) return _fixedControllerField;
        _lookedForFixedControllerField = true;

        _fixedControllerField = viewport.GetType().GetField(
            "<FixedController>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
```

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1743-L1755
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
```

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L162-L190
        if (Vec.Len2(Vec.Unit(UpEcl)) < 0.5)
        {
            Forget();
            base.OnFrame(inViewport, inDeltaTime);
            return;
        }

        // How fast the wanted up may pull the carried one. Fast enough that levelling looks
        // immediate on any ordinary slew, slow enough that it cannot snap: at 180 deg/s a frame
        // moves it three degrees, which is below what anyone sees as a jump. A view the mouse turns
        // directly states its own up exactly, and a limit there is roll trailing a hard turn.
        double step = Pose?.UpIsExact == true ? Math.PI : LevelRateRad * Math.Clamp(inDeltaTime, 0.0, 0.1);

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
```

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L99-L115
    public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action,
                                       GlfwModifier mods)
    {
        if (TakesInput) return true;
        if (Swinging is not null && button == GlfwMouseButton.Number1) return true;

        if (Orbit is not { } orbit || button != GlfwMouseButton.Number2)
        {
            return base.OnMouseButton(window, button, action, mods);
        }

        // The modifiers KSA's orbit camera starts a drag on, so a chord it leaves alone stays alone.
        if (action == GlfwButtonAction.Press && (mods == 0 || (mods & GlfwModifier.Shift) != 0)) orbit.Press();
        else if (action == GlfwButtonAction.Release) orbit.Release();

        return true;
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `FixedController` (`CameraOffset`, `CameraRotation`, `OnFrame`, `GetFrame2Ecl`) | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L48) | Base class; the stock roll rule the subclass replaces |
| `Controller` virtuals | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L22-L94) | `OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll`, `GetCursorMode`, `IsMouseDrag`, `OnFrame` |
| `GameViewport.FixedController { get; protected set; }` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42) | Auto-property whose backing field is written; `BaseCamera` at L30 |
| `IGameViewport.FixedController` / `SetCameraMode` | [current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L31-L39) | Get-only read used to check the write; mode switch |
| `GameViewport.OnFrame` / `GetActiveController` / `NextCameraMode` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124) | Controller then camera per frame; Shift+C has no `Fixed` case (L314-L330) |
| `Camera.LookAtRotation` / `PositionEcl` / `LocalRotation` / `Following` / `SetFieldOfView` / `ChangeFieldOfView` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203) | The two writes; FOV set (unclamped) against the zoom keys' 15-120° clamp (L417-L462) |
| `Program.OnFrameViewports` / `PrepareFrame` / `OnMouseButton` / `OnCursorPos` / `OnScroll` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2689) | Where `OnFrame` and the input virtuals are called from |
| `Vehicle.OnMouseButton` (right release) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3669-L3681) | Asks `IsMouseDrag()` before opening a part's window |

## Pitfalls and version notes

- **One name, no signature.** `<FixedController>k__BackingField` is a compiler-generated name.
  The decomp shows only the auto-property, so the field name is inferred, not visible. The mod's
  API-surface tooling cannot pin it; the property's getter is pinned. If the field is gone, the mod
  warns once and keeps the stock controller. That costs the levelled horizon and the in-phase aim,
  not a crash.[^golf-ksaworld]
- **Who can take the view back.** `FixedController` reads no input, and `GameViewport.NextCameraMode`
  has no `Fixed` case, so the camera-mode key does nothing in Fixed.[^ksa-gameviewport] A mod that
  borrows the view owes the player a way out (see
  [Borrowed chase camera](../features/borrowed-chase-camera.md)).
- **Releases over a panel are lost.** `Program.OnMouseButton` returns before any controller while
  ImGui wants the mouse on the main view. A drag must be endable from the controller's own frame
  (`ChaseOrbit.Advance(seconds, buttonHeld)` does that). `HeadLook` has no such check.[^ksa-program]
- **FOV.** `Camera.SetFieldOfView` does not clamp; it rebuilds the projection at once. The mod's
  comment says that projection throws for a field of zero or of half a turn or more. The zoom keys
  (`ChangeFieldOfView`) clamp to 15-120°, so a value narrower than 15° is lost on the next keypress.
  The mod clamps to 1-120° and restates the value every frame.[^ksa-camera][^golf-ksaworld]
- **Version notes.** Built against 2026.9.22.5482. In 2026.10.10.5554:
  - `FixedController.OnFrame` now skips `ViewportType.CharacterPortrait` viewports, and its
    `OnSwitchOn` alert text is localised. The subclass is installed only on the main viewport and
    replaces `OnFrame`, so neither change reaches it.[^ksa-fixedcontroller]
  - The backing-field pattern, `GetFrame2Ecl` and the `Controller` virtuals are unchanged.

## See also

- [Borrowing the main view and handing it back](../features/borrowed-chase-camera.md)
- [First-person kitten view that keeps the shadow](../features/first-person-kitten-view.md)
- [Putting session with a mouse-swung club](../features/putting-session.md)
- [Top-down minigolf course builder](../features/minigolf-course-builder.md)
- [Visual testing captures](../features/visual-testing-captures.md)
- [Frame and epoch draw anchor](../features/frame-epoch-draw-anchor.md)
- [API surface record and assembly lock](../features/api-surface-and-assembly-lock.md)
- [Never enter Fixed with a zero or along-axis forward, and re-assert the mode every step](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-1.md) - how-to article demonstrating this feature
- [Carry a rate-limited StableUp reference instead of switching up vectors near the vertical](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-5.md) - how-to article demonstrating this feature
- [Install a FixedController subclass by writing the auto-property backing field, check it took and restore on unload](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-1.md) - how-to article demonstrating this feature
- [Route orbit drag, club swing and builder input through the controller and keep ImGui-captured releases from sticking a drag](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-5.md) - how-to article demonstrating this feature
- [Read the decompiled frame order, per-mode behaviour and crash inventory before writing to a camera](../../../howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-1.md) - how-to article demonstrating this feature
- [Ask a pose source from inside a FixedController subclass's OnFrame so the aim matches the frame being drawn](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-1.md) - how-to article demonstrating this feature
- [Treat the field of view as one half of a borrowed view and always return it first](../../../howto/camera/main-view/override-the-camera-field-of-view/override-the-camera-field-of-view-3.md) - how-to article demonstrating this feature
- [Pose the eye from the head bone in the golf pose with a pitch-tilted up, a captured-cursor look and a drawn turn for a standing kitten](../../../howto/camera/special-views/first-person-camera-for-a-kitten/first-person-camera-for-a-kitten-2.md) - how-to article demonstrating this feature
- [Capture the cursor and drive a kitten action with raw mouse deltas](../../../howto/kittens/eva/steer-a-walking-kitten/steer-a-walking-kitten-2.md) - how-to article demonstrating this feature

[^golf-levelhorizon]: src/KSAGolf/Ksa/LevelHorizonController.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-stableup]: src/KSAGolf/Sim/StableUp.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-cameras]: docs/KSA-CAMERAS.md
[^golf-frameorder]: docs/KSA-FRAME-ORDER.md
[^golf-test-stableup]: tests/KSAGolf.Tests/StableUpTests.cs
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-imguibackend]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^starmap-core]: StarMap.Core/StarMapCore.cs
