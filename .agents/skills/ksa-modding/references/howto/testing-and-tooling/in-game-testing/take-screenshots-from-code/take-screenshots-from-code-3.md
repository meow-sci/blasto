---
type: How-To
title: A SceneCamera shot relative to the craft or a building, held by the dev bridge
description: The lean variant of a held composed shot with one pose source, an office_view command at building coordinates and a release that hands the original view back
category: testing-and-tooling
group: in-game-testing
topic: take-screenshots-from-code
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/dev-file-bridge-and-mcp]
techniques: [reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapUnload]
lifecycle: [frame:after-on-frame, frame:viewports]
game_types: [KSA.Camera, KSA.FixedController, KSA.Program, KSA.ScreenshotCapture]
tags: [screenshots, camera, composed-shot, bridge, office-view]
---

Outcome: two bridge commands that pose the main view for a screenshot: `camera` (east/north/up metres from the craft you fly, aimed at it or another craft, own FOV) and
`office_view` (a position, heading, pitch and FOV given in a building's own frame, for looking at places you cannot fly to). Both are held every step until released.
It is [article 2](take-screenshots-from-code-2.md) with fewer features: one pose source, no body-relative offsets, and the building-frame variant. Demonstrated by
KSAStructures ([dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md)), whose visual work (underground lighting, a lift car against its rider) was judged from pictures.
Prerequisites: [article 1](take-screenshots-from-code-1.md) and the camera borrowing helpers.

## Background: how the game does it

Same engine facts as [article 2](take-screenshots-from-code-2.md): `FixedController.CameraOffset` and `CameraRotation` relative to the followed object, `CameraMode.Fixed`, and
the capture hiding the UI. What differs is the frame in which the shot is expressed: KSAStructures measures from the flown craft's local horizon (east/north/up from the parent body's spin axis) or, for
`office_view`, from the building's frame, so the same command works wherever the structure was placed on the planet. Camera mechanics:
[Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md).

## Lifecycle and timing

- `camera` and `office_view` run in `Bridge.Update` (`frame:after-on-frame`).
- `Bridge.DriveCamera()` is called inside the mod's step ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L136)), alongside the hook that reaches the step first each frame, so the pose is restated before the render reads the camera.
- `Bridge.Release()` runs from `[StarMapUnload]` ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L193)), so unloading never leaves the player in a pose nothing drives.

## Steps

1. **Pose source.** A small class that returns the eye offset, forward and up for the current frame. It measures everything from the anchor craft so no frame of planet motion leaks in:

   ```csharp
   // src/KSAStructures/Ksa/SceneCamera.cs L12-L47
   internal sealed class SceneCamera : IViewPose
   {
       public required Vehicle Anchor { get; init; }
       public double3 EastNorthUp { get; init; }
       public Vehicle? AtCraft { get; init; }
       public double AtUp { get; init; }
       public double FovDeg { get; init; } = 50.0;

       public IMouseDrag? Orbit => null;

       /// <summary>The eye's offset from the anchor and where it looks, in the ecliptic.</summary>
       public bool TryLook(out double3 offset, out double3 forward, out double3 up)
       {
           offset = forward = up = default;
           if (!KsaWorld.IsAlive(Anchor) || Anchor.Parent is not Celestial body) return false;

           double3 anchor = KsaWorld.PositionEcl(Anchor);
           up = Vec.Unit(anchor - body.GetPositionEcl());
           double3 axis = new double3(0, 0, 1).Transform(body.GetCce2Ccf().Inverse());
           double3 north = Vec.Unit(axis - (up * Vec.Dot(axis, up)));
           double3 east = Vec.Cross(north, up);
           offset = (east * EastNorthUp.X) + (north * EastNorthUp.Y) + (up * EastNorthUp.Z);

           double3 target = up * AtUp;
           if (AtCraft is { } craft && KsaWorld.IsAlive(craft)) target += KsaWorld.PositionEcl(craft) - anchor;

           forward = Vec.Unit(target - offset);

           // Looking straight down, the vertical is along the view: north is the up then.
           if (Math.Abs(Vec.Dot(forward, up)) > 0.8) up = north;
           return Vec.IsFinite(forward) && Vec.Len2(forward) > 0.5 && Vec.IsFinite(offset);
       }

       /// <summary>Points the main view. Once a frame, with the step.</summary>
       public bool Drive() => TryLook(out double3 offset, out double3 forward, out double3 up)
                              && KsaWorld.TryLookFromMainViewport(offset, forward, up, FovDeg, this);
   ```

2. **`camera` command.** Remember the view only if neither `camera` nor `office_view` already holds it, build the `SceneCamera`, and `Drive()` once so the reply says whether the hold took:

   ```csharp
   // src/KSAStructures/Ksa/Bridge.cs L219-L261
       // The main view held "east", "north" and "up" metres from the craft being flown, looking at it
       // or at the craft named "at" ("at_up" metres above it), at "fov_deg". "release" hands it back.
       private Reply Camera(BridgeCommand command)
       {
           if (command.Flag("release", false))
           {
               if (_scene is null) return Done();

               _scene = null;
               if (_officeView is null)
               {
                   KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
                   _posedFrom = null;
               }

               return Done();
           }

           if (KsaWorld.ControlledVehicle is not { } craft) return Failed("no craft is being flown");

           string at = command.String("at");
           Vehicle? target = at.Length > 0
                                 ? KsaWorld.Vehicles.FirstOrDefault(v => KsaWorld.IsAlive(v) && KsaWorld.DisplayName(v) == at)
                                 : null;
           if (at.Length > 0 && target is null) return Failed($"no craft called '{at}'");

           if (_scene is null && _officeView is null)
           {
               _saved = KsaWorld.RememberMainView();
               _posedFrom = craft;
           }

           _scene = new SceneCamera
           {
               Anchor = craft,
               EastNorthUp = new double3(command.Number("east", 0.0), command.Number("north", -8.0), command.Number("up", 2.0)),
               AtCraft = target,
               AtUp = command.Number("at_up", 0.0),
               FovDeg = command.Number("fov_deg", 50.0),
           };

           return Done(new() { ["held"] = _officeView is null && _scene.Drive() });
       }
   ```

3. **`office_view`**: the same remember-once logic, with a tuple (x, y, z, heading, pitch, FOV) in the building's frame instead of a `SceneCamera`:

   ```csharp
   // src/KSAStructures/Ksa/Bridge.cs L263-L291
       // The main view held at a place in the office's own frame, for looking at what cannot be flown to.
       private Reply OfficeView(BridgeCommand command)
       {
           if (command.Flag("release", false))
           {
               if (_officeView is null) return Done();

               _officeView = null;
               if (_scene is null)
               {
                   KsaWorld.TryHandBackMainView(_saved, _posedFrom, out _, out _);
                   _posedFrom = null;
               }

               return Done();
           }

           if (KsaWorld.ControlledVehicle is not { } craft) return Failed("no craft is being flown");

           if (_officeView is null && _scene is null)
           {
               _saved = KsaWorld.RememberMainView();
               _posedFrom = craft;
           }

           _officeView = (command.Number("x", -3.3), command.Number("y", 0.0), command.Number("z", 0.0),
                          command.Number("heading_deg", 0.0), command.Number("pitch_deg", 0.0), command.Number("fov_deg", 70.0));
           return Done(new() { ["held"] = true });
       }
   ```

4. **Always release.** `release: true` hands the saved view back only when the other holder (`_officeView` or `_scene`) is not active.

5. **Capture** with the article 1 code (`capture` command); the manifest gets the same `craft_screen` and FOV.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs) | `Camera`, `OfficeView`, `OfficeLight`, `BeginCapture`, `ReloadShaders` |
| [src/KSAStructures/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/SceneCamera.cs) | Composed pose source |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs) | `TryRequestScreenshot` (lines 340-397) and the camera helpers |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs) | Hooks: `Update`, `DriveCamera`, `Release` |

## Variations and gotchas

- **Two holders, one saved view.** `camera` and `office_view` share `_saved`; the first to hold remembers, the last to release restores. Mirror that rule or one command will hand back the other's pose.
- **Needs a flown craft.** Both commands fail with `no craft is being flown` otherwise.
- **Keep the pose source free of game state it cannot read when the craft dies.** `TryLook` returns false for a disposed anchor and the hold simply stops.
- **Pair with lighting commands.** `office_light` in the same bridge flips darkness, shadows, lamp scale and fill so one script can capture the same view lit differently; see [Visual regression testing for a KSA mod](../visual-regression-for-mods/overview.md).

## See also

- [Overview](overview.md), [article 2](take-screenshots-from-code-2.md), [article 4](take-screenshots-from-code-4.md)
- [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-3.md)
- [KSAStructures dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md)
