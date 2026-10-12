---
type: How-To
title: Hold the main view from the bridge as one camera claimant, optionally orbiting on player time
description: Make the bridge an IViewPose that takes the main view in claim order beside the mod's other camera owners and hands the original view back
category: testing-and-tooling
group: in-game-testing
topic: take-screenshots-from-code
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/file-command-bridge-and-mcp]
techniques: [view-claim-ladder, reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, frame:viewports]
game_types: [KSA.Camera, KSA.FixedController, KSA.Program, KSA.ScreenshotCapture]
tags: [screenshots, camera, claim-order, orbit, bridge]
---

Outcome: a `camera` command that holds the main view at an azimuth, elevation and distance about the newest effect (here a nuclear burst cloud), optionally orbiting at
`orbit_deg_s` degrees per second of player time, and a `watch` command that follows one in-flight round, while the weapons mod's own camera users (a chase camera on a round, a gunner sight, a
director window) keep working because the bridge is one claimant in a fixed order. Demonstrated by KSArmory ([file command bridge](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)).
Prerequisites: [article 2](take-screenshots-from-code-2.md) for the basic hold; this article adds ordering and an orbit.

## Background: how the game does it

The main view has one writer per frame at a time: whoever sets the `FixedController`'s `CameraOffset` and `CameraRotation` last wins ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L5-L44)). A mod with several camera users therefore needs an order and a rule for who takes the view outright.
There is also a phase problem: all three StarMap GUI hooks run after the viewport pass has built the frame's matrices, so a pose written from them is a frame late. KSArmory's `IViewPose` is asked again *inside* the viewport pass, at the instant the engine uses the answer
([src/KSArmory/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LevelHorizonController.cs#L264-L282)); [Write the camera pose in phase with the frame](../../../camera/controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md) explains the mechanism.

## Lifecycle and timing

| Piece | Hook |
|---|---|
| Orbit advance, command poll | `Bridge.Update` in `StarMapAfterOnFrame` (`frame:after-on-frame`), using `dtPlayer`: an orbit should keep moving through a pause, so it runs on player time, not simulated time |
| `DriveCamera()` | `DriveCameras` inside the mod's per-frame step, after the watch camera and before the chase and the sight ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L433-L438)) |
| `IViewPose.TryPose` | Asked by the controller inside the viewport pass (`frame:viewports`) |

## Steps

1. **Fix a claim order and write it down.** The mod's own comment explains why the chase reads the sight's base field from the frame before and why the two cannot be swapped; the bridge slots in right after the watch camera:

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L433-L439
       private void DriveCameras(double dt)
       {
           if (_roster is null || _ui is null) return;

           _watch.Apply(dt);
           _bridge?.DriveCamera();

   ```

2. **Hold a named pose.** The `camera` command remembers the view once, then stores a pose (not a transform), so the pose can be re-evaluated each frame against the thing it is relative to:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L1022-L1048
       private Reply Camera(BridgeCommand command)
       {
           if (command.Flag("release", false))
           {
               if (_pose is null) return Done();

               _pose = null;
               KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
               _posedFrom = null;
               return Done();
           }

           if (KsaWorld.ControlledVehicle is not { } craft) return Failed("no craft is being flown");

           if (_pose is null)
           {
               _saved = KsaWorld.RememberMainView();
               _posedFrom = craft;
           }

           _pose = new CloudWatch.Pose(command.Number("azimuth_deg", 0.0), command.Number("elevation_deg", 14.0),
                                       command.Number("distance_m", 0.0), command.Number("aim", 0.45),
                                       command.Number("turn_deg", 0.0));
           _orbitDegPerSecond = command.Number("orbit_deg_s", 0.0);

           return Done(new() { ["held"] = true });
       }
   ```

3. **Orbit on player time.** Before processing commands, `Update` advances the stored azimuth so the camera sweeps around a frozen scene:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L105-L108
               if (_pose is { } held && _orbitDegPerSecond != 0.0 && double.IsFinite(dtPlayer))
               {
                   _pose = held with { AzimuthDeg = held.AzimuthDeg + (_orbitDegPerSecond * dtPlayer) };
               }
   ```

4. **Apply the pose in the claim step.** `DriveCamera` evaluates the pose against the newest burst (`CloudWatch.Update`) or, for `watch`, holds on a round; a pose that cannot be evaluated leaves the view alone:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L197-L216
       /// <summary>Holds the view on the newest burst while a pose is set. From the camera pass.</summary>
       public void DriveCamera()
       {
           try
           {
               if (_watch is not null)
               {
                   HoldOnRound();
                   return;
               }

               if (_pose is not { } pose || _posedFrom is not { } craft || !KsaWorld.IsAlive(craft)) return;

               CloudWatch.Update(craft, pose);
           }
           catch
           {
               // The view is the player's to take back; a pose that cannot be held is simply not held.
           }
       }
   ```

5. **Be an `IViewPose` for the in-phase ask.** `watch` positions the eye behind a flying round in its own flight frame, with azimuth and elevation, and answers `TryPose` with the offset from the followed object each time the controller asks:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L538-L548
       bool IViewPose.TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                              out double3 upEcl, out double fovDeg)
       {
           fovDeg = _watch?.FovDeg ?? 30.0;
           forwardEcl = upEcl = offsetFromFollowed = Vec.Zero;
           if (!TryWatchPose(out offsetFromFollowed, out upEcl)) return false;

           forwardEcl = -offsetFromFollowed;
           return true;
       }

   ```

6. **Release.** `camera {"release":true}` and the end of a `watch` call `TryHandBackMainView(_saved, ...)`; if the object the view followed was destroyed, the engine has already moved the view to the wreckage and the helper leaves it alone.

7. **Run a scripted sequence**: `pause`, `burst`, `camera {"distance_m":2400,"elevation_deg":8}`, `capture {"label":"flash","frames":6,"every_s":0.25}` (the CLI form is in the mod's `docs/VISUAL-TESTING.md`, [docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/VISUAL-TESTING.md)). Expect `held: true`, then six PNGs at exact effect ages with manifests.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs) | `Camera`, `Watch`, `DriveCamera`, `IViewPose` implementation |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | `DriveCameras` claim order |
| [src/KSArmory/Ksa/CloudWatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudWatch.cs) | The pose evaluated against the burst (`Update`) |
| [src/KSArmory/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LevelHorizonController.cs) | The controller and `IViewPose` |

## Variations and gotchas

- **Order matters.** Putting the bridge after the chase makes the bridge fight it every frame, because the chase takes the view outright.
- **A pose is not a transform.** Store azimuth, elevation and distance; compute the eye each frame from the target's current state, or the view lags a moving subject.
- **Player time vs sim time.** Orbit on `dtPlayer` for a paused world; step effects on simulated time. Mixing them freezes the orbit when paused.
- **Release on unload and on death.** The `catch {}` in `DriveCamera` means a pose that cannot be held is simply not held, never an exception in the frame.

## See also

- [Overview](overview.md), [article 2](take-screenshots-from-code-2.md), [article 3](take-screenshots-from-code-3.md), [article 5](take-screenshots-from-code-5.md)
- [Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Replace a viewport camera controller](../../../camera/controllers/replace-a-viewport-camera-controller/overview.md)
- [KSArmory file command bridge and MCP](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md), [KSArmory borrow main view camera](../../../../mods/ksarmory/features/borrow-main-view-camera.md)
