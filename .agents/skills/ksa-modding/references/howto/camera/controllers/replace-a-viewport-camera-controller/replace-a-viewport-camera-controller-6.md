---
type: How-To
title: Match the orbit camera's drag feel with 0.003 rad per pixel, a squared-2 threshold, 1.1 per wheel notch and an ease back
description: KSAGolf's ChaseOrbit copies KSA's orbit-camera drag constants so a chase answers the mouse like the camera the player knows; at the pinned ref only the constants are wired into the game
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/borrowed-chase-camera]
techniques: [camera-arbitration]
hooks: [subclass KSA.Controller.OnMouseButton]
lifecycle: [frame:input, frame:viewports]
game_types: [KSA.OrbitController, KSA.Controller, KSA.Program]
tags: [camera, orbit, mouse, constants, ease, headless-tests]
---
A controller you write should feel like the one the player already knows. KSAGolf's `ChaseOrbit` is a
pure class (no KSA types, unit-tested) that copies the numbers from `OrbitController`: the right button, a
squared-pixel drag threshold, radians per pixel and a distance step per wheel notch, then eases back
behind the chased body when the hand is off the mouse. This article is the "Take input through the controller" half of the topic, for the constants.

**Status at the pinned ref.** `ChaseOrbit`'s drag logic, `ChaseView`, `ViewClaim` and `SmoothedStep` have no caller in the game code of KSAGolf;
only their headless tests use them. What *is* wired is `HeadLook`, which reads `ChaseOrbit`'s constants (the shared
`DragStartsPastSquared`). KSArmory runs fuller versions of the same files in its chase and sight cameras
([Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)). The article therefore documents the constants and the design; the
in-game use of the controller side is in [approach 5](replace-a-viewport-camera-controller-5.md).

## Background: how the game does it

```csharp
// current/decomp/KSA/OrbitController.cs L354-L358, L374-L398
public override bool OnCursorPos(GlfwWindow window, double2 pos)
{
	float2 float5 = float2.Pack(in pos);
	float2 float6 = float5 - CursorPositionScreen;
	CursorPositionScreen = float5;
	// ...
	if (IsPotentiallyDragging && (double)(CursorPositionScreen - CursorPositionScreenStartDrag).LengthSquared() > 2.0)
	{
		IsDragging = true;
		IsPotentiallyDragging = false;
	}
	if (IsDragging)
	{
		OrbitView view2 = View;
		if (view2 != null)
		{
			view2.Azimuth -= (double)float6.X * 0.003;
			view2.Elevation -= (double)float6.Y * 0.003;
			Elevation = Math.Clamp(Elevation, -Math.PI / 2.0, Math.PI / 2.0);
			view2.Elevation = Math.Clamp(view2.Elevation, -Math.PI / 2.0, Math.PI / 2.0);
			if (Math.Abs(Azimuth) > Math.PI * 2.0 && Math.Abs(view2.Azimuth) > Math.PI * 2.0)
			{
				_prevViewAzimuth -= view2.Azimuth - view2.Azimuth % (Math.PI * 2.0);
				Azimuth %= Math.PI * 2.0;
				view2.Azimuth %= Math.PI * 2.0;
			}
			return true;
		}
	}
	return false;
}
```

The wheel step and the clamp:

```csharp
// current/decomp/KSA/OrbitController.cs L400-L431
public override bool OnScroll(GlfwWindow window, double2 offset)
{
	if (EditorOnScroll(window, offset))
	{
		return true;
	}
	OrbitView view = View;
	if (view != null)
	{
		if (offset.Y > 0.0)
		{
			if (SprintFlag)
			{
				view.DistancePower /= 2.2;
			}
			else
			{
				view.DistancePower /= 1.1;
			}
		}
		else if (SprintFlag)
		{
			view.DistancePower *= 2.2;
		}
		else
		{
			view.DistancePower *= 1.1;
		}
		view.DistancePower = Math.Max(val2: (!(Camera.Following is StellarBody star)) ? 0.5 : SunRenderer.OrbitCamDistPowOf(star), val1: view.DistancePower);
	}
	return true;
}
```

- Right press with no modifier or Shift arms the drag ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L157-L188)).
- Drag starts when `(CursorPositionScreen - CursorPositionScreenStartDrag).LengthSquared() > 2.0`.
- Each pixel changes azimuth and elevation by `0.003` rad.
- One wheel notch multiplies or divides `DistancePower` by `1.1` (2.2 with the sprint flag), clamped below at 0.5.

## Lifecycle and timing

Drag events arrive in `frame:input`; `ChaseOrbit.Advance(seconds, buttonHeld)` is meant to run once per frame from the
chase's pose request in `frame:viewports` (inside the controller's `OnFrame`). It eases on **viewing time**
(player time that stops while paused), not simulated time, because it answers a hand on the mouse: at a
tenth of normal speed a simulated ease would leave the view off the body ten times as long
([src/KSAGolf/Sim/ChaseOrbit.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ChaseOrbit.cs#L3-L15)).

## Steps

1. **Declare the constants once.** They are the engine's, restated with the source named:

```csharp
// src/KSAGolf/Sim/ChaseOrbit.cs L18-L41
/// <summary>What KSA's orbit camera turns per pixel dragged.</summary>
public const double RadiansPerPixel = 0.003;

/// <summary>A press becomes a drag past this squared distance, in pixels — KSA's own threshold.</summary>
public const double DragStartsPastSquared = 2.0;

/// <summary>The distance factor per wheel notch, as KSA's orbit camera steps it.</summary>
public const double ZoomPerNotch = 1.1;

/// <summary>Short of straight over or under, where a turn about the view's own right runs out of room.</summary>
public static readonly double MaxPitchRad = double.DegreesToRadians(80.0);

public const double MinZoom = 0.25;
public const double MaxZoom = 6.0;

/// <summary>How long a view is left where the player put it before it starts back.</summary>
public const double HoldSeconds = 0.4;

/// <summary>How fast it goes back, per second: about a second to settle behind the body.</summary>
public const double ReturnRate = 3.0;

// Below this an angle or a log-zoom is set to exactly zero, so a view that has come back is the
// chase's own pose to the bit rather than a millionth of a radian off it for ever.
private const double Settled = 1e-4;
```

2. **Count the press as a drag only past the threshold, and accumulate deltas only while dragging:**

```csharp
// src/KSAGolf/Sim/ChaseOrbit.cs L70-L109
public void Press()
{
    _pressed = true;
    Dragging = false;
    _startX = _x;
    _startY = _y;
}

public void Release()
{
    _pressed = false;
    Dragging = false;
}

/// <summary>Where the cursor is now, in screen pixels.</summary>
public void Move(double x, double y)
{
    if (!double.IsFinite(x) || !double.IsFinite(y)) return;

    double dx = _haveCursor ? x - _x : 0.0;
    double dy = _haveCursor ? y - _y : 0.0;

    if (!_haveCursor && _pressed) (_startX, _startY) = (x, y);

    _x = x;
    _y = y;
    _haveCursor = true;

    double fromStartX = x - _startX;
    double fromStartY = y - _startY;
    if (_pressed && !Dragging && (fromStartX * fromStartX) + (fromStartY * fromStartY) > DragStartsPastSquared)
    {
        Dragging = true;
    }

    if (!Dragging) return;

    _dx += dx;
    _dy += dy;
}
```

3. **Apply per frame, then ease back.** `Advance` consumes the accumulated deltas, clamps pitch and zoom, and
   ends a drag whose release the engine swallowed (`buttonHeld` false while `_pressed`):

```csharp
// src/KSAGolf/Sim/ChaseOrbit.cs L118-L142
/// <param name="buttonHeld">
/// Whether the button is still down. The engine drops a release made with the cursor over a
/// panel, so a drag has to be able to end from here or it never ends.
/// </param>
public void Advance(double seconds, bool buttonHeld)
{
    if (_pressed && !buttonHeld) Release();

    bool moved = _dx != 0.0 || _dy != 0.0 || _notches != 0.0;

    Yaw = Wrap(Yaw - (_dx * RadiansPerPixel));
    Pitch = Math.Clamp(Pitch + (_dy * RadiansPerPixel), -MaxPitchRad, MaxPitchRad);
    _logZoom = Math.Clamp(_logZoom - (_notches * Math.Log(ZoomPerNotch)), Math.Log(MinZoom), Math.Log(MaxZoom));

    _dx = 0.0;
    _dy = 0.0;
    _notches = 0.0;

    if (moved || Dragging)
    {
        _sinceInput = 0.0;
        return;
    }

    double dt = double.IsFinite(seconds) ? Math.Max(0.0, seconds) : 0.0;
```

4. **Hand it to the controller** through `IMouseDrag` (`Press`, `Release`, `Move`, `Scroll`, `Dragging`), the
   interface the level-horizon controller drives ([approach 4](replace-a-viewport-camera-controller-4.md)).

## Complete example

- [src/KSAGolf/Sim/ChaseOrbit.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ChaseOrbit.cs) and [src/KSAGolf/Sim/IMouseDrag.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IMouseDrag.cs)
- [tests/KSAGolf.Tests/ChaseOrbitTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ChaseOrbitTests.cs): the headless tests
- [src/KSAGolf/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/HeadLook.cs): the one in-game user of the constants

## Variations and gotchas

- **Constants drift.** They are copied, not read from the game (`OrbitController` has `PAN_SENSITIVITY = 0.003` as a public constant and the drag threshold and wheel step as inline literals), so a game update that retunes the orbit camera leaves the chase feeling different.
- **Zoom is a factor on the stand-off**, not `DistancePower`: `MinZoom` 0.25 to `MaxZoom` 6.
- **Pitch clamp 80 degrees** (`MaxPitchRad`) because a turn about the view's right runs out of room straight over or under.
- **Hold and return.** A view is left where the player put it for 0.4 s, then returns at 3 per second.

## See also

- [Topic overview](overview.md); [approach 4](replace-a-viewport-camera-controller-4.md), [5](replace-a-viewport-camera-controller-5.md)
- [Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md), [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)
- [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md), [Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](../../special-views/first-person-camera-for-a-kitten/overview.md)
