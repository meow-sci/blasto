---
type: How-To
title: Chase a round with a RoundFollowable on a level-horizon controller, the pose solved in the viewport pass
description: KSArmory follows the round itself through RoundFollowable, installs a FixedController subclass for a stable up vector and orbit drag, and solves the stand-off pose from settled state inside the engine's viewport pass
category: camera
group: main-view
topic: chase-a-fast-body-with-the-main-camera
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/ksarmory/features/chase-camera-on-a-round, mods/ksarmory/features/mod-simulated-rounds]
techniques: [custom-followable, controller-subclass]
hooks: [implements KSA.IFollowable, subclass KSA.FixedController.OnFrame, starmap StarMapAfterGui]
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types:
  - KSA.IFollowable
  - KSA.Camera
  - KSA.FixedController
  - KSA.OrbitView
  - KSA.CameraReferenceFrame
  - KSA.WreckageMarker
tags: [camera, chase, projectile, followable, fixed-camera, kinematics]
---
A shell, missile or ball that a mod simulates itself is not a `Vehicle`, so the stock follow modes do not apply and the player's craft is not near it. KSArmory's chase camera takes the main view when a round leaves the tube, rides it in and hands the view back.
The three pieces are: a custom `IFollowable` for the round, a `FixedController` subclass that supplies a level horizon and takes the orbit drag, and pure geometry that solves a stable pose behind the flight path.
Prerequisites: StarMap, reflection, and the articles linked below for each piece.

## Background: how the game does it

Following works because `Camera.PositionEcl` is derived from `following.GetPositionEcl()` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L131)); `Fixed` mode then places the camera at `following + CameraOffset` and aims along `CameraRotation` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47)).
`Camera.SetFollow` jumps the camera to 2.5 mean radii of its target and, with `changeControl: true`, swaps the controlled vehicle ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)). The player's mode key cannot leave `Fixed` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L314-L330)),
so the chase hands the view back itself. The engine's controller derives roll from the followed object's type, which is wrong for a mod object ([Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md)).

## Lifecycle and timing

| Phase | Where | Work |
|---|---|---|
| `frame:viewports` | `GameViewport.OnFrame` -> `LevelHorizonController.OnFrame` -> `ChaseCamera.TryPose` | Position and rotation written; matrices built |
| `frame:draw-ui` | `[StarMapAfterGui]` -> `StepOnce` -> `StepSimulation` (rounds integrated) then `DriveCameras` -> `ChaseCamera.Apply` | Decisions, aim sample, transition step |
| `frame:render` | `Render` builds per-viewport shader data | Reads `Camera.PositionEcl`, i.e. `RoundFollowable.GetPositionEcl` after the step |

The followed position is live (the getter re-evaluates), and the rotation is re-solved next frame from state `Apply` settled ([pose in phase](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **Make the round followable** ([follow-a-point](../follow-a-point-that-is-not-a-vehicle/overview.md), [approach 2](../follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-2.md)): a non-zero radius, one `OrbitView`, identity body-fixed rotation, and `GetPositionEcl` returning where the round is *drawn*:

```csharp
// src/KSArmory/Ksa/RoundFollowable.cs L16-L19
// Not zero: the engine divides by it when a camera changes focus.
private const double Radius = 1.0;

private readonly OrbitView _orbitView = new(CameraReferenceFrame.Stars);
```

2. **Take the view without taking control.** Record the main view, read the player's pose, follow the round with `changeControl: false` and write the old position back ([Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md)):

```csharp
// src/KSArmory/Ksa/ChaseCamera.cs L465-L500
_saved = KsaWorld.RememberMainView();
if (!_saved.Valid)
{
    // Said, not swallowed. A silent return here is indistinguishable from the chase
    // being switched off, and looks like rounds simply stopping being followed.
    Log.Warn("chase: cannot read the main view, not taking it");
    return;
}

// Read where the player had the view BEFORE anything is attached to the round.
// Camera.SetFollow sets PositionEcl to the followed object plus 2.5 mean radii, and
// a round's mean radius is one metre -- so the instant the follow is swapped the
// camera is 2.5 m from the missile. Reading afterwards gives that, not the player's
// pose, and the transition then eases from its own destination: no travel at all,
// just the aim swinging from a point already at the round.
bool hasPose = KsaWorld.TryMainCameraPose(out double3 wasEcl, out _);

_followed.Track(round, system);

if (!KsaWorld.TryFollowOnMainViewport(_followed))
{
    Log.Warn("chase: the view refused to follow the round");
    _saved = default;
    return;
}

if (hasPose)
{
    // Undo the jump SetFollow just made. This frame's view matrix was built in the
    // viewport pass, which is over, and the controller does not pick up the offset
    // until the next frame -- so without this one frame renders from beside the
    // missile before the transition has begun.
    KsaWorld.TryPlaceMainCamera(wasEcl);

    _fromOffset = wasEcl - system.PlatformEcl;
    _blend = 0.0;
```

3. **Solve a pose behind the flight path, not the round's own velocity.** The rig stands behind the round on the line to what it is flying at, held within 80 degrees of the flight path, with a lift carried from the last frame so passing the vertical does not swap sides:

```csharp
// src/KSArmory/Sim/ChaseView.cs L69-L90, L121-L128

        // Held to the target all the way in. A round arriving is only pointing at what it
        // arrives at when the target is not moving: proportional navigation flies a collision
        // course, which holds a lead angle to impact by design, so handing back to the flight
        // path over the last stretch would swing the view off the target by that whole angle at
        // the one moment anybody is watching. The line reversing as the round goes past is
        // HeldNearFlightPath's job, and it bounds the swing to 80° whether the round is
        // arriving or long past.
        //
        // The look-at stays at least a look-ahead out so the framing does not pitch up as the
        // range collapses, and MinAimRange is a divide-by-zero guard rather than a distance.
        if (range > MinAimRange)
        {
            axis = HeldNearFlightPath(Vec.Unit(toAim), along);
            lookRange = Math.Max(range, ahead);
        }
    }

    // Carried when there is one to carry. Derived from the hint, the lift is whatever part of
    // the hint lies across the axis, which near the vertical is a sliver pointing the way the
    // axis leans -- and past a threshold a fixed perpendicular instead. Either reverses the
    // lift as the axis passes the vertical, so the eye swaps sides and the round appears to turn
    // ...

    if (Vec.Len2(forwardEcl) < 1e-12) return false;

    forwardEcl = LeanOffAxis(Vec.Unit(forwardEcl), engineAxisEcl);
    upEcl = lift;

    return Vec.IsFinite(eyeEcl) && Vec.IsFinite(forwardEcl);
}
```

4. **Close in as the round converges.** The stand-off is a root curve of the range so the camera holds station then rushes in:

```csharp
// src/KSArmory/Sim/ChaseView.cs L275-L286
public static double StandOff(double range, double far, double near,
                              double farDistance, double nearDistance)
{
    if (!double.IsFinite(range) || !(far > near)) return farDistance;

    double t = Math.Clamp((range - near) / (far - near), 0.0, 1.0);

    // A root curve: the slope grows as the range runs out, so it holds station then rushes in.
    t = Math.Pow(t, Sharpness);

    return nearDistance + ((farDistance - nearDistance) * t);
}
```

5. **Write through the borrowed view**: `TryLookFromMainViewport` sets `CameraRotation` before `Fixed`, the offset as a separation from the round, the up vector, the pose source and the field of view
   ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5522-L5575)); the controller asks `ChaseCamera.TryPose` again inside the viewport pass ([src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L858-L898)).

6. **Let the player nudge the view** with the orbit drag: the controller takes the right button and the wheel while the pose source exposes a `ChaseOrbit` ([approach 4](../../controllers/replace-a-viewport-camera-controller/overview.md)); KSArmory advances it each frame with `ImGui.IsMouseDown(Right)` so a release swallowed over a panel cannot stick the drag
   ([src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L335-L342)).

7. **Hand back** with `TryHandBackMainView`; if the view still follows the round because its craft is gone, `HoldWhereItIs` anchors it to the ground instead of an ecliptic point the planet leaves behind.

## Complete example

- [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs), [src/KSArmory/Ksa/RoundFollowable.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundFollowable.cs), [src/KSArmory/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LevelHorizonController.cs)
- [src/KSArmory/Sim/ChaseView.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaseView.cs), [src/KSArmory/Sim/ChaseOrbit.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaseOrbit.cs), [src/KSArmory/Sim/ChaseInterest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaseInterest.cs)

## Variations and gotchas

- **Why not follow the launching craft?** The engine applies vehicle solvers before the viewport pass while the mod integrates its rounds after it, so an offset against the craft gets a frame-newer platform position: about 500 m of camera displacement per frame near Earth.
- **Clocks are chosen per consumer.** The transition runs on the simulated step, the burst linger and orbit ease on viewing time that stops while paused, the give-up grace on simulated seconds.
- **`ChaseView.LeanOffAxis`** keeps every forward vector 2.6 degrees off a singular axis for the stock controller; with the subclass nothing is singular.
- **Field of view.** The chase flies at the player's recorded field, never the sight's magnified one.
- **The camera does not survive a save load**; the chase is released by the world-reload hook.

## See also

- [Topic overview](overview.md); [approach 2](chase-a-fast-body-with-the-main-camera-2.md)
- [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md), [Mod-simulated rounds integrated in sub-steps inside the frame](../../../../mods/ksarmory/features/mod-simulated-rounds.md)
- [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../follow-a-point-that-is-not-a-vehicle/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md), [Play scripted cinematic camera tracks with blends and easing](../../special-views/play-cinematic-camera-tracks/overview.md), [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md)
