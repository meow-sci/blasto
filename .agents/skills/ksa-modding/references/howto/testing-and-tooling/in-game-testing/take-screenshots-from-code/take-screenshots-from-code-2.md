---
type: How-To
title: Compose a shot with a held SceneCamera, a 24-frame countdown and a manifest of the craft's screen position
description: Hold the main view at an offset from the flown craft aimed at a target, wait out the warm-up frames and record where the craft falls in the picture
category: testing-and-tooling
group: in-game-testing
topic: take-screenshots-from-code
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/visual-testing-captures]
techniques: [reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.Program._screenshotCapture]
lifecycle: [frame:after-on-frame, frame:viewports]
game_types: [KSA.Camera, KSA.FixedController, KSA.Program, KSA.ScreenshotCapture]
tags: [screenshots, camera, composed-shot, fov, manifest]
---

Outcome: a bridge command `frame` that places the main camera `east`, `north`, `up` metres from the craft you are flying (or at offsets in the craft's own
frame), aims it at another craft or a body at a chosen field of view, and keeps it there every step until `release`, so the following `capture` shows exactly the composition you asked for.
Prerequisites: [article 1](take-screenshots-from-code-1.md) (request, wait, manifest) and a way to take the main view over; this article is about the held pose.
Demonstrated by KSAGolf ([visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md)).

## Background: how the game does it

The main viewport's camera follows an object and, in `Fixed` mode, a `FixedController` places it at `following.GetPositionEcl() + CameraOffset` looking along `CameraRotation`
([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L5-L44)). So a composed shot is: remember the current mode, follow, field of view; set `CameraRotation` (must be non-zero: the controller normalises it), `CameraOffset` and the FOV;
switch the viewport to `CameraMode.Fixed` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302)); and keep writing every step, because other code rewrites the view each frame.
KSAGolf does this through its own `FixedController` subclass with a pose callback ([src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L302-L327)); the mechanics
(why the offset must be measured against the followed object's position *now*, the take-over order, handing the view back) are in
[Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md) and
[Write the camera pose in phase with the frame](../../../camera/controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md).

## Lifecycle and timing

| What | Where |
|---|---|
| `frame` command handler remembers the view, builds the `SceneCamera`, calls `Drive()` once | `StarMapAfterOnFrame` (`frame:after-on-frame`) |
| `Drive()` every step while held | The mod's per-frame step: the GUI postfix when the HUD is drawn, else a prefix on `OnFrameCelestials`, else the frame postfix; and the pose callback is asked again inside the viewport pass (`frame:viewports`) |
| Capture | `capture` closure as in article 1: request with `warm=24`, countdown, manifest on the capture frame |

The held pose must be restated during the warm-up frames: the capture hides the UI for those frames, so the GUI-pass hook does not run, and the fallback hooks keep stepping and driving the camera.

## Steps

1. **Remember the view before the first write**, not after: setting Fixed clears the follow, so a reading taken afterwards describes the borrowed state (see `RememberMainView`, [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1612-L1624)).

2. **Build the pose source.** `SceneCamera.TryLook` converts east/north/up to the ecliptic frame from the craft's position and the parent body's spin axis, aims at the target (`AtCraft` plus `AtCraftUp`, or `AtBody`, else north), then applies a turn and tilt so the subject sits off centre:

   ```csharp
   // src/KSAGolf/Ksa/SceneCamera.cs L33-L68
       /// <summary>The eye's offset from the anchor and where it looks, in the ecliptic.</summary>
       public bool TryLook(out double3 offset, out double3 forward, out double3 up)
       {
           offset = forward = up = default;
           if (!KsaWorld.IsAlive(Anchor) || Anchor.Parent is not Celestial body) return false;

           double3 anchor = KsaWorld.PositionEcl(Anchor);
           up = Vec.Unit(anchor - KsaWorld.PositionEcl(body));
           double3 axis = new double3(0, 0, 1).Transform(body.GetCce2Ccf().Inverse());
           double3 north = Vec.Unit(axis - (up * Vec.Dot(axis, up)));
           double3 east = Vec.Cross(north, up);
           offset = (east * EastNorthUp.X) + (north * EastNorthUp.Y) + (up * EastNorthUp.Z);
           if (Body is { } own)
           {
               double3 side = Vec.Unit(Vec.Cross(own.Face, own.Head));
               offset = (own.Face * EastNorthUp.Y) + (side * EastNorthUp.X) + (own.Head * EastNorthUp.Z);
               up = own.Head;
               north = own.Head;
           }

           double3 target;
           if (AtCraft is { } craft && KsaWorld.IsAlive(craft)) target = KsaWorld.PositionEcl(craft) - anchor + (up * AtCraftUp);
           else if (AtBody is { } other) target = KsaWorld.PositionEcl(other) - anchor;
           else target = north * 100.0;

           forward = Vec.Unit(target - offset);

           // Looking straight down at the body, the vertical is along the view: north is the up then.
           if (Math.Abs(Vec.Dot(forward, up)) > 0.8) up = north;
           double3 right = Vec.Unit(Vec.Cross(forward, up));
           double turn = double.DegreesToRadians(TurnDeg), tilt = double.DegreesToRadians(TiltDeg);
           forward = Vec.Unit((forward * Math.Cos(turn)) + (right * Math.Sin(turn)));
           double3 top = Vec.Unit(Vec.Cross(right, forward));
           forward = Vec.Unit((forward * Math.Cos(tilt)) + (top * Math.Sin(tilt)));
           return Vec.IsFinite(forward) && Vec.IsFinite(offset);
       }
   ```

3. **Hold it every step and state the FOV every time.** `Drive()` writes through the helper that sets `CameraRotation` and `CameraOffset` first and then the mode; `TryPose` is the in-phase callback:

   ```csharp
   // src/KSAGolf/Ksa/SceneCamera.cs L70-L79
       /// <summary>Points the main view. Once a frame, from the camera pass.</summary>
       public bool Drive() => TryLook(out double3 offset, out double3 forward, out double3 up)
                              && KsaWorld.TryLookFromMainViewport(offset, forward, up, FovDeg, this);

       public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl, out double3 upEcl,
                           out double fovDeg)
       {
           fovDeg = FovDeg;
           return TryLook(out offsetFromFollowed, out forwardEcl, out upEcl);
       }
   ```

4. **The `frame` command** wires arguments to the pose (`east`, `north`, `up`, `at`, `at_up`, `at_body`, `fov`, `own`, `turn`, `tilt`) and `release` hands the view back:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L424-L461
       private Reply Frame(BridgeCommand command)
       {
           if (command.Flag("release", false))
           {
               if (_scene is null) return Done();
               _scene = null;
               KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
               _posedFrom = null;
               return Done();
           }

           if (KsaWorld.ControlledVehicle is not { } craft) return Failed("no craft is being flown");
           string at = command.String("at");
           Vehicle? target = at.Length > 0 ? KsaWorld.Vehicles.FirstOrDefault(v => KsaWorld.IsAlive(v) && KsaWorld.DisplayName(v) == at) : null;
           if (at.Length > 0 && target is null) return Failed($"no craft called '{at}'");

           if (_scene is null)
           {
               _saved = KsaWorld.RememberMainView();
               _posedFrom = craft;
           }

           _scene = new SceneCamera
           {
               Anchor = craft,
               EastNorthUp = new double3(command.Number("east", 0.0), command.Number("north", -5.0), command.Number("up", 1.0)),
               AtCraft = target,
               AtCraftUp = command.Number("at_up", 0.0),
               AtBody = command.String("at_body") is { Length: > 0 } b ? FindBody(b) : null,
               FovDeg = command.Number("fov", 50.0),
               Body = command.Flag("own", false) && craft is KittenEva k && TryKittenFrame(k, out double3 face, out double3 head)
                   ? (face, head)
                   : null,
               TurnDeg = command.Number("turn", 0.0),
               TiltDeg = command.Number("tilt", 0.0),
           };
           return Done(new() { ["held"] = _scene.Drive() });
       }
   ```

5. **Capture and record where the craft is.** After the warm-up countdown ends the mod writes the manifest (`Screen(...)` above, using `camera.EgoToClipDouble`), which includes `fov_deg` and `craft_screen`; a crop tool can then centre on the craft ([tools/vis/vis.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/vis/vis.py), see [Visual regression testing for a KSA mod](../visual-regression-for-mods/overview.md)).

6. **Check it.** `{"cmd":"frame","north":-6,"up":1.5,"fov":40}` replies `held: true`; `capture` then returns a PNG with the craft centred and `craft_screen` near `[0.5, 0.5]`. Always finish with `{"cmd":"frame","release":true}`; unloading the mod also calls `Release()`.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/SceneCamera.cs) | Composed pose source |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | `Frame`, `BeginCapture`, `Manifest` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs) | `RememberMainView`, `TryLookFromMainViewport`, `TryHandBackMainView` |
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs) | The `FixedController` subclass and `IViewPose` |

## Variations and gotchas

- **A zero `CameraRotation` crashes** the fixed controller (the mod's comment: set it before the mode, every time).
- **Look-straight-down degeneracy.** When the view is within about 37 degrees of the up axis the code uses north as up (`Math.Abs(Dot(forward, up)) > 0.8`).
- **Restore the FOV too.** A borrower that hands back everything except the zoom leaves the player at a few degrees with no control that reaches it.
- **Followed object must exist.** `TryLookFromMainViewport` returns false when the camera is not following anything; the command then replies `held: false`.
- **Game updates.** The mod swaps a `FixedController` into the viewport through its auto-property backing field by reflection; if `GameViewport` changes it degrades to the stock controller with a warning and a less level horizon.

## See also

- [Overview](overview.md), [article 1](take-screenshots-from-code-1.md), [article 3: the lean variant](take-screenshots-from-code-3.md), [article 4](take-screenshots-from-code-4.md)
- [Drive the main camera in fixed mode](../../../camera/controllers/drive-the-main-camera-in-fixed-mode/overview.md), [Project world positions to screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [KSAGolf visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md)
