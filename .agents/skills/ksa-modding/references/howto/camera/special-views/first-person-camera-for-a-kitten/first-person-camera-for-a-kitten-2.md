---
type: How-To
title: Pose the eye from the head bone in the golf pose with a pitch-tilted up, a captured-cursor look and a drawn turn for a standing kitten
description: KSAGolf builds the first-person eye 12 cm ahead of and 5 cm above the head bone, tilts the up vector with pitch, looks with the captured cursor at 0.08 degrees per pixel or a right-drag at the orbit rate, and draws the turn to the look on the skeleton until the kitten walks
category: camera
group: special-views
topic: first-person-camera-for-a-kitten
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by:
  - mods/ksagolf/features/first-person-kitten-view
  - mods/ksagolf/features/level-horizon-camera-controller
techniques: [anim-processor, controller-subclass, harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.KittenLocomotion.StepGrounded
lifecycle: [frame:draw-ui, frame:viewports, frame:render, frame:input, physics:vehicle-solver]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.IAnimProcessor
  - KSA.GameViewport
  - KSA.FixedController
  - KSA.Controller
  - Brutal.GlfwApi.GlfwCursorMode
  - KSA.KittenLocomotion
  - KSA.CharacterControlInputs
  - KSA.LocomotionCommand
  - KSA.LocomotionState
  - KSA.PhysicsBubble
tags: [camera, first-person, kitten, golf, mouse-look, captured-cursor, up-vector]
---
KSAGolf has the same first-person view as [approach 1](overview.md) ([article](first-person-camera-for-a-kitten-1.md)) with differences that suit a golfer: the eye sits a little further out, the up vector is tilted with the pitch so that looking
straight down and turning spins the picture instead of rolling it, the mouse look can use the **captured cursor** (a shooter's feel) as well as the right-drag, and a kitten standing still has the turn to the look drawn on the skeleton. The head bone, the borrowed `Fixed` view and the
`StepGrounded` patch are the same as in approach 1; this article covers what is different. Prerequisites: those of approach 1.

## Background: how the game does it

The same engine facts as approach 1: the processor list on `AnimatedRenderable`, the main camera copied into the kitten's inputs by `PrepareWorker`, `StepGrounded` building the move from them ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L150-L180), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L330), [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L340-L369)).
In addition, a kitten that is standing still is asleep on the terrain and the locomotion servo does not turn it, so its facing only follows the look once it walks.

## Lifecycle and timing

Same table as approach 1. The captured-cursor look is driven by `LevelHorizonController.OnCursorPos` calling `IMouseSwing.Cursor` in `frame:input` (the cursor mode `Disabled` is applied by `Program.OnFrameController`, `frame:controller`); the eye is asked in `frame:viewports`
([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **Build the eye from the head bone.** Ahead of the head along the *look* (not the body, which lags a hard turn) so the view pivots about the head, and above it; measured against the followed kitten's position as the engine hands it in:

```csharp
// src/KSAGolf/Ksa/Golf.cs L38-L42
private const double ViewFovDeg = 50.0;

// Through the kitten's eyes: above and ahead of the middle of its head, which is hidden, so the
// collar of its suit stays below the view.
private static readonly double3 EyeFromHead = new(0, 5.0, 12.0);
```

```csharp
// src/KSAGolf/Ksa/Golf.cs L679-L712
private bool TryEyes(double3 followedEcl, out double3 offset, out double3 forward, out double3 up)
{
    offset = forward = up = default;
    if (_kitten is not { } kitten || _pose?.Head is not { } head || !TryLocalFrame(kitten, out up, out double3 north, out double3 east)) return false;

    // Ahead of the head along the look rather than along the body, which lags a hard turn: the view
    // then pivots about the head as a head turns, instead of swinging round it. Measured against the
    // kitten's own position, with the one the engine hands in: both this frame's.
    double3 heading = (north * Math.Cos(_look.Yaw)) + (east * Math.Sin(_look.Yaw));
    double3 headEcl = KittenFrame.ToEcl(kitten, head) - KsaWorld.PositionEcl(kitten) + followedEcl;
    double3 eye = headEcl + (heading * (EyeFromHead.Z / 100.0)) + (up * (EyeFromHead.Y / 100.0));
    offset = eye - followedEcl;

    // Where the player looks. Stepping up to the ball nods the view down to it as the kitten steps,
    // and back up as it steps away -- a nod and nothing else, so the heading never moves; the mouse
    // swings the club meanwhile.
    double pitch = _look.Pitch;
    if (_blend > 0.0 && _ball is not null && _ballBody is not null)
    {
        double3 toBall = BallEcl() - eye;
        double down = -Vec.Dot(toBall, up);
        double ahead = Math.Max(Vec.Dot(toBall, heading), 0.05);
        double atBall = Math.Max(-Math.Atan2(down, ahead), -HeadLook.MaxPitchRad);
        pitch += (atBall - pitch) * _blend;
    }

    forward = Vec.Unit((heading * Math.Cos(pitch)) + (up * Math.Sin(pitch)));

    // The view's own up, tilted with it: square to the view at any pitch, so looking straight down
    // and turning spins the picture about its middle instead of rolling it.
    up = Vec.Unit((up * Math.Cos(pitch)) - (heading * Math.Sin(pitch)));

    return Vec.IsFinite(offset) && Vec.IsFinite(forward);
}
```

2. **Tilt the up with the pitch** (the last lines above) so the picture spins about its middle at any pitch. The orientation is given exactly (`UpIsExact` is true in first person), so the controller takes the up as it is instead of easing toward it ([approach 5](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md)).

## Mouse look

3. **Choose the cursor mode for the look.** In first person the view's `Swing` is the mod itself unless Alt is held or a KSA window is open (which give the cursor back so the panel can be reached); the controller then returns `Disabled` from `GetCursorMode` and feeds cursor positions to `Cursor`:

```csharp
// src/KSAGolf/Ksa/Golf.cs L97-L104
public IMouseDrag? Orbit => _borrowed && _phase == Phase.Carrying && _config.FirstPerson && !CursorWanted ? _look : null;

// The mouse is taken to swing at address, and to look in first person -- never while the game is not
// the window in front, and in first person not while Alt is held or a KSA window is open, which give
// the cursor back so the panel can still be reached.
public IMouseSwing? Swing => _borrowed && Focused && (Addressing || (_config.FirstPerson && !CursorWanted)) ? this : null;

private static bool CursorWanted => ImGui.GetIO().KeyAlt || Program.IsWindowOpen;
```

4. **Turn by 0.08 degrees per captured pixel**, discarding the first jump after the capture:

```csharp
// src/KSAGolf/Ksa/Golf.cs L428-L439
public void Cursor(double x, double y)
{
    // The first position after the cursor is captured jumps to wherever the capture put it.
    if (_lastX is { } lastX && _lastY is { } lastY && Math.Abs(x - lastX) < 400.0 && Math.Abs(y - lastY) < 400.0)
    {
        if (Addressing) _dx += x - lastX;
        else _look.Turn(x - lastX, y - lastY);
    }

    _lastX = x;
    _lastY = y;
}
```

   with the constants in [src/KSAGolf/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/HeadLook.cs#L13-L43) (pitch clamped to 85 degrees; the right-drag path shares the 0.003 rad per pixel rate and squared threshold of 2 with `ChaseOrbit`). `HeadLook` is headless-tested ([tests/KSAGolf.Tests/HeadLookTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/HeadLookTests.cs)).

## Make the walking kitten follow the look

5. **Same prefix and postfix on `StepGrounded`** as approach 1, with the heading handed over as a `volatile float[]` ([src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L143-L176)).

6. **Draw the turn for a standing kitten.** Until it walks, the skeleton is turned toward the look so the body shown (the paw holding the putter) agrees with the view; walking, the body itself comes round and the turn falls to nothing. Only worker-owned copies are written:

```csharp
// src/KSAGolf/Ksa/Golf.cs L197-L210

// How far the kitten is drawn turned to face the first-person look. A kitten standing still is asleep
// on the terrain and KSA's servo does not turn it, so until it walks the turn is drawn; walking, the
// body itself comes round to the look and this falls to nothing.
private double _turnToLook;

private void TurnToLook()
{
    _turnToLook = 0.0;
    if (!_config.FirstPerson || !_borrowed || _phase != Phase.Carrying || _kitten is not { } kitten || LookHeading() is not { } heading) return;

    double3 inModel = KittenFrame.DirectionFromEcl(kitten, heading);
    if (Math.Abs(inModel.X) + Math.Abs(inModel.Z) > 1e-6) _turnToLook = Math.Atan2(inModel.X, inModel.Z);
}
```

## Complete example

- [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs) (`TryEyes`, `Cursor`, `TurnToLook`), [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs), [src/KSAGolf/Sim/HeadLook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/HeadLook.cs)
- [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L39-L191): the controller

## Variations and gotchas

- **Other kittens may turn too.** The postfix guard is that `inputs.CameraForwardCce` is not near zero, and its comment says only the flown kitten's inputs carry a camera. But `KittenEva.PrepareWorker` writes the main camera's forward into the inputs of every `KittenEva`, so the guard does not tell kittens apart; whether another grounded kitten in the bubble reaches `StepGrounded` with those inputs was not checked, so test with two kittens out before relying on it.
- **Whole-body hiding and the shadow** are in [Hide a kitten's body from the main view only, keeping its shadow](../../../kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md); golf strips the skeleton per view and puts the whole kitten back in the shadow pool.
- **Optional walk patch.** If `StepGrounded` is missing, the install logs it and carries on: the kitten turns away from the view when strafing.
- **Version notes.** All targets keep their signatures in 2026.10.10.5554 (`KittenLocomotion.StepGrounded(in ..., ref LocomotionState)`).

## See also

- [Topic overview](overview.md); [approach 1](first-person-camera-for-a-kitten-1.md)
- [First-person kitten view that keeps the shadow](../../../../mods/ksagolf/features/first-person-kitten-view.md), [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md)
- [Make a walking kitten move and face along the look direction, and drive it with the mouse](../../../kittens/eva/steer-a-walking-kitten/overview.md), [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md), [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../../../kittens/animation/pose-kitten-bones-with-an-anim-processor/overview.md)
