---
type: How-To
title: Follow a point with an anchor IFollowable that re-resolves a vessel, part, body or held point on every call
description: gatOS implements IFollowable as CameraFollowable, whose GetPositionEcl re-resolves its anchor each call, with identity body-fixed rotation, a one metre radius and a stable OrbitView, so the stock camera tracks a point with no registration
category: camera
group: main-view
topic: follow-a-point-that-is-not-a-vehicle
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/synthetic-camera-follow-anchor]
techniques: [custom-followable]
hooks: [implements KSA.IFollowable]
lifecycle: [frame:viewports]
game_types:
  - KSA.IFollowable
  - KSA.Camera
  - KSA.OrbitView
  - KSA.CameraReferenceFrame
  - KSA.WreckageMarker
  - KSA.OrbitController
  - KSA.FixedController
tags: [camera, followable, anchor, orbit-view, gatos]
---
`Camera.Following` is an `IFollowable`, and nothing requires it to be a vehicle or a celestial. gatOS gives the camera a small mod-owned object, `CameraFollowable`,
that resolves a real anchor (a vessel, a part, a body or a held ecliptic point) every time the engine asks. The camera then tracks something the game never
registered, and every offset the mod writes is measured against a position sampled in the same call, which removes the frame-phase mismatch an absolute write suffers at orbital speed.
Prerequisites: StarMap.

## Background: how the game does it

```csharp
// current/decomp/KSA/IFollowable.cs L1-L6
namespace KSA;

public interface IFollowable : IObjectId, IPosition, IVelocity, IOrientation, IRadius
{
	OrbitView OrbitView { get; }
}
```

While a body is followed, `Camera.PositionEcl` is not stored: its getter returns `following.GetPositionEcl() + PositionCce`, where `PositionCce` rotates `LocalPosition` through
`following.GetBodyFixed2Ecl()` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L81-L131)). `WreckageMarker` is the engine's own non-vehicle implementation: a plain class handed to `SetFollow` that is never registered
in a celestial system ([current/decomp/KSA/WreckageMarker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/WreckageMarker.cs#L1-L30)). The stock controllers dereference `following.OrbitView` without a null check
([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L38), [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487-L500)), `MeanRadius` is used as a divisor when the orbit camera's focus radius changes ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L602-L606)), and `SetFollow` teleports the camera to 2.5 mean radii.

## Lifecycle and timing

`GetPositionEcl` runs whenever the engine reads the camera position: in the controller's `OnFrame`, in `Camera.OnFrame`, and later in the frame wherever render code asks for ego positions
(`frame:viewports`, then `frame:render`). Re-resolving on each call from main-thread state means all calls in one frame agree, because between `Apply*Solvers` calls the state is the step
applied at the start of the frame ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). The anchor is re-pointed from the prefix on the main viewport `OnFrame` ([reference, approach 4](../../controllers/understand-camera-modes-and-controllers/overview.md)).

## Steps

1. **Implement the interface with the invariants the stock code needs.** The full member set is `Id`, `Hash`, `Class`, `IsMoon`, `IsStar`, `HasOrbit` (`IObjectId`); `GetPositionEcl`, `GetPositionEclFromCce`, `GetPositionCceFromEcl` (`IPosition`); `GetVelocityEcl` (`IVelocity`); `ShowAxes`, `GetBodyFixed2Ecl`, `GetBodyRates`, `DrawAxes` (`IOrientation`); `MeanRadius` (`IRadius`); and `OrbitView`. The invariants that matter are a non-zero radius, one stable `OrbitView` for the object's lifetime, and an identity `GetBodyFixed2Ecl` so offsets do not roll with a vessel:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs L43-L57
internal sealed class CameraFollowable : IFollowable
{
    /// <summary>Non-zero: the engine divides by the followed object's radius on focus changes.</summary>
    private const double Radius = 1.0;

    private readonly OrbitView _orbitView = new(CameraReferenceFrame.Stars);

    private TargetRef _anchor = TargetRef.None;
    private double3 _heldEcl;

    /// <summary>Where the anchor was last resolved, for when it stops existing mid-frame.</summary>
    internal double3 LastPositionEcl { get; private set; }

    /// <summary>Points the followable at a live anchor (<c>vessel:</c>/<c>body:</c>/<c>part:</c>).</summary>
    internal void Follow(in TargetRef anchor) => _anchor = anchor;
```

2. **Re-resolve the anchor on every call and keep the last sample if it vanishes:**

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs L94-L107
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
```

3. **Return the rest as neutral values.** Velocity from the anchor or zero; `GetBodyFixed2Ecl` identity (frame-relative placement is composed explicitly by the director, not by the engine); no orbit:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs L109-L127
public double3 GetVelocityEcl()
    => _anchor.HasTarget && CameraTargets.TryResolve(_anchor, out var target)
        ? CameraTargets.VelocityEcl(target)
        : double3.Zero;

public double3 GetPositionEclFromCce(double3 positionCce) => GetPositionEcl() + positionCce;

public double3 GetPositionCceFromEcl(double3 positionEcl) => positionEcl - GetPositionEcl();

/// <summary>Identity, so a camera offset does not turn with the anchor (see the type remarks).</summary>
public doubleQuat GetBodyFixed2Ecl() => doubleQuat.Identity;

public double3 GetBodyRates() => double3.Zero;

public bool IsMoon() => false;

public bool IsStar() => false;

public bool HasOrbit() => false;
```

4. **Hold or follow.** `Follow(in TargetRef)` points at a live anchor; `Hold(double3)` seeds a fixed ecliptic point ([gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs#L56-L68)).

5. **Attach it** with `SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false)` and undo the teleport ([approach 3](overview.md), [article](follow-a-point-that-is-not-a-vehicle-3.md)).

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs): the object
- [gatOS.GameMod/Game/Ksa/Camera/CameraTargets.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraTargets.cs): `TryResolve`, `PositionEcl` for vessels, parts and bodies
- [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L592-L598): `Follow`/`Hold` per frame

## Variations and gotchas

- **Always `changeControl: false`.** The default assigns `Program.ControlledVehicle = target as Vehicle`, which is null for a non-vehicle ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)).
- **`SetFollow` always moves the camera.** Write the old `PositionEcl` back when the intent is only to change the target.
- **Identity `GetBodyFixed2Ecl`** keeps offsets in ecliptic axes; return the anchor's real rotation if the camera should turn with it.
- **A mod followable does not survive a save load**: `Camera.DeserializeSave` resolves the saved id through the current system, which returns `Astronomical` only ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L832-L849)); the engine's own
  cleanup is keyed on vehicle destruction. You own the lifetime; `Unfollow(false)` or re-`SetFollow` before dropping the reference.
- **gatOS records this design as replacing an unfollow-and-write-absolute-ECL approach** that left the camera one frame out of register at orbital speed; live revalidation is listed as pending in the repo.

## See also

- [Topic overview](overview.md); [approach 2](follow-a-point-that-is-not-a-vehicle-2.md), [3](follow-a-point-that-is-not-a-vehicle-3.md), [4](follow-a-point-that-is-not-a-vehicle-4.md), [5](follow-a-point-that-is-not-a-vehicle-5.md)
- [Synthetic camera follow anchor](../../../../mods/gatos/features/synthetic-camera-follow-anchor.md)
- [Chase a projectile or other fast body with the main camera](../chase-a-fast-body-with-the-main-camera/overview.md), [Play scripted cinematic camera tracks with blends and easing](../../special-views/play-cinematic-camera-tracks/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md), [Taking control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md)
