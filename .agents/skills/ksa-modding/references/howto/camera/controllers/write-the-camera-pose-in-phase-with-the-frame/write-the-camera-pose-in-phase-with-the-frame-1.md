---
type: How-To
title: Ask a pose source from inside a FixedController subclass's OnFrame so the aim matches the frame being drawn
description: KSAGolf, KSAStructures and KSArmory install a FixedController subclass that calls the borrower's IViewPose.TryPose inside the viewport pass, against the followed object's position as the engine holds it at that instant
category: camera
group: controllers
topic: write-the-camera-pose-in-phase-with-the-frame
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
  - mods/ksarmory/features/chase-camera-on-a-round
techniques: [controller-subclass, reflection]
hooks: [subclass KSA.FixedController.OnFrame, starmap StarMapAfterGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera, KSA.IViewport, KSA.Controller, KSA.Transform3D]
tags: [camera, phase, fixed-camera, controller, jitter, frame-order]
---
All three StarMap per-frame hooks run after `OnFrameViewports`, so a camera aimed from a hook is used on the
next frame's matrices while the followed body is drawn where it is now. The error is one frame of the
target's motion across the view: invisible while paused, and growing with simulation speed and with how
close the target is (KSArmory's sight comments put it at a couple of pixels at 1x and a third of the
picture at 16x warp, as the mod's own estimate). The fix used by three mods is to keep the hook for *intent* and answer the pose from
inside the controller's `OnFrame`, the engine's own write point. Prerequisites: StarMap, and a controller
installed as in [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md).

## Background: how the game does it

`GameViewport.OnFrame` is controller, then camera, then audio ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124));
`Camera.OnFrame` clamps and builds matrices from `LocalRotation` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)). The stock
`FixedController` makes two writes, `Camera.PositionEcl = following.GetPositionEcl() + CameraOffset` and
`Camera.LocalRotation = LookAtRotation(CameraRotation, up)` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47)). A subclass can make the
same two writes, with the pose it computes at that instant. `Camera.PositionEcl` is derived while a body is
followed, so the offset is added to the followed position the engine already advanced in `PrepareFrame`.

## Lifecycle and timing

| Step | Runs | Value |
|---|---|---|
| Borrower decides intent (what to follow, which mode) | `[StarMapAfterGui]` step | `frame:draw-ui` |
| Controller asks the pose source | `GameViewport.OnFrame` -> `FixedController.OnFrame` | `frame:viewports` |
| `Camera.OnFrame` builds the matrices | immediately after | `frame:viewports` |

What is safe to read when the pose is asked: the followed object's position (already advanced by this frame's
`ApplyVehicleSolvers`) and the borrower's own state from the previous step. Anything the borrower samples
earlier in the frame from a mod object is a frame old (see [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). Nothing there may
throw: the frame loop has no catch ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L40-L71)).

## Steps

1. **Define a pose-source contract.** The controller passes the followed position *now* and takes back an
   offset, a forward, an up and a field of view:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L302-L340
internal interface IViewPose
{
    /// <param name="followedEcl">
    /// Where the followed craft is <em>now</em>, from the engine. The offset must be measured
    /// against this and nothing else: a separation taken from a sample of the same craft made in
    /// the GUI hook carries a frame of its motion, which is the fault this interface exists to
    /// remove rather than a second copy of it.
    /// </param>
    /// <param name="fovDeg">
    /// The field this borrower wants, every frame and without exception. Part of the pose rather
    /// than something set on the side, because a borrower that says nothing about the field
    /// inherits whatever the last one left — which can be a few magnified degrees, and a chase
    /// that inherits it flies down a straw. Answering the field it wants is the only way not to have an opinion.
    /// </param>
    bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                 out double3 upEcl, out double fovDeg);

    /// <summary>
    /// The player looking around this view, or null for a view that is not looked around. Non-null
    /// is what hands the right button and the wheel to it rather than to the engine.
    /// </summary>
    IMouseDrag? Orbit { get; }

    /// <summary>Something the mouse moves while this view is held -- a club, or the view itself -- or null to leave the mouse alone.</summary>
    IMouseSwing? Swing => null;

    /// <summary>
    /// Whether the up handed over is the view's own, square to it at any pitch, to be taken as it is
    /// rather than eased towards: a view turned directly by the mouse.
    /// </summary>
    bool UpIsExact => false;

    /// <summary>
    /// Whether this view takes the keyboard and the mouse from the game while it is held, reading them
    /// itself: the course builder, whose keys would otherwise walk the kitten and whose clicks would open
    /// a part's window.
    /// </summary>
    bool TakesInput => false;
}
```

2. **Ask it inside `OnFrame`, first, and keep last frame's fields on any failure.** The write ends up in
   the same two engine properties:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L265-L292
private void AskThePoseSource()
{
    if (Pose is not { } source) return;
    if (Camera.Following is not { } following) return;

    try
    {
        if (!source.TryPose(following.GetPositionEcl(), out double3 offset,
                            out double3 forward, out double3 up, out double fovDeg))
        {
            return;
        }

        if (!Vec.IsFinite(offset) || Vec.Len2(forward) < 0.5) return;

        CameraOffset = offset;
        CameraRotation = forward;
        UpEcl = up;

        // Clamped rather than refused. The engine's own setter does not clamp and the
        // projection throws outside (0, pi) -- from in here, which is the engine's loop.
        KsaWorld.TrySetMainViewFov(fovDeg);
    }
    catch
    {
        // Keep whatever was written a frame ago rather than dropping the view.
    }
}
```

3. **Make the two engine writes with the up substituted.** If no up is given, fall back to `base.OnFrame`:

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

4. **Implement the source in the borrower, as a separation.** KSArmory's chase asks for the pose against a
   followed object that is the round itself, so every term is already an offset and `followedEcl` is
   deliberately unused:

```csharp
// src/KSArmory/Ksa/ChaseCamera.cs L851-L864, L893-L898
/// <summary>
/// Where the view goes, asked from inside the engine's own frame pass.
///
/// <para><paramref name="followedEcl"/> is deliberately unused: the view follows the round
/// itself, and every term of this pose is already a separation from it. The sight needs that
/// argument because its eye is an absolute position on a craft; nothing here is.</para>
/// </summary>
public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                    out double3 upEcl, out double fovDeg)
{
    offsetFromFollowed = forwardEcl = upEcl = Vec.Zero;
    fovDeg = _poseFovDeg;

    if (!_saved.Valid || !(fovDeg > 0.0)) return false;
    // ...

    if (!TryPoseFor(round, out double3 eye, out double3 forward, out double3 up, out _)) return false;

    LookAround(eye, forward, up, out offsetFromFollowed, out forwardEcl, out upEcl);
    return true;
}
```

5. **Keep the hook for intent.** The borrower still takes the view, records the previous mode and follow,
   writes `CameraRotation` and `CameraOffset` and sets `Pose = this` from the GUI hook
   ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1821-L1874)); the controller asks again each frame.

## Complete example

- [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L39-L191) and [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1734-L1766)
- [src/KSAStructures/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/LevelHorizonController.cs#L117-L158)
- [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs#L851-L898): `TryPose` with the burst-hold branches

## Variations and gotchas

- **Do not re-read absolute positions.** KSArmory notes that `PrepareFrame` has advanced vehicles but the mod has not stepped its
  rounds when the pose is asked, so re-reading both absolute positions would leave a frame of the round's
  ecliptic travel in their difference (about 500 m at 60 fps). The pose reads only settled state: an offset
  from the followed object ([Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)).
- **Asked twice a frame.** If the source is also called from the GUI hook (`Apply`), advance transitions in only one of them.
- **Fallback to stock.** A zero up or a refused pose runs `base.OnFrame`; keep `CameraRotation` non-zero when that is reachable.
- **Clamp after.** `Camera.OnFrame` still applies the 0.5 m terrain clamp ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L639)).

## See also

- [Topic overview](overview.md); [approach 2](write-the-camera-pose-in-phase-with-the-frame-2.md), [3](write-the-camera-pose-in-phase-with-the-frame-3.md), [4](write-the-camera-pose-in-phase-with-the-frame-4.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md), [A replacement fixed camera controller with an up vector and an in-phase pose](../../../../mods/ksastructures/features/level-horizon-camera-controller.md), [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)
- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md), [Mount a camera on a vessel part and look through it](../../secondary-viewports/mount-a-camera-on-a-part/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../understand-camera-modes-and-controllers/overview.md), [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md), [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../special-views/chain-camera-moves-into-keyframe-sequences/overview.md)
