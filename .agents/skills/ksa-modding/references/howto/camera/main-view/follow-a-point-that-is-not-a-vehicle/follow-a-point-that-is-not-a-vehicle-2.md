---
type: How-To
title: Report the drawn position of a mod-simulated round from a RoundFollowable
description: KSArmory's RoundFollowable answers the position a round is drawn at this frame, with a non-zero radius, a stable OrbitView and an identity body-fixed rotation, so the camera and the picture agree and the stock controllers do not hit a null OrbitView
category: camera
group: main-view
topic: follow-a-point-that-is-not-a-vehicle
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/chase-camera-on-a-round]
techniques: [custom-followable]
hooks: [implements KSA.IFollowable]
lifecycle: [frame:viewports, frame:render]
game_types:
  - KSA.IFollowable
  - KSA.Camera
  - KSA.OrbitView
  - KSA.WreckageMarker
  - KSA.OrbitController
  - KSA.FixedController
  - KSA.Program
tags: [camera, followable, projectile, drawn-position, chase]
---
KSArmory simulates its rounds itself, so a round is not a `Vehicle` and the stock follow modes do not apply. `RoundFollowable` is an `IFollowable` whose `GetPositionEcl`
answers where the round's body is **drawn** this frame, so the eye and the mesh come out of one expression. It is the
chase camera's follow target. This article is the object; the camera that uses it is
[Chase a projectile or other fast body with the main camera](../chase-a-fast-body-with-the-main-camera/overview.md).

## Background: how the game does it

`IFollowable` is `IObjectId, IPosition, IVelocity, IOrientation, IRadius` plus `OrbitView` ([current/decomp/KSA/IFollowable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IFollowable.cs#L1-L6)). Following works because `Camera.PositionEcl` is not stored while something is followed
([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L131)), so every read re-evaluates the followed object. What the stock code requires from a mod implementation, read from the call sites:

| Requirement | Why | Source |
|---|---|---|
| `OrbitView` non-null and stable | Dereferenced unguarded | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L38), [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487-L500) |
| `MeanRadius` greater than zero | A divisor when the orbit camera's focus radius changes, and the teleport scale of `SetFollow` | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L602-L606), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618) |
| Cheap, consistent `GetPositionEcl` | Called several times per frame from several places | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L131) |
| `GetBodyFixed2Ecl` identity if offsets must not roll | Applied on every `PositionCce` get/set | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L81-L108) |

## Lifecycle and timing

`GetPositionEcl` runs in the controller's `OnFrame` (`frame:viewports`) and again when `Render` builds per-viewport shader data (`frame:render`). KSArmory steps its rounds in the GUI hook
*after* the viewport pass, so a round's position changes between those two reads: the render sees the eye at the round's new drawn position because the getter re-evaluates
([Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)). The pose rotation is solved in the next viewport pass from settled state ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **Declare the invariants as constants and fields.** One `OrbitView` in the `Stars` frame, a one metre radius:

```csharp
// src/KSArmory/Ksa/RoundFollowable.cs L16-L19
// Not zero: the engine divides by it when a camera changes focus.
private const double Radius = 1.0;

private readonly OrbitView _orbitView = new(CameraReferenceFrame.Stars);
```

2. **Answer the drawn position through a fallback ladder inside a `try`**, because the engine calls it from its own loop: the drawn body from the weapon's `TryRoundEffectEcl`, the launcher plus offset, the round's own position, a ground-anchored burst point, then a craft plus an offset:

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

3. **Keep the identity metadata**:

```csharp
// src/KSArmory/Ksa/RoundFollowable.cs L115-L125
public string Id => "KSArmory.Round";

public KeyHash Hash => KeyHash.Make(Id.AsSpan());

public string Class => "KSArmoryRound";

public double MeanRadius => Radius;

public OrbitView OrbitView => _orbitView;

public bool ShowAxes { get; set; }
```

   with `GetBodyFixed2Ecl()` returning `doubleQuat.Identity` so an offset does not turn with the round ([src/KSArmory/Ksa/RoundFollowable.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundFollowable.cs#L196-L196)).

4. **Change what it points at without losing the camera**: `Track(round, source)` for a flying round, `HoldAgainst`/`HoldAt` for the burst point (anchored in the body-fixed frame of the ground it happened over, because ground turns and a bare ecliptic point is left behind at about 29.8 km/s), `HoldWhereItIs` when the craft that fired it has gone
   ([src/KSArmory/Ksa/RoundFollowable.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundFollowable.cs#L30-L105)).

## Complete example

- [src/KSArmory/Ksa/RoundFollowable.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/RoundFollowable.cs)
- [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs): the user ([Chase a projectile or other fast body with the main camera](../chase-a-fast-body-with-the-main-camera/overview.md))

## Variations and gotchas

- **Why not follow the launching craft.** `PrepareFrame` applies the vehicle solvers before `OnFrameViewports` while the mod integrates its rounds after, so an offset measured against the craft gets a frame-newer platform position: about 500 m of camera displacement per frame near Earth.
- **`Camera.OnFrame` still clamps** the camera to 0.5 m above terrain, so the ladder anchors to the platform's live position rather than the mod's last sample.
- **A mod followable does not survive a save load** and nothing cleans it up when the mod object dies.
- **Never zero for `MeanRadius`**: the orbit camera divides by it when the focus radius changes (`_lastFocusedRadius / meanRadius`), which yields an infinite or NaN orbit distance.

## See also

- [Topic overview](overview.md); [approach 1](follow-a-point-that-is-not-a-vehicle-1.md), [4](follow-a-point-that-is-not-a-vehicle-4.md)
- [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md), [Mod-simulated rounds integrated in sub-steps inside the frame](../../../../mods/ksarmory/features/mod-simulated-rounds.md)
- [Chase a projectile or other fast body with the main camera](../chase-a-fast-body-with-the-main-camera/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md)
