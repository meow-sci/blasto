---
type: How-To
title: Never enter Fixed with a zero or along-axis forward, and re-assert the mode every step
description: KSAGolf writes CameraRotation and CameraOffset before SetCameraMode(Fixed), refuses a zero or along-up forward, and sets Fixed again each step because the player's mode key cannot leave it
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
approach: 1
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksagolf/features/level-horizon-camera-controller]
techniques: [reflection, controller-subclass]
hooks: [starmap StarMapAfterGui, subclass KSA.FixedController.OnFrame]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.ViewportBase, KSA.Camera, KSA.CameraMode]
tags: [camera, fixed-camera, crash, divide-by-zero, camera-mode]
---
`Fixed` is the camera mode a mod drives, and it has three sharp edges: a zero `CameraRotation` throws on
the first frame, a forward parallel to the roll axis throws the same way, and no player input leaves the mode.
KSAGolf's `TryLookFromMainViewport` is a compact, defensive way to enter and hold it. Prerequisites: StarMap, and
a followed object (with nothing followed, `FixedController.OnFrame` does nothing).

## Background: how the game does it

The stock body crosses `CameraRotation` with the +Z axis of the followed object's reference frame and
normalises twice; `double3.Normalize` throws `DivideByZeroException` for an exactly zero vector. `GameViewport.ResetToDefaults` leaves
`CameraRotation` at zero:

```csharp
// current/decomp/KSA/FixedController.cs L36-L43
OrbitView orbitView = following.OrbitView;
doubleQuat frame2Ecl = GetFrame2Ecl(following, orbitView.ReferenceFrame);
double3 b = double3.UnitZ.Transform(frame2Ecl);
double3 cameraRotation = CameraRotation;
double3 a = double3.Cross(cameraRotation, b).Normalized();
double3 cameraRotation2 = CameraRotation;
double3 upEcl2 = double3.Cross(a, cameraRotation2).Normalized();
doubleQuat localRotation = KSA.Camera.LookAtRotation(cameraRotation2, upEcl2);
```

```csharp
// current/decomp/Brutal.Numerics/double3.cs L746-L754
public static double3 Normalize(double3 value)
{
	double num = Dot(value, value);
	if (num == 0.0)
	{
		throw new DivideByZeroException();
	}
	return value / double.Sqrt(num);
}
```

`GetFrame2Ecl` supplies that axis from the followed object's runtime type and `OrbitView.ReferenceFrame`
(local vertical for a `Vehicle` under `Surface`, the orbit normal under `Orbit`, ecliptic +Z for anything unknown
or `Stars`) ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L50-L100)). `SetCameraMode` changes the field at once and `NextCameraMode` only
cycles `Orbit`, `Free`, `IVA`, so the mode key cannot leave `Fixed`:

```csharp
// current/decomp/KSA/GameViewport.cs L314-L330
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

The View menu, which calls `SetCameraMode` directly, is the only player-facing way out; the `SetCamera` console command, opening the vehicle editor (which forces `Orbit`) and loading a save also set the mode ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3690-L3702)).

## Lifecycle and timing

The entry runs in the GUI hook step (`frame:draw-ui`), after the viewport pass; the first Fixed frame is
the next `frame:viewports`. That is why the rotation must already be non-zero when the mode is set: the frame
that follows is drawn through `FixedController.OnFrame` ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Validate before touching anything.** A non-finite offset or a (near) zero forward returns false:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1821-L1825
public static bool TryLookFromMainViewport(double3 offsetFromFollowed, double3 forwardEcl,
                                           double3 upEcl, double fovDeg, IViewPose? pose = null)
{
    if (!Vec.IsFinite(offsetFromFollowed) || !Vec.IsFinite(forwardEcl)) return false;
    if (Vec.Len2(forwardEcl) < 1e-12) return false;
```

2. **Refuse an along-axis forward only when you are on the stock controller.** With the stock controller the roll
   axis is ecliptic +Z for a mod-supplied followable, so a forward exactly along it divides by zero and one close to it
   flips the roll, which is why the mod refuses anything within about 2.6 degrees (`dot > 0.999`);
   the subclass from [the controller topic](../replace-a-viewport-camera-controller/overview.md) carries its own up and has no such direction:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1835-L1844
// Only when the engine is deriving up for itself, which it does whenever the level
// controller could not be installed. Its axis is then ecliptic +Z, and a view along
// that divides by zero. LevelHorizonController has no such direction: it falls back
// for a view along the up it was given rather than refusing, so the chase is never
// dropped mid-flight.
if (controller is not LevelHorizonController
    && Math.Abs(Vec.Dot(Vec.Unit(forwardEcl), new double3(0, 0, 1))) > 0.999)
{
    return false;
}
```

3. **Write `CameraRotation` and `CameraOffset` before the mode, every time**, then set `Fixed` if it is not
   already. Calling this each step *is* the re-assertion: if anything switched the mode away, the next step puts it back:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1846-L1849, L1865-L1867
// Set before the mode, every time. A frame drawn in Fixed with a zero rotation is the
// crash, and setting the mode first leaves exactly that gap.
controller.CameraRotation = Vec.Unit(forwardEcl);
controller.CameraOffset = offsetFromFollowed;
// ...
if (viewport.Mode != CameraMode.Fixed) viewport.SetCameraMode(CameraMode.Fixed);

return true;
```

4. **Record how to leave** before the first write, and give the player a way back: the mode key does nothing,
   so say that `View > Orbit Camera` restores the view ([Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)).

## Complete example

[src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1821-L1874) and the controller
[src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs). The same method exists in KSAStructures and KSArmory.

## Variations and gotchas

- **Order matters, not only values.** Setting the mode first leaves exactly one frame drawn with a zero rotation, which is the crash.
- **Wreckage.** When a followed vehicle is destroyed the engine re-points cameras at a `WreckageMarker`, whose roll axis `FixedController.GetFrame2Ecl` does not special-case, so the stock axis becomes ecliptic +Z.
- **`ResetToDefaults`** (a leased secondary viewport) zeroes `CameraRotation` again; re-apply after every claim ([Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md)).
- **Entry alert.** `FixedController.OnSwitchOn` raises a "Fixed camera" alert ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L140-L143)); see [approach 3](drive-the-main-camera-in-fixed-mode-3.md) to silence your own park.

## See also

- [Topic overview](overview.md); [approach 2](drive-the-main-camera-in-fixed-mode-2.md), [3](drive-the-main-camera-in-fixed-mode-3.md), [4](drive-the-main-camera-in-fixed-mode-4.md), [5](drive-the-main-camera-in-fixed-mode-5.md), [6](drive-the-main-camera-in-fixed-mode-6.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)
- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../understand-camera-modes-and-controllers/overview.md)
