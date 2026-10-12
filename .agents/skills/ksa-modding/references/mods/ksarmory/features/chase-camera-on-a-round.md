---
type: Mod Feature
title: Chase camera riding a mod-simulated round
description: The main view follows a round through a custom IFollowable that reports the round's drawn position, on a reflection-installed FixedController subclass that adds an up vector and re-solves the pose inside the engine's viewport pass
mod: ksarmory
category: camera
techniques: [reflection, controller-subclass, custom-ifollowable, pose-source-in-viewport-pass, imgui-drawlist]
hooks:
  - starmap StarMapAfterGui (via StepOnce)
  - harmony-prefix KSA.Program.OnFrameCelestials (via StepOnce, HUD hidden)
  - starmap StarMapAfterOnFrame (via StepOnce, fallback)
  - reflection KSA.GameViewport.<FixedController>k__BackingField
  - subclass-override KSA.FixedController.OnFrame
  - starmap StarMapUnload
lifecycle: [frame:input, frame:viewports, frame:draw-ui, frame:celestials, frame:controller, frame:render]
game_types:
  - KSA.Camera
  - KSA.FixedController
  - KSA.Controller
  - KSA.GameViewport
  - KSA.IGameViewport
  - KSA.IFollowable
  - KSA.OrbitView
  - KSA.CameraReferenceFrame
  - KSA.CameraMode
  - KSA.WreckageMarker
  - KSA.OrbitController
  - KSA.Program
  - KSA.Vehicle
tags:
  - camera
  - chase-camera
  - ifollowable
  - fixed-controller
  - main-view
  - projectile
  - transition
  - epochs
  - imgui
---

With a launcher's "chase" switch on, KSArmory takes the player's main view when a round leaves the
tube, travels the view from wherever the player had it onto a stand-off behind the round, rides the
round in (closing as it converges, with the right mouse button orbiting it), stops short to watch it
arrive or cuts to a side-on observer for a charge that grows a cloud, holds on the burst, and hands
the view back.[^ksarmory-chasecamera] It uses the main view rather than a camera window because
KSA's secondary-viewport path (`Program.RenderViewport`) records no ocean or cloud pass; those run
only for the main view later in `RenderGame`.[^ksa-program]

The reusable trick is **what the camera follows**. The view follows the round itself, through
`RoundFollowable`, a mod object presented to the engine as a `KSA.IFollowable` in the same way the
engine's own `WreckageMarker` is. Its `GetPositionEcl` answers where the round's body is *drawn*, so
the eye and the mesh come out of one expression all frame.[^ksarmory-roundfollowable][^ksa-wreckagemarker]
A subclass of KSA's `FixedController`, installed on the main viewport by reflection, adds the up
vector the stock fixed camera does not offer and re-asks the pose inside the engine's viewport
pass.[^ksarmory-levelhorizon]

## How it works

| Piece | Role |
|---|---|
| `ChaseCamera` (Ksa) | State machine per frame: pick a round, take the view, transition, ride, stop short or observe, hold on the burst, hand back. Implements the mod's `IViewPose`[^ksarmory-chasecamera] |
| `RoundFollowable` (Ksa) | The `IFollowable` the main camera follows: the round's drawn body, later a ground-anchored burst or hold point[^ksarmory-roundfollowable] |
| `LevelHorizonController` (Ksa) | `FixedController` subclass: up vector, pose source asked inside `OnFrame`, right-drag and wheel routed to `ChaseOrbit`[^ksarmory-levelhorizon] |
| `ChaseView` (Sim) | Engine-free geometry: stand-off pose, closing curve, stop-short distance, observer placement, transition blend[^ksarmory-chaseview] |
| `ChaseOrbit` (Sim) | The player turning the view round the round; eased back behind it 0.4 s after the mouse is let go[^ksarmory-chaseorbit] |
| `ChaseInterest` (Sim) | Time to go along the line of sight, time to fall for a store, and "going nowhere for 2 simulated seconds" as the reason to give up[^ksarmory-chaseinterest] |
| `ChaseHud` (Ksa) | Brackets and range in metres over what the round is flying at[^ksarmory-chasehud] |
| `KsaWorld` main-view helpers | `RememberMainView`, `TryFollowOnMainViewport`, `TryLookFromMainViewport`, `TryHandBackMainView`, `LevelTheHorizon`[^ksarmory-ksaworld] |

### A mod object the engine can follow

`IFollowable` is `IObjectId, IPosition, IVelocity, IOrientation, IRadius` plus an `OrbitView`, and
nothing has to be registered: `Camera.SetFollow` takes any implementation.[^ksa-ifollowable][^ksa-camera]
`RoundFollowable` returns `MeanRadius = 1.0` (never zero, the engine divides by it on a focus change),
an `OrbitView(CameraReferenceFrame.Stars)`, and `GetBodyFixed2Ecl() = Identity` so an offset does not
turn with the round.[^ksarmory-roundfollowable][^ksa-orbitview] Its `GetPositionEcl` walks a fallback
ladder inside a `try`, because the engine calls it from its own loop:

1. the drawn body from `IEffectSource.TryRoundEffectEcl`, which places the round at the launcher's
   *live* position plus the round's travel, the same call the subpart body placement uses;[^ksarmory-weaponsystem]
2. the launcher's position plus the round's offset; 3. the round's own position;
4. a burst or hold point anchored to the ground under it (`KsaWorld.TryGroundAnchorEcl`);
5. a craft plus an offset when no body could be found.

Following works because `Camera.PositionEcl` is not stored while something is followed: its getter
returns `_following.GetPositionEcl() + PositionCce` (Camera.cs L110-L119). The controller writes the
offset once in the viewport pass, and every later read of the camera position in the frame,
including the per-viewport shader data built in `Render`, re-evaluates the followed object.[^ksa-camera][^ksa-program]
Once the mod has stepped the round, the render sees the eye at the round's new drawn position.

**Why not follow the launching craft**, whose frame would give local vertical: `PrepareFrame` applies
the vehicle solvers (Program.cs L2167) before `OnFrameViewports` (L2263), while the mod integrates its
rounds after it. The engine would add a frame-newer platform position to an offset measured against
the older one; near Earth every position carries about 29.8 km/s of ecliptic motion, which the mod
puts at about 500 m of camera displacement per frame.[^ksarmory-levelhorizon][^ksarmory-frames-doc][^ksa-program]

### The up vector: a FixedController subclass

Stock `FixedController.OnFrame` builds the camera's up from `double3.UnitZ` carried through
`GetFrame2Ecl(following, orbitView.ReferenceFrame)`, and `GetFrame2Ecl` dispatches on the followed
object's *type*: `Vehicle`, `Celestial` and `VehicleEditingSpace` get real frames, anything else gets
`Identity` (FixedController.cs L36-L46, L95-L98).[^ksa-fixedcontroller] Following a `RoundFollowable`
therefore rolls the horizon by the site's angle from the ecliptic pole, and a view along ecliptic +Z
normalises a zero cross product.[^ksarmory-levelhorizon][^ksarmory-cameras-doc]

`LevelHorizonController` overrides the virtual `Controller.OnFrame` (Controller.cs L62):[^ksa-controller][^ksarmory-levelhorizon]

- asks its `IViewPose` source for offset, forward, up and field of view (`AskThePoseSource`);
- with no up given, behaves exactly as the stock controller (`base.OnFrame`);
- otherwise carries last frame's up and pulls it towards the wanted one by at most pi rad/s times the
  frame (clamped to 0.1 s), scaled by how much of the wanted up lies across the view
  (`SightPicture.TryStableUp`), so passing the vertical never flips the picture;
- then makes the engine's own two writes with that up substituted.

It also takes the right button, cursor and wheel (`OnMouseButton`, `OnCursorPos`, `OnScroll`) only
while the pose source exposes a `ChaseOrbit`, reports `IsMouseDrag` so a drag ending over a craft does
not open its part window (Vehicle.cs L3667-L3675 asks the active controller before opening one), and
returns `GlfwCursorMode.Disabled` from `GetCursorMode` while dragging.[^ksarmory-levelhorizon][^ksa-vehicle]
`ChaseOrbit` copies the orbit camera's 0.003 rad per pixel and its "squared distance above 2 px"
drag threshold (OrbitController.cs L19, L374).[^ksarmory-chaseorbit][^ksa-orbitcontroller]

**Installing it** needs reflection: `IGameViewport.FixedController` is get-only and
`GameViewport.FixedController` has a protected setter (GameViewport.cs L42), so
`KsaWorld.LevelTheHorizon` writes the compiler-generated backing field once on the main viewport,
reads the property back to prove the write took, and keeps KSA's instance so
`KsaWorld.RestoreStockController` can put it back from `[StarMapUnload]`.[^ksa-gameviewport][^ksarmory-ksaworld][^ksarmory-mod]
If the install fails the stock controller stays: no levelled horizon, and the pose is a frame late.

### Taking, riding and handing back (`ChaseCamera.Apply`)

1. **Pick**: the newest flying round not already passed over, at least 80 m from the launcher, and
   going somewhere (closing on its aim, or a store the ground stops).[^ksarmory-chasecamera][^ksarmory-chaseinterest]
2. **Take only from the player's own craft**: nothing happens unless the main view already follows
   the firing system's craft (`KsaWorld.MainViewFollows`), so a site elsewhere cannot hijack it.
3. **Record first**: `RememberMainView` (follow target, camera mode, field of view), then read the
   current eye (`TryMainCameraPose`), *then* `SetFollow` the round with `changeControl: false`.
   `Camera.SetFollow` jumps the camera to `target.GetPositionEcl() + 2.5 * MeanRadius * forward`
   (Camera.cs L602-L618), so the old eye is written straight back (`TryPlaceMainCamera`) and becomes
   the start of a 1.2 s transition.[^ksarmory-chasecamera][^ksa-camera]
4. **Every frame**: sample the aim once (a live craft, a hostile round, or a ground aimpoint) and keep
   it as a separation from the round; derive local up from gravity at the round; count down time to
   go; close the stand-off from 26 m behind and 6 m above to 7 m and 1.6 m on a root curve
   (`ChaseView.StandOff`, sharpness 0.5), scaled by the round's body length against a 3.10 m missile;
   look along the line to the aim, held within 80 degrees of the flight path; advance the transition
   on the *simulated* step with a smoothstep blend.[^ksarmory-chaseview]
5. **Write** through `TryLookFromMainViewport(eyeFromRound, forward, up, fov, this)`: `CameraRotation`
   before the mode, `CameraOffset` as a separation from the round, `UpEcl`, `Pose = this`, the field
   of view, then `SetCameraMode(Fixed)`.[^ksarmory-ksaworld]
6. **Stop short** when the arrival is within `max(60 m, 6 fireball radii)` (1.6 cloud heights for a
   charge that grows a cloud) and within its watch time: the eye is held at that point, fixed to the
   ground under it, and only the look follows the round. Over a body with air a cloud-growing charge instead cuts to an observer
   1.85 cloud extents out at 14 degrees, climbing in 6 degree steps to 44 until the terrain clears.[^ksarmory-chaseview][^ksarmory-chasecamera]
7. **Hold on the burst**, anchored to the ground it went off over, for a linger sized by the charge
   (at least 3 s); an expired round gets no hold. Siblings still flying are passed over.
8. **Hand back** with `TryHandBackMainView`; if the view still follows the round because the craft it
   was taken from is gone, `RoundFollowable.HoldWhereItIs` anchors it to the ground instead of leaving
   it at an ecliptic point the planet leaves behind. A refused hand-back is retried for 180 frames.
   If the player changes the mode or the follow first, the chase stands down instead (see
   [borrow the main view](./borrow-main-view-camera.md)).

The field of view it flies at is the player's own: the sight's recorded base when the sight is holding
the view underneath, else the recorded one, else 50 degrees, never the sight's magnified field.[^ksarmory-chasecamera]

`ChaseHud` then draws closed `Reticle` brackets and the range over `chase.Aim`, on a transparent,
input-less full-screen ImGui window, with a half-size growing from 10 px to 32% of the screen
height on `1 - sqrt(closing)`, each stroke drawn twice (dark under bright).[^ksarmory-chasehud]

## Lifecycle and timing

| Value | Where (build 5554) | What runs |
|---|---|---|
| `frame:input` | GLFW mouse callbacks during `Glfw.PollEvents` in `PrepareFrame` (Program.cs L2179), routed to the input viewport's active controller (L2045, L2110, L2118) | `LevelHorizonController` feeds `ChaseOrbit`[^ksa-program] |
| `frame:viewports` | `OnFrameViewports` (L2263, viewport loop L2622) calls `GameViewport.OnFrame`: active controller `OnFrame`, then `Camera.OnFrame` (GameViewport.cs L116-L124) | `LevelHorizonController.OnFrame` asks `ChaseCamera.TryPose`, writes position and rotation; the view matrices are built[^ksa-gameviewport] |
| `frame:draw-ui` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (L2268) runs `StepOnce`: `StepSimulation` (rounds integrated) then `DriveCameras` | `ChaseCamera.Apply`: decisions, aim sample, transition step, field writes; `ChaseHud.Draw` afterwards in the same hook[^ksarmory-mod] |
| `frame:celestials` | HUD hidden: Harmony prefix on `Program.OnFrameCelestials` (L2320) runs `StepOnce` | Same as above, so hiding the HUD does not freeze a borrowed view |
| `frame:controller` | `OnFrameController` (L2316, body L2520) | Applies `GetCursorMode` (hidden cursor while dragging) |
| `frame:render` | `Render` builds per-viewport shader data (L2461-L2465) | Reads `Camera.PositionEcl`, i.e. `RoundFollowable.GetPositionEcl` after the step |

**Latency.** By the time any StarMap hook runs, this frame's controllers and view matrices are built,
so a pose written from the hook would first be drawn next frame. Two things remove that frame:

- **Translation is live.** The eye is the followed object's position plus an offset, and the followed
  object answers with the round's newest drawn position whenever it is asked, so the render after
  the step places the eye correctly against the mesh.[^ksa-camera][^ksarmory-roundfollowable]
- **Rotation is re-solved in the pass that uses it.** `TryPose` is asked inside the next frame's
  `OnFrameViewports`. It reads only state `Apply` settled (the aim as a separation from the round, the
  stand-off, the carried lift, the blend): at that point the engine has advanced vehicles but the mod
  has not yet stepped the round, and re-reading both absolute positions there would leave a frame
  of the round's ecliptic travel in their difference: about 500 m at 60 fps, which at a couple of
  kilometres swings the aim by degrees every frame (the mod's own figure).[^ksarmory-chasecamera]

The transition is advanced only in `Apply`, because the pose is asked twice a frame (once from
`Apply`, once from the pass) and would otherwise run at a rate that depends on whether the HUD drew.
Clocks are chosen per consumer: the transition runs on the simulated step (it must arrive on a round
that moves at simulated speed), the burst linger and the orbit ease on viewing time that stops while
paused, and the give-up grace on simulated seconds.[^ksarmory-chasecamera][^ksarmory-chaseorbit][^ksarmory-chaseinterest]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L301-L666) | `Apply`: stand-down, burst hold, take (L459-L510), closing, stop short, write |
| [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L858-L973) | `TryPose` (asked in the viewport pass) and `TryPoseFor` |
| [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L208-L278) | `Release`, `EndEngagement`, `LetGo` |
| [src/KSArmory/Ksa/RoundFollowable.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundFollowable.cs#L14-L214) | The `IFollowable` |
| [src/KSArmory/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LevelHorizonController.cs#L38-L297) | Controller subclass and the `IViewPose` interface |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5410-L5575) | Backing-field lookup, install, restore, `TryLookFromMainViewport` |
| [src/KSArmory/Sim/ChaseView.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaseView.cs) | Pose, stand-off, stop-short, observer, blend |
| [src/KSArmory/Sim/ChaseOrbit.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaseOrbit.cs) | Mouse orbit round the round |
| [src/KSArmory/Sim/ChaseInterest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ChaseInterest.cs) | Time to go, time to fall, give-up grace |
| [src/KSArmory/Ksa/ChaseHud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseHud.cs) | Target brackets |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L433-L460) | `DriveCameras`: watch, bridge, then the chase |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L107-L126) | `TryRoundEffectEcl`, the shared drawn-position expression |

## Snippets

```csharp
// src/KSArmory/Ksa/RoundFollowable.cs L151-L187
    public double3 GetPositionEcl()
    {
        // Inside the engine's own frame pass, where an exception is the game rather than a log
        // line. The last good answer is always a better outcome than that.
        try
        {
            if (_round is { } round && _source is { } source
                && source.TryRoundEffectEcl(round, out double3 drawn))
            {
                LastPositionEcl = drawn;
            }
            else if (_round is { } fallback && _platform is { } craft && KsaWorld.IsAlive(craft))
            {
                LastPositionEcl = KsaWorld.PositionEcl(craft) + fallback.OffsetFromPlatform;
            }
            else if (_round is { } loose)
            {
                LastPositionEcl = loose.PositionEcl;
            }
            else if (_burstBody is not null
                     && KsaWorld.TryGroundAnchorEcl(_burstBody, _burstAnchor,
                                                    out double3 burst, out _))
            {
                LastPositionEcl = burst;
            }
            else if (_anchor is { } platform)
            {
                LastPositionEcl = KsaWorld.PositionEcl(platform) + _anchorOffset;
            }
        }
        catch
        {
            // Held where it was.
        }

        return LastPositionEcl;
    }
```

```csharp
// src/KSArmory/Ksa/LevelHorizonController.cs L123-L164
    public override void OnFrame(IViewport inViewport, double inDeltaTime)
    {
        AskThePoseSource();

        double3 forward = Vec.Unit(CameraRotation);
        // ...
        if (Vec.Len2(Vec.Unit(UpEcl)) < 0.5)
        {
            Forget();
            base.OnFrame(inViewport, inDeltaTime);
            return;
        }
        // ...
        double step = LevelRateRad * Math.Clamp(inDeltaTime, 0.0, 0.1);

        if (!SightPicture.TryStableUp(forward, UpEcl, _lastUp, step, out double3 up))
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

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5415-L5456
        _fixedControllerField = viewport.GetType().GetField(
            "<FixedController>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        // ...
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
```

```csharp
// current/decomp/KSA/FixedController.cs L36-L46
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
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `IFollowable` and its parts (`IObjectId`, `IPosition`, `IVelocity`, `IOrientation`, `IRadius`, `OrbitView`) | [current/decomp/KSA/IFollowable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IFollowable.cs) | Implemented by `RoundFollowable`; `WreckageMarker` is the engine's own non-vehicle example[^ksa-wreckagemarker] |
| `Camera.SetFollow`, `PositionEcl` (getter re-reads the followed object), `LookAtRotation`, `OnFrame`, `SetFieldOfView` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618) | Follow the round with `changeControl: false`; undo the 2.5-radius jump |
| `FixedController.OnFrame`, `CameraOffset`, `CameraRotation`, private `GetFrame2Ecl` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L99) | Subclassed; the fields are the borrower's interface |
| `Controller` virtuals (`OnFrame`, `OnMouseButton`, `OnCursorPos`, `OnScroll`, `GetCursorMode`, `IsMouseDrag`) | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs) | Overridden by `LevelHorizonController` |
| `GameViewport.FixedController` (protected setter), `OnFrame`, `SetCameraMode`, `GetCamera` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42) | Backing field written by reflection; mode switched to `Fixed` |
| `OrbitView(CameraReferenceFrame)` | [current/decomp/KSA/OrbitView.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitView.cs) | The followable's orbit settings |
| `OrbitController.PAN_SENSITIVITY`, drag threshold | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L19) | Copied by `ChaseOrbit` |
| `Program.OnFrame`, `PrepareFrame`, `OnFrameViewports`, `OnFrameController`, `Render` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2335) | Frame order the timing argument rests on |
| `Vehicle.OnMouseButton` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3675) | Asks `IsMouseDrag` before opening a part window |

## Pitfalls and version notes

- **`CameraRotation` before `Fixed`.** Stock `OnFrame` crosses `CameraRotation` with an axis and
  normalises; a zero rotation (what `ResetToDefaults` leaves) gives a degenerate basis. The mod sets
  the rotation before the mode on every write.[^ksa-fixedcontroller][^ksa-gameviewport][^ksarmory-ksaworld]
- **Fixed has no way out for the player.** `GameViewport.NextCameraMode` has no `Fixed` case and
  `FixedController.OnKey` returns false, so the hand-back must set the mode explicitly.[^ksa-gameviewport][^ksa-fixedcontroller]
- **Read the pose before `SetFollow`.** Afterwards the camera is already 2.5 mean radii from the
  round and the transition would start at its own destination.[^ksa-camera][^ksarmory-chasecamera]
- **`changeControl: false`** on every follow, or `Program.ControlledVehicle` becomes `target as
  Vehicle`, which is null for a round.[^ksa-camera][^ksarmory-ksaworld]
- **One expression for eye and mesh.** Correcting the camera's epoch alone moves it away from the
  mesh, which stays in the mod's; the round's body placement, plume and tracer hang on the same
  `TryRoundEffectEcl`. `Camera.OnFrame` clamps a camera to 0.5 m above terrain (`ClampCamera`), which
  is why that call anchors to the platform's live position rather than the mod's last sample.[^ksarmory-roundfollowable][^ksarmory-weaponsystem][^ksa-camera]
- **Reflection on a compiler-generated name.** A rename of the auto-property's backing field
  degrades to the stock controller with a warning, not a crash.[^ksarmory-ksaworld]
- **Fallback axis (observed in code).** `ChaseView.LeanOffAxis` documents the stock controller's
  singular axis as ecliptic +Z, but `ChaseCamera` passes local up as that argument. With the subclass
  installed nothing is singular; without it `TryLookFromMainViewport` refuses views within about 2.6
  degrees of ecliptic +Z and the chase releases the view.[^ksarmory-chaseview][^ksarmory-chasecamera][^ksarmory-ksaworld]
- **Save load (observed in code).** `ForgetTheWorld` comments "Forget, not release" but calls
  `_chase.Release()`; a release restores the recorded camera mode through `BeginRestoreMainView`,
  after `Camera.DeserializeSave` has already set the save's own mode. Not measured.[^ksarmory-mod][^ksa-camera]
- **Doc drift.** `docs/KSA-CAMERAS.md` cites builds up to 2026.10.7.5541 and the removed `Viewport`
  class; its controller and frame-order claims match 5554, its viewport tables do not.[^ksarmory-cameras-doc]

## See also

- [Borrow the main view and hand it back](./borrow-main-view-camera.md)
- [Gunner's sight overlay](./gunner-sight-overlay.md)
- [World-anchored screen markers](./world-anchored-screen-markers.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Add the up vector the stock FixedController lacks and solve up and aim together for a mod-simulated body](../../../howto/camera/controllers/drive-the-main-camera-in-fixed-mode/drive-the-main-camera-in-fixed-mode-6.md) - how-to article demonstrating this feature
- [Install a FixedController subclass by writing the auto-property backing field, check it took and restore on unload](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-1.md) - how-to article demonstrating this feature
- [Read the decompiled frame order, per-mode behaviour and crash inventory before writing to a camera](../../../howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-1.md) - how-to article demonstrating this feature
- [Ask a pose source from inside a FixedController subclass's OnFrame so the aim matches the frame being drawn](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-1.md) - how-to article demonstrating this feature
- [Chase a round with a RoundFollowable on a level-horizon controller, the pose solved in the viewport pass](../../../howto/camera/main-view/chase-a-fast-body-with-the-main-camera/chase-a-fast-body-with-the-main-camera-1.md) - how-to article demonstrating this feature
- [Report the drawn position of a mod-simulated round from a RoundFollowable](../../../howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-2.md) - how-to article demonstrating this feature

[^ksarmory-chasecamera]: src/KSArmory/Ksa/ChaseCamera.cs
[^ksarmory-roundfollowable]: src/KSArmory/Ksa/RoundFollowable.cs
[^ksarmory-levelhorizon]: src/KSArmory/Ksa/LevelHorizonController.cs
[^ksarmory-chasehud]: src/KSArmory/Ksa/ChaseHud.cs
[^ksarmory-chaseview]: src/KSArmory/Sim/ChaseView.cs
[^ksarmory-chaseorbit]: src/KSArmory/Sim/ChaseOrbit.cs
[^ksarmory-chaseinterest]: src/KSArmory/Sim/ChaseInterest.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-frames-doc]: docs/FRAMES-AND-EPOCHS.md
[^ksarmory-cameras-doc]: docs/KSA-CAMERAS.md
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-controller]: KSA.Controller (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-ifollowable]: KSA.IFollowable (decompiled, build 2026.10.10.5554)
[^ksa-wreckagemarker]: KSA.WreckageMarker (decompiled, build 2026.10.10.5554)
[^ksa-orbitview]: KSA.OrbitView (decompiled, build 2026.10.10.5554)
[^ksa-orbitcontroller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
