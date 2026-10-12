---
type: How-To
title: Read the decompiled frame order, per-mode behaviour and crash inventory before writing to a camera
description: Where each KSA camera mode writes the pose, in what order the viewport pass runs relative to StarMap's GUI hooks, which mode changes are immediate, what Fixed requires and which inputs crash the frame loop
category: camera
group: controllers
topic: understand-camera-modes-and-controllers
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksarmory/features/chase-camera-on-a-round
techniques: [starmap-lifecycle, reflection, controller-subclass]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.GameViewport.<FixedController>k__BackingField
  - subclass KSA.FixedController.OnFrame
lifecycle: [frame:viewports, frame:controller, frame:input, frame:draw-ui, frame:render]
game_types:
  - KSA.Program
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.ViewportRegistry
  - KSA.Controller
  - KSA.Camera
  - KSA.CameraMode
  - KSA.FixedController
  - KSA.OrbitController
  - KSA.FlyController
  - KSA.IVAController
  - KSA.MapController
tags: [camera, viewport, controller, frame-order, fixed-camera, crash, reference]
---
Every camera mod starts from the same facts, so this article is the reference the other camera
how-tos link to. It describes what the decompiled engine does with a camera in build 2026.10.10.5554:
which object holds the camera, which controller writes it, in which order the frame calls things, what
a mode change does, and which inputs crash the process. Two mods apply the rules in practice:
KSAGolf's level-horizon controller ([Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)) and KSArmory's
chase camera ([Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)); both lean on the analysis in their
`docs/KSA-CAMERAS.md` ([docs/KSA-CAMERAS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-CAMERAS.md)), which this article re-checks against the
current decompile.

Nothing here needs more than a StarMap mod that can run code in `[StarMapAfterGui]`
([lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)); the later steps that swap a controller also need reflection.

## Background: how the game does it

**One `GameViewport` owns two cameras and five controllers.** `GameViewport` holds `BaseCamera` and
`MapCamera` plus an `OrbitController`, `FlyController`, `MapController`, `IvaController` and
`FixedController`, all constructed in its constructor; the four other controllers act on
`BaseCamera`, `MapController` on `MapCamera` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L30-L42), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L87-L104)).
`ViewportBase.Mode` is a `{ get; protected set; }` property ([current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L36)), and two switches
pick the live controller and camera from it:

```csharp
// current/decomp/KSA/GameViewport.cs L284-L300
public Controller GetActiveController()
{
	return base.Mode switch
	{
		CameraMode.Orbit => OrbitController, 
		CameraMode.Free => FlyController, 
		CameraMode.Map => MapController, 
		CameraMode.IVA => IvaController, 
		CameraMode.Fixed => FixedController, 
		_ => throw new ArgumentOutOfRangeException(), 
	};
}

public override Camera GetCamera()
{
	return (base.Mode == CameraMode.Map) ? MapCamera : BaseCamera;
}
```

`CameraMode` has exactly five values: `Orbit`, `Free`, `Map`, `IVA`, `Fixed` ([current/decomp/KSA/CameraMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CameraMode.cs)). A
value outside that range written into `Mode` makes `GetActiveController` throw
`ArgumentOutOfRangeException` on every frame afterwards.

**The viewports.** `Program` builds them once at boot: the main viewport, a part-thumbnail viewport (a
`ViewportBase` that is not a `GameViewport`), four `ViewportType.Secondary` viewports and two
`CharacterPortrait` viewports that start in `Fixed` mode, then seals the registry
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L948-L962)). `ViewportRegistry.MainViewport` is the main one and
`ViewportRegistry.Views` lists all of them ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L41-L50)). Every viewport has its own
cameras and controllers, which is why a Harmony patch on `Camera`, `FixedController` or
`OrbitController` fires for the secondary and portrait cameras as well as the player's. See
[the topic overview](overview.md) for how the other approaches scope that.

**What each controller does to the camera.** Verified against the decompile:

| Mode | Controller (camera) | Writes each frame | Needs |
|---|---|---|---|
| `Orbit` | `OrbitController` (`BaseCamera`) | `NoRotation = false`, `LocalRotation`, `PositionCce` from the followed object's `OrbitView` ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L636-L655)) | A followed object: `OnFrame` returns at once when `Camera.Following` is null ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487-L494)) |
| `Free` | `FlyController` (`BaseCamera`) | `LocalRotation = _frame2Ecl * _offsetEcl` every frame ([current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L734)); position only while a movement key is held | `CacheOffset()` is the public way to make a rotation write stick ([current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L866)) |
| `Map` | `MapController` (`MapCamera`) | `PositionCce` and `LocalRotation` from its own scope and `OrbitView` ([current/decomp/KSA/MapController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MapController.cs#L296-L304)); `OnSwitchOn` sets `NoRotation = true` ([current/decomp/KSA/MapController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MapController.cs#L572-L574)) | Follows the world sun when nothing is followed ([current/decomp/KSA/MapController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MapController.cs#L135-L145)) |
| `IVA` | `IVAController` (`BaseCamera`) | `PositionCce` at the seat every frame and `LocalRotation` from the mouse ([current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L27-L41), [current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L112)) | A followed `Vehicle` that matches `LastFollowing` and has an `IVASeat`; otherwise it calls `Program.HoveredViewport.NextCameraMode()` |
| `Fixed` | `FixedController` (`BaseCamera`) | `PositionEcl = following.GetPositionEcl() + CameraOffset` and `LocalRotation = LookAtRotation(CameraRotation, up)` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47)) | A followed object (with nothing followed, `OnFrame` does nothing); skips `CharacterPortrait` viewports; reads no input |

The `Fixed` controller is the intended mode for a mod-driven camera: its inputs are two public `double3`
fields, `CameraOffset` (metres, ecliptic axes, added to the followed object's position) and
`CameraRotation` (a forward direction, not Euler angles), and it never reads the keyboard
or mouse, so nothing fights the mod ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L7-L14)).

**Everything the viewport pass does:** `GameViewport.OnFrame` is three steps in order, controller first,
then camera, then audio:

```csharp
// current/decomp/KSA/GameViewport.cs L116-L124
public override void OnFrame(double dt)
{
	GetActiveController().OnFrame(this, dt);
	GetCamera().OnFrame(dt);
	if (this.HasAll(ViewportOptionFlags.HasAudio))
	{
		ViewportAudio(dt);
	}
}
```

`Camera.OnFrame` runs `ClampCamera` first, which can push the camera back above the surface (0.5 m), and
only then builds the view and view-projection matrices from `LocalRotation` (`Ego2View`) and the
frustum planes ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L639)).

## Lifecycle and timing

The frame order that matters for a camera write, from `Program.OnFrame`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2240-L2321); the full sequence is in the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)):

| # | Call | Lifecycle value | Camera relevance |
|---|---|---|---|
| 1 | `PrepareFrame` (input poll, `Apply*Solvers`) | `frame:prepare`, `frame:input` | Followed vehicles now hold this frame's position; mouse and key callbacks reach the controllers |
| 2 | `OnFrameViewports` | `frame:viewports` | For every viewport, `GameViewport.OnFrame`: controller `OnFrame`, then `Camera.OnFrame`. **The only place a camera moves** |
| 3 | `OnDrawUiFrame` then `OnDrawUiViewports` | `frame:draw-ui` | `StarMapBeforeGui` and `StarMapAfterGui` fire here, only while the HUD is shown |
| 4 | `OnFrameController` | `frame:controller` | Only copies the active controller's `GetCursorMode()` into GLFW and ImGui ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552)); cameras do not move here |
| 5 | `OnFrameCelestials`, then `Render` | `frame:celestials`, `frame:render` | Read the matrices step 2 built |

`OnFrameViewports` calls `viewport.OnFrame` for every registered viewport, visible or not, before it tests
`Visible` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2625)). The consequence for a mod is the rule the sibling articles
depend on:

> A camera pose written from a StarMap GUI hook (step 3) is not rendered this frame. Step 2 has
> already built the matrices, so the write is consumed by step 2 of the next frame, and if a controller
> rewrites the pose from its own inputs it is overwritten before it is ever drawn.

KSArmory's analysis states the same rule and its fix: write the controller's *inputs*
(`CameraOffset`, `CameraRotation`) from the hook, or write the pose inside the viewport pass
([docs/KSA-CAMERAS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-CAMERAS.md#L43-L80)). See [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md) for
the in-phase patterns.

## Steps

1. **Decide where your write runs.** There are three workable places, in increasing order of phase
   correctness:
   - A `[StarMapAfterGui]` hook that sets controller inputs (`FixedController.CameraOffset` and
     `CameraRotation`). The pose appears next frame; any target that moves at orbital speed
     jitters by one frame of its motion.
   - A Harmony prefix on a controller's `OnFrame` or on `GameViewport.OnFrame`, scoped to the viewport you own.
   - A `FixedController` subclass installed in the viewport. This is the form KSAGolf uses: it asks the
     mod for the pose from inside `OnFrame`, with the followed object's position as the engine holds it at
     that instant ([src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L265-L292)).

2. **Know which mode changes are immediate.** `SetCameraMode` changes the field at once and runs
   the outgoing controller's `OnSwitchOff` and the incoming one's `OnSwitchOn`, and clears the controlled vehicle's held input.
   The pose moves only when the next viewport pass runs, except where `OnSwitchOn` itself writes the
   camera (Map restores its last pose, IVA sets `LocalRotation`). The player's mode key cycles
   `Orbit`, `Free`, `IVA` and returns `false` from `Map` and `Fixed` (the Map controller handles its own toggle-map key and goes back to `Orbit`, [current/decomp/KSA/MapController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MapController.cs#L428-L434); `Fixed` has no equivalent):

```csharp
// current/decomp/KSA/GameViewport.cs L302-L330
public void SetCameraMode(CameraMode mode)
{
	if (base.Mode != mode)
	{
		GetActiveController().OnSwitchOff(mode);
		CameraMode mode2 = base.Mode;
		base.Mode = mode;
		GetActiveController().OnSwitchOn(mode2);
		Program.ControlledVehicle?.ClearHeldPlayerInput();
	}
}

public bool NextCameraMode()
{
	switch (base.Mode)
	{
	case CameraMode.Orbit:
		SetCameraMode(CameraMode.Free);
		return true;
	case CameraMode.Free:
		SetCameraMode(CameraMode.IVA);
		return true;
	case CameraMode.IVA:
		SetCameraMode(CameraMode.Orbit);
		return true;
	default:
		return false;
	}
}
```

   So **a viewport in `Fixed` mode can only be left by an explicit `SetCameraMode`**. A borrower owes
   the player a way back ([Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)).

3. **Set `CameraRotation` before you enter `Fixed`.** The stock body crosses `CameraRotation` with an
   axis taken from the followed object and normalises twice; `double3.Normalize` throws
   `DivideByZeroException` for an exactly zero vector ([current/decomp/Brutal.Numerics/double3.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/double3.cs#L746-L754)), and `ResetToDefaults` leaves
   `CameraRotation` at zero ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L85)):

```csharp
// current/decomp/KSA/FixedController.cs L22-L47
public override void OnFrame(IViewport inViewport, double inDeltaTime)
{
	IFollowable following = Camera.Following;
	if (following != null && !inViewport.Is(ViewportType.CharacterPortrait))
	{
		if (DockingConnector != null && following is Vehicle vehicle)
		{
			doubleQuat rotation = DockingConnector.Asmb2VehicleAsmb.Concatenate(vehicle.Asmb2Cce);
			double3 double5 = new double3(1.0, 0.0, 0.0).Transform(rotation);
			double3 upEcl = new double3(0.0, 0.0, -1.0).Transform(rotation);
			Camera.LocalRotation = KSA.Camera.LookAtRotation(double5, upEcl);
			Camera.PositionEcl = vehicle.GetPositionEcl() + vehicle.PosAsmbToBody(DockingConnector.PositionVehicleAsmb).Transform(vehicle.Body2Cce) + double5 * 0.1;
			return;
		}
		OrbitView orbitView = following.OrbitView;
		doubleQuat frame2Ecl = GetFrame2Ecl(following, orbitView.ReferenceFrame);
		double3 b = double3.UnitZ.Transform(frame2Ecl);
		double3 cameraRotation = CameraRotation;
		double3 a = double3.Cross(cameraRotation, b).Normalized();
		double3 cameraRotation2 = CameraRotation;
		double3 upEcl2 = double3.Cross(a, cameraRotation2).Normalized();
		doubleQuat localRotation = KSA.Camera.LookAtRotation(cameraRotation2, upEcl2);
		double3 positionEcl = following.GetPositionEcl() + CameraOffset;
		Camera.PositionEcl = positionEcl;
		Camera.LocalRotation = localRotation;
	}
```

   KSAGolf writes `CameraRotation` and `CameraOffset` first, then the mode ([Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md)).

4. **Know what the engine reads back after your write.** `PositionEcl` is derived while something is
   followed (`following.GetPositionEcl() + PositionCce`, with `LocalPosition` rotated by
   `following.GetBodyFixed2Ecl()`), so a write moves the stored offset, not an absolute point
   ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L81-L131)). The rendered orientation is `LocalRotation`; `GetForwardEcl()` and its siblings
   go through `WorldRotation` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L133-L154), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L176-L189)). `Camera.OnKey` handles the
   zoom keys before the active controller, in every mode ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1763-L1775)).

5. **Check the crash inventory before you ship.** The frame loop has no `try`/`catch` ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L40-L71)),
   so an exception thrown from any controller or `Camera.OnFrame` ends the process. Rows below are re-verified against the
   decompile:

   | Trigger | Where | Result |
   |---|---|---|
   | `CameraRotation` is exactly zero, or parallel to the frame's +Z axis | `FixedController.OnFrame` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L43)) | `DivideByZeroException` |
   | `Mode` outside the five enum values | `GameViewport.GetActiveController` | `ArgumentOutOfRangeException` every frame |
   | Followed object returns a null `OrbitView` | `FixedController.OnFrame` (`orbitView.ReferenceFrame`) | `NullReferenceException` |
   | `Surface` or `Orbit` reference frame on a vehicle whose ENU or LVLH frame is null | `FixedController.GetFrame2Ecl` (`.Value` on a nullable, [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L52-L70)) | `InvalidOperationException`; the KSArmory analysis says a landed craft with near-zero velocity triggers the `Orbit` case |
   | FOV of 0 degrees, or 180 or more | `Camera.UpdateProjection` calls `CreatePerspectiveFieldOfViewReverseZ`, which rejects it ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L471-L485), [current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13)) | `ArgumentOutOfRangeException` out of `SetFieldOfView` |

   `GetFrame2Ecl` has no case for `WreckageMarker`, so the roll axis silently becomes ecliptic +Z when
   a followed vehicle is destroyed, which can turn the parallel-axis crash on without the mod doing
   anything ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L52-L107)).

## Complete example

The reference implementation of a safe `Fixed` driver is KSAGolf's `Ksa/LevelHorizonController.cs`
plus the `KsaWorld.LevelTheHorizon` installer: it sets the rotation first, asks for the pose
inside `OnFrame`, and falls back to the stock body when no pose is supplied.

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L149-L153, L169-L191
public override void OnFrame(IViewport inViewport, double inDeltaTime)
{
    AskThePoseSource();

    double3 forward = Vec.Unit(CameraRotation);
    // ...
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
}
```

Full files: [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs) and
[src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1697-L1790).

## Variations and gotchas

- **Stale sections in the analysis.** `docs/KSA-CAMERAS.md` ([docs/KSA-CAMERAS.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-CAMERAS.md)) was written
  against builds up to 2026.10.7.5541. Its controller, frame-order and `Camera` sections still hold (re-checked here),
  but its viewport sections and every `Viewport.cs:N` citation are stale: since build 5400 the `Viewport` class is
  replaced by `IViewport`, `ViewportBase`, `GameViewport` and `ViewportRegistry`, and `Program.Viewports`,
  `Viewport.Index` and `Viewport.IsOffscreen` no longer exist. Use the registry instead
  ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L13-L60)). Line numbers in the old file move on every build.
- **The mod-supplied `IFollowable`.** The roll axis of `Fixed` and `Orbit` comes from the followed
  object's runtime type. Anything that is not a `Vehicle`, `Celestial` or `VehicleEditingSpace` gets
  the ecliptic ([Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md)).
- **Mode key from a secondary camera.** `IVAController` and `MapController` bail by calling
  `Program.HoveredViewport` methods, which act on a different viewport from the one that failed.
- **Direct `Mode` writes.** The `Mode` setter is protected; calling it by reflection bypasses
  `OnSwitchOff`/`OnSwitchOn`, which is how the Fixed entry alert is suppressed ([Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md)) but
  also skips Map's `NoRotation` bookkeeping.

## See also

- [Topic overview and sibling approaches](overview.md): [approach 2](understand-camera-modes-and-controllers-2.md), [approach 3](understand-camera-modes-and-controllers-3.md), [approach 4](understand-camera-modes-and-controllers-4.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md), [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)
- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md), [Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
