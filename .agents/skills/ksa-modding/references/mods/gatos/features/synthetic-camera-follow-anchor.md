---
type: Mod Feature
title: Synthetic camera follow anchor
description: Implement KSA's IFollowable on a mod-owned object that re-resolves a vessel, part, body or fixed point on every call, so the stock camera follows it and every camera pose stays an offset measured in the engine's own frame pass
mod: gatos
category: camera
techniques: [custom-followable]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports]
game_types:
  - KSA.IFollowable
  - KSA.Camera
  - KSA.FixedController
  - KSA.OrbitController
  - KSA.OrbitView
  - KSA.WreckageMarker
  - KSA.GameViewport
tags: [camera, ifollowable, follow, floating-origin, precision]
---

KSA's camera stores its position relative to whatever it follows: with a follow target set,
`Camera.PositionEcl` is the target's `GetPositionEcl()` plus the camera's own offset. gatOS does not
unfollow the camera when a script takes it over. It makes the camera follow a small mod-owned
object, `CameraFollowable`, that implements `IFollowable` and resolves the real anchor (a vessel, a
part, a body or a held ecliptic point) every time the engine asks. Every pose the camera director
writes is then an offset from a position sampled in the same call, which removes the frame-phase
mismatch an absolute ecliptic write suffers at orbital speed. The technique applies to any mod that
wants the stock camera to track something that is not a vehicle: a landmark, a point between two
vessels, a moving waypoint.

## How it works

**The interface.** `IFollowable` is `IObjectId, IPosition, IVelocity, IOrientation, IRadius` plus an
`OrbitView` property ([current/decomp/KSA/IFollowable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IFollowable.cs#L1-L6)). Nothing has to be
registered with the game: `KSA.WreckageMarker` is the engine's own non-vehicle implementation
([current/decomp/KSA/WreckageMarker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/WreckageMarker.cs#L6-L6)).[^ksa-ifollowable]

**What the camera does with it** ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L80-L130)):

- `PositionEcl` get returns `_following.GetPositionEcl() + PositionCce`; set stores
  `value - _following.GetPositionEcl()`.
- `PositionCce` transforms `LocalPosition` through `following.GetBodyFixed2Ecl()` unless the camera
  has `NoRotation`.
- `SetFollow(target, tidalLocking, changeControl = true, alert = true)` teleports the camera to
  `target.GetPositionEcl() + MeanRadius * 2.5 * forward`, and with `changeControl` true assigns
  `Program.ControlledVehicle = target as Vehicle`, which is null for a non-vehicle
  ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)).
- The stock controllers dereference `following.OrbitView` without a null check
  ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L36),
  [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L500-L510)).

**gatOS's implementation** ([gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs#L43-L132)):

| Member | Value | Why |
|---|---|---|
| `GetPositionEcl()` | resolves the current `TargetRef` (vessel, part or body) on every call, or returns the held point; a vanished anchor keeps its last sample | the answer is computed in the caller's epoch, not cached from the command drain |
| `GetVelocityEcl()` | the anchor's velocity, or zero | |
| `GetBodyFixed2Ecl()` | `doubleQuat.Identity` | camera offsets must not rotate with a rolling vessel; frame-relative placement is composed explicitly in the director |
| `MeanRadius` | 1 m | must be non-zero (gatOS notes say the engine divides by it on focus changes); keeps `SetFollow`'s teleport at 2.5 m |
| `OrbitView` | one `OrbitView(CameraReferenceFrame.Stars)` instance for the object's lifetime | dereferenced unguarded by the controllers |
| `Id`, `Hash`, `Class` | `gatos.camera.anchor`, `KeyHash.Make(Id)`, `GatosCameraAnchor` | identity for UI and lookups |

**How the director uses it** ([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L256-L262)):
on take it holds the anchor at the captured camera position, calls
`SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false)` and immediately
writes the captured position and rotation back to undo the teleport. Each frame it calls
`_followable.Follow(pose.Anchor)` or `Hold(...)` for the composed pose
([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L592-L598)), and it
stands down if `camera.Following` is no longer its object, which is how it detects another mod or
the player taking the camera back ([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L556-L560)).
The same `SetFollow` plus position restore is used to change `TidalLocking`, which has no setter.

## Lifecycle and timing

- The anchor is re-pointed in the main-viewport `GameViewport.OnFrame` prefix (`DriveCamera` ->
  `CameraDirector.Update`, `frame:viewports`), before KSA runs the active controller and
  `Camera.OnFrame` for that viewport.
- `GetPositionEcl()` itself runs whenever the engine reads the camera's position: in the
  controller's `OnFrame`, in `Camera.OnFrame`, and later in the frame wherever render code asks the
  camera for ego positions. Each call re-resolves the anchor from main-thread state, which between
  `ApplyVehicleSolvers` calls is the step applied at the start of the frame, so all calls in one
  frame agree.
- Main thread only; the object holds no locks.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs) | the `IFollowable` implementation |
| [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L213-L262) | take: follow the anchor, undo the teleport |
| [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L1148-L1157) | re-issue `SetFollow` to change tidal locking |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs L94-L119
    public double3 GetPositionEcl()
    {
        if (!_anchor.HasTarget)
        {
            LastPositionEcl = _heldEcl;
        }
        else if (CameraTargets.TryResolve(_anchor, out var target))
        {
            LastPositionEcl = CameraTargets.PositionEcl(target);
        }

        // A despawned anchor keeps the last live sample: hold, don't fling.
        return LastPositionEcl;
    }
    // ...
    public double3 GetPositionEclFromCce(double3 positionCce) => GetPositionEcl() + positionCce;

    public double3 GetPositionCceFromEcl(double3 positionEcl) => positionEcl - GetPositionEcl();

    /// <summary>Identity, so a camera offset does not turn with the anchor (see the type remarks).</summary>
    public doubleQuat GetBodyFixed2Ecl() => doubleQuat.Identity;
```

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L256-L262
        _followable.Hold(_restorePositionEcl);

        var owned = viewport.BaseCamera;
        owned.NoRotation = false; // insurance: a stuck map flag would re-interpret every write below
        owned.SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false);
        owned.PositionEcl = _restorePositionEcl; // undo SetFollow's 2.5 m teleport
        owned.LocalRotation = _restoreLocalRotation;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `IFollowable` | [current/decomp/KSA/IFollowable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IFollowable.cs#L1-L6) | implemented by the anchor |
| `Camera.SetFollow`, `Following`, `TidalLocking` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618) | follow the anchor without changing control |
| `Camera.PositionEcl`, `PositionCce` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L80-L130) | offsets relative to the followed object |
| `FixedController.OnFrame`, `OrbitController` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L36) | read `following.OrbitView` |
| `WreckageMarker` | [current/decomp/KSA/WreckageMarker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/WreckageMarker.cs#L6-L6) | the engine's own non-vehicle `IFollowable` |

## Pitfalls and version notes

- Always pass `changeControl: false`: the default assigns `Program.ControlledVehicle = target as
  Vehicle`, which hands control to nothing when the target is not a vehicle.
- `SetFollow` always moves the camera; write the old `PositionEcl` back after it when the intent is
  only to change the follow target or the tidal-locking flag.
- Return a stable `OrbitView` instance and a non-zero `MeanRadius`.
- An identity `GetBodyFixed2Ecl` keeps offsets in ecliptic axes; return the anchor's real rotation
  instead if the camera should turn with it.
- gatOS records this design as replacing an earlier "unfollow and write absolute ECL" approach
  that left the camera one frame out of register at orbital speed (anchor notes in
  `CameraDirector.cs`); in-game validation of the camera recheck is listed as pending in the repo.

## See also

- [Programmable camera director](programmable-camera-director.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [gatOS overview](../overview.md)
- [Offset against the followed object sampled in the same call, with sampling and publishing on GameViewport.OnFrame](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-2.md) - how-to article demonstrating this feature
- [Follow a point with an anchor IFollowable that re-resolves a vessel, part, body or held point on every call](../../../howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-1.md) - how-to article demonstrating this feature
- [Compose each frame's pose from keyframed tracks, manual overrides and a captured baseline](../../../howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-1.md) - how-to article demonstrating this feature

[^ksa-ifollowable]: KSA.IFollowable (decompiled, build 2026.10.10.5554)
