---
type: How-To
title: Concatenate GetBody2Cci with the inverse ENU frame and call ToRollPitchYawRadians
description: Compute roll, pitch and heading from the vehicle quaternion and the game's local ENU frame, plus angle of attack and sideslip from the body-frame velocity
category: flight-and-orbits
group: flight-state
topic: read-flight-state-in-a-local-horizon-frame
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-aircrafthud/features/flight-data-from-vehicle]
techniques: [custom-physics, harmony-postfix]
hooks: [harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [frame:viewports]
game_types: [KSA.Vehicle, KSA.VehicleReferenceFrameEx, KSA.QuaternionEx, Brutal.Numerics.doubleQuat]
tags: [attitude, roll, pitch, heading, enu, angle-of-attack, sideslip, hud]
---

Outcome: aircraft-style roll, pitch and heading, and alpha and beta, from three or four game calls.
KSA_AircraftHUD demonstrates it.[Surface attitude and air data from the controlled vehicle](../../../../mods/ksa-aircrafthud/features/flight-data-from-vehicle.md) Prerequisites: StarMap and Harmony
(only for the hook); the math is plain C# on `Brutal.Numerics` types.

## Background: how the game does it

See the [overview](overview.md) for `GetBody2Cci`, `GetEnuBody2Cci`, the XYZ Euler extraction and the
scalar-last `doubleQuat`. The idea: `Concatenate(a, b)` composes the rotations in the game's order, so
`Concatenate(vehicle.GetBody2Cci(), enuBody2Cci.Inverse())` is the vehicle body relative to the ENU-aligned body
frame, and its Euler angles are attitude relative to the horizon. The same helper is what the game uses
for its `EnuBody` navball frame, so the angles agree with the stock navball conventions.

## Lifecycle and timing

Run it after the solver results are applied. The HUD patches `Program.OnFrameEditor` with a postfix
(`frame:viewports`), which executes after `PrepareFrame`; the position and quaternion are therefore of the step
applied this frame. Do not call it from a worker thread. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Hook a main-thread point** after `PrepareFrame` (the HUD uses `[HarmonyPatch("OnFrameEditor")]` with a postfix).
2. **Compose and extract the angles.** `GetEnuBody2Cci` returns `null` at the centre or over a pole, so default to
   `Identity`:

```csharp
// AircraftHUDMain.cs L238-L245
public static double3 GetSurfaceAttitude(Vehicle vehicle)
{
    double3 Body2Cci = vehicle.GetBody2Cci().ToRollYawPitchRadians();

    doubleQuat enuBody2Cci = VehicleReferenceFrameEx.GetEnuBody2Cci(vehicle.GetPositionCci()) ?? doubleQuat.Identity;
    doubleQuat attitudeQuat = doubleQuat.Concatenate(vehicle.GetBody2Cci(), enuBody2Cci.Inverse());
    return attitudeQuat.ToRollPitchYawRadians();
}
```

   `EulerRad.X` is roll, `.Y` pitch and `.Z` heading in the HUD's use.
3. **Convert to display angles.** The HUD adds 90 degrees (`RadToDeg`) and wraps; roll and heading to 0..360,
   pitch to a +/-90 range:

```csharp
// AircraftHUDMain.cs L302-L327
public static float RadToDeg(float angle)
{
    return (angle + (float)Math.PI / 2.0f) * (180.0f / (float)Math.PI);
}

public static float NormalizeDeg(float d)
{
    d %= 360f;
    if (d < 0) d += 360f;
    return d;
}

public static float AngleRadToDegClamped(float angle)
{
    float deg = RadToDeg(angle);
    return NormalizeDeg(deg);
}

public static float AngleRadToDegHalfClamped(float angle)
{
    float deg = RadToDeg(angle);
    deg %= 360.0f;
    deg -= 90.0f;
    if (deg > 90) deg -= 360.0f;
    return deg;
}
```

   The shift is a property of the HUD's conventions, not of the game: check the zero point against the stock navball
   before reusing it.
4. **Alpha and beta.** Rotate the velocity into body axes with the inverse of `GetBody2Cci()` and take two
   `atan2`s:

```csharp
// AircraftHUDMain.cs L227-L236
public static double2 GetAlphaBeta(Vehicle vehicle)
{
    var state = vehicle.Orbit.StateVectors;
    var cci2body = vehicle.GetBody2Cci().Inverse();
    var velBody = (cci2body * state.VelocityCci).Normalized();

    var alpha = Math.Atan2(-velBody.X, -velBody.Z);
    var beta = Math.Atan2(-velBody.X, velBody.Y);
    return new double2(alpha, beta); // radians
}
```

   With body +X as the long axis, velocity along +X gives `atan2(-1, 0)` = -90 degrees for both, which is why the shift
   makes level flight read zero. Positive directions follow the body axis signs.
5. **Choose the right velocity.** The HUD feeds the inertial `Orbit.StateVectors.VelocityCci`, so on a runway the
   flight-path marker is offset by the planet's rotation. For air-relative angles use `GetSurfaceVelocityCci()`:

   ```csharp
   // adapted from KSA_AircraftHUD/AircraftHUDMain.cs GetAlphaBeta
   var cci2body = vehicle.GetBody2Cci().Inverse();
   var velBody = (cci2body * vehicle.GetSurfaceVelocityCci()).Normalized();
   ```

   There is no wind vector in the game's air velocity (`ComputeAirVelocityBody` removes only the frame rotation), so
   "surface-relative" is the best air-relative velocity available.
6. **Guard degenerate cases**: `velBody` of zero length (a stationary craft) makes the angles meaningless;
   skip them.

## Complete example

[AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L220-L263) holds the helper methods; the call sites that convert and print them are
[AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1076-L1085). The hook is [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1332-L1341).

## Variations and gotchas

- **Do not assume the Euler order.** The game picks `ToRollPitchYawRadians` (XYZ) for `EnuBody` and
  `ToRollYawPitchRadians` for other frames; mixing them swaps pitch and yaw.
- **Gimbal lock.** XYZ extraction becomes ill-conditioned near +/-90 degrees of the middle angle; a vertically
  pointing rocket needs a different decomposition (approach 2 avoids Euler angles altogether).
- **Dead code in the source.** `GetSurfaceAttitude` computes `Body2Cci` from `ToRollYawPitchRadians()` and
  never uses it.
- A crash risk elsewhere in the same file (casting `Orbit.Parent` to `Celestial` around a star) is described in
  [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md).

## See also

- [Overview](overview.md); [basis-vector approach](read-flight-state-in-a-local-horizon-frame-2.md)
- [Surface attitude and air data from the controlled vehicle](../../../../mods/ksa-aircrafthud/features/flight-data-from-vehicle.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md), [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../understand-ksa-coordinate-frames-and-epochs/overview.md)
