---
type: How-To
title: Raycast-style suspension impulses in the PrepareWorker prefix
description: Hold a craft on per-wheel spring-dampers sized from its weight share and grip it with friction-circle tyres that cancel side slip first, summing everything into one linear and one angular impulse written in a Vehicle.PrepareWorker prefix
category: physics
group: aero-and-ground
topic: simulate-wheels-and-suspension-without-a-wheel-module
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksacars/features/wheel-suspension-tyre-model
  - mods/ksacars/features/terrain-contact-under-wheels
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.PhysicsStates]
tags: [physics, wheels, suspension, spring-damper, anti-roll, tyres, friction-circle, steering, impulse]
---

Outcome: a craft that stands on springs above the ground, corners, brakes and drives, using only the colliders it already has
(held clear of the ground) and one velocity and spin change per frame. Prerequisites: StarMap and Harmony, and a way to get the
height of each wheel hub above the ground (see
[sample the ground under each wheel](../sample-the-ground-under-each-wheel/overview.md)). Demonstrated by KSACars, whose two
cars (a beach buggy and a Cadillac) are single parts with data-driven corner tables.

## Background: how the game does it

KSA has no wheel, tyre or suspension: the physics step integrates a rigid body, contacts come from Bepu colliders, and terrain
contact uses one friction value for every collider, so a wheel-shaped collider would drag like a skid. KSACars therefore keeps its
colliders 18 to 25 cm clear of the ground at rest (they only touch in a crash) and holds the car up with springs it simulates.

The engine has no hook for external forces during a substep, but it does have a settled window. `Universe.ExecuteNextVehicleSolvers`
calls the virtual `Vehicle.PrepareWorker(SimStep)` on the main thread for every vehicle immediately before it copies the
vehicle's state for the worker, and `GetPhysicsStatesMutable()` returns a `PhysicsStates` (a `ref struct`) whose fields alias the
live vehicle's, so a velocity written in a prefix on `PrepareWorker` is part of the next step.[^ksa-vehicle][^ksa-universe]
The step the worker integrates is `simStep.DeltaTime`, the frame time times the simulation speed. See
[inject forces and impulses at the physics-safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-1.md)
for the mechanism.

## Lifecycle and timing

- **Where: `Vehicle.PrepareWorker` prefix, `frame:dispatch-solvers`, main thread.** `Program.PrepareFrame` has just applied the
  last step and the input events; the prefix runs per vehicle right before the snapshot; the engine then integrates the whole
  step with the velocity and spin change already applied. The model never runs per physics substep.[^ksacars-physicshook]
  See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step).
- **`dt` is long under warp.** `Buggies.Step` skips the springs and tyres entirely when `dt > 0.1` s (the car rests on its
  colliders), writes nothing while `dt` is 0 (paused), and returns early for a parked car on rails with no input.[^ksacars-buggies]
  The tests run the model at several fixed `dt` values to prove it stays stable at real frame rates.[^ksacars-drivetests]
- **Per-step shares, not rates.** Side grip is asked to cancel a share of the sideways speed within the step (`Settle / dt`), not
  an absolute force, so the correction scales with the step and does not ring.

## Steps

1. **Give the model its inputs: a contact per wheel.** For each corner, `HubHeight` (height of the hub's rest point above the
   ground along the ground's up), `GroundUp` (radial, in the car's body frame) and `HubVelocity` (velocity over the ground, in the
   body frame), plus the hub positions relative to the centre of mass, the car's up and forward, mass, gravity, air density and
   `dt`. KSACars builds these in `Buggies.Step` from the planet-fixed physics state (see the sampling topic):[^ksacars-buggydrive]

```csharp
// src/KSACars/Sim/BuggyDrive.cs L9-L16
public readonly record struct WheelContact(bool Valid, double HubHeight, double3 GroundUp, double3 HubVelocity);

/// <param name="Throttle">-1 full reverse or brake, +1 full forward.</param>
/// <param name="Steer">-1 full right, +1 full left.</param>
public readonly record struct DriveInput(double Throttle, double Steer);

/// <summary>What a step asks of the craft: an impulse through its centre of mass and one about it, body frame.</summary>
public readonly record struct DriveImpulse(double3 Linear, double3 Angular);
```

2. **Size each spring from the wheel's static weight share.** Work out each wheel's share of the weight from where the centre of
   mass sits between the axles (clamped 10 to 90 percent front), then `omega = 2 pi SpringHz`, `k = m omega^2` and
   `c = 2 DampingRatio m omega`, with `m = mass * share`. Because the load is preloaded with the wheel's weight, rest is zero
   compression for any car:

```csharp
// src/KSACars/Sim/BuggyDrive.cs L238-L261
    // Each wheel's share of the car's weight at rest, from where the centre of mass sits between the axles.
    private void StaticShares(ReadOnlySpan<double3> hubs, double3 forward, Span<double> share)
    {
        double front = 0.0, rear = 0.0;
        int nFront = 0, nRear = 0;
        for (int i = 0; i < hubs.Length; i++)
        {
            double y = Vec.Dot(hubs[i], forward);
            if (Profile.Corners[i].Steers) { front += y; nFront++; }
            else { rear += y; nRear++; }
        }
        if (nFront == 0 || nRear == 0)
        {
            share.Fill(1.0 / hubs.Length);
            return;
        }
        front /= nFront;
        rear /= nRear;
        double frontShare = Math.Clamp(-rear / (front - rear), 0.1, 0.9);
        for (int i = 0; i < hubs.Length; i++)
        {
            share[i] = Profile.Corners[i].Steers ? frontShare / nFront : (1.0 - frontShare) / nRear;
        }
    }
```

3. **Measure compression and closing speed for every wheel before working out any spring.** The compression is the wheel radius
   minus the hub's reach to the ground along the car's own up (`HubHeight / tilt`, contact ignored when the ground is tilted more
   than about 78 degrees from the car's up); the closing speed is the hub's speed into the ground. An axle's anti-roll bar needs
   both its wheels' values first:

```csharp
// src/KSACars/Sim/BuggyDrive.cs L102-L113
        // How far each wheel is pushed up, from the ground below its hub along the car's own up; an
        // axle's anti-roll bar needs both of its wheels' before either spring can be worked out.
        Span<double> compressions = stackalloc double[n];
        Span<double> closings = stackalloc double[n];
        for (int i = 0; i < n; i++)
        {
            WheelContact c = contacts[i];
            double tilt = c.Valid ? Vec.Dot(up, c.GroundUp) : 0.0;
            double reach = tilt > 0.2 ? c.HubHeight / tilt : double.PositiveInfinity;
            compressions[i] = c.Valid ? Profile.Corners[i].Radius - reach : double.NegativeInfinity;
            closings[i] = c.Valid ? -Vec.Dot(c.HubVelocity, c.GroundUp) / Math.Max(tilt, 0.2) : 0.0;
        }
```

4. **Work out the load: spring, damper, anti-roll, roll damper, bump stop, clamp, droop.** A wheel past full droop is airborne and
   eases toward full droop at 1.5 m/s. The load is clamped to 0 to 4 times the wheel's weight share so a car placed with its
   wheels in the ground is not thrown:

```csharp
// src/KSACars/Sim/BuggyDrive.cs L115-L148
        for (int i = 0; i < n; i++)
        {
            BuggyCorner corner = Profile.Corners[i];
            WheelContact c = contacts[i];
            double m = mass * share[i];
            double k = m * omega * omega;
            double damping = 2.0 * Profile.DampingRatio * m * omega;

            double tilt = c.Valid ? Vec.Dot(up, c.GroundUp) : 0.0;
            double compression = compressions[i];

            if (!c.Valid || compression < -Profile.DroopTravel)
            {
                Grounded[i] = false;
                Travel[i] = MoveToward(Travel[i], -Profile.DroopTravel, 1.5 * dt);
                Spin[i] += WheelSpinRate(i, corner, drivenOnGround, input.Throttle) * dt;
                continue;
            }

            Grounded[i] = true;
            Travel[i] = Math.Min(compression, Profile.BumpTravel);

            double closing = closings[i];
            double spring = (m * gravity) + (k * compression) + (damping * closing);
            int partner = AxlePartner(i);
            if (partner >= 0)
            {
                double other = Math.Clamp(compressions[partner], -Profile.DroopTravel, Profile.BumpTravel);
                spring += Profile.AntiRoll * k * (Math.Min(compression, Profile.BumpTravel) - other);
                // Half the difference is this wheel's speed in a pure roll, and nothing in a bounce.
                spring += Profile.RollDamping * m * omega * (closing - closings[partner]);
            }
            if (compression > Profile.BumpTravel) spring += Profile.BumpStopFactor * k * (compression - Profile.BumpTravel);
            double load = Math.Clamp(spring, 0.0, MaxLoadFactor * m * gravity);
```

## The tyre and steering

5. **Side slip first, then drive in the remaining friction circle.** The wheel heading is the car's forward turned by the steer
   angle about the car's up (steered wheels only), projected onto the ground plane to give `along` and `side`. The side force
   asks to cancel `Settle = 0.7` of the wheel's sideways speed within this step (`-vSide * m * Settle / dt`); cancelling less than
   all of it keeps four wheels from overshooting through the yaw inertia. The side force is clamped to the circle of radius
   `grip * load` first, and drive or brake gets only what is left, so a tyre that is cornering hard gives up drive before it gives
   up its line. The forces become a linear impulse (`force * dt`) and an angular one about the car's centre of mass, plus a roll
   moment from the side force acting at `RollCentreHeight`:

```csharp
// src/KSACars/Sim/BuggyDrive.cs L150-L180
            double3 normal = c.GroundUp;
            double3 heading = corner.Steers
                ? (doubleQuat.CreateFromAxisAngle(up, SteerAngle) * forward)
                : forward;
            double3 along = Vec.Unit(Vec.RejectFrom(heading, normal));
            double3 side = Vec.Cross(normal, along);

            double vAlong = Vec.Dot(c.HubVelocity, along);
            double vSide = Vec.Dot(c.HubVelocity, side);

            double grip = corner.Steers ? Profile.FrontGrip : Profile.RearGrip;
            double limit = grip * load;

            double fAlong = LongitudinalForce(corner, input.Throttle, drive, drivenOnGround, vAlong, m, load, dt);
            double fSide = -vSide * m * Settle / dt;

            // Holding the line comes first and pushing gets what is left: a tyre that spends its grip on
            // drive before cornering lets go of the tail the moment the throttle opens in a turn.
            fSide = Math.Clamp(fSide, -limit, limit);
            double spare = Math.Sqrt(Math.Max((limit * limit) - (fSide * fSide), 0.0));
            fAlong = Math.Clamp(fAlong, -spare, spare);

            double3 force = (normal * load) + (along * fAlong) + (side * fSide);
            double3 contact = hubs[i] - (up * (corner.Radius - Travel[i]));

            linear += force * dt;
            angular += Vec.Cross(contact, force) * dt;
            angular += Vec.Cross(up * Profile.RollCentreHeight, side * fSide) * dt;

            Spin[i] += vAlong / corner.Radius * dt;
        }
```

   The longitudinal force is a launch-acceleration curve, not a driveline: `throttle * LaunchAccelG * g * mass` fading
   linearly to zero at `TopSpeed`, split over the driven wheels on the ground; pressing against the direction of travel brakes
   all four wheels (`min(BrakeG * load, |v| m / dt)`); a standing car with no throttle cancels 0.7 of its rolling speed so it
   parks on a slope.

6. **Limit steering lock by speed and grip.** The lock is the angle whose turn the front tyres can just hold at the current
   speed (`v^2 / R = grip * g` with `R = wheelbase / tan(angle)`), never below 3 degrees and never above `MaxSteerDeg`, approached
   at `SteerRateDegPerSec`. A full-lock flick at speed then turns the car instead of scrubbing it to a standstill:

```csharp
// src/KSACars/Sim/BuggyDrive.cs L211-L224
    // The lock is the angle whose turn the front tyres can just hold at this speed, v^2 / R = grip g
    // with R = wheelbase / tan(angle), so a flick at speed turns the car rather than scrubbing it to a stop.
    private void SteerToward(double steer, double gravity, double dt)
    {
        double lockRad = Profile.MaxSteerDeg * Math.PI / 180.0;
        double v2 = ForwardSpeed * ForwardSpeed;
        if (v2 > 1.0)
        {
            double held = Math.Atan(Wheelbase() * Profile.FrontGrip * gravity * Profile.SteerOverGrip / v2);
            lockRad = Math.Min(lockRad, Math.Max(held, 3.0 * Math.PI / 180.0));
        }
        double wanted = Math.Clamp(steer, -1.0, 1.0) * lockRad;
        SteerAngle = MoveToward(SteerAngle, wanted, Profile.SteerRateDegPerSec * Math.PI / 180.0 * dt);
    }
```

7. **Write the impulse into the vehicle.** Divide the linear impulse by the mass, apply the inverse inertia tensor to the angular
   one, refuse non-finite results, and add both (rotated into the physics frame) to the mutable state in the prefix:

```csharp
// src/KSACars/Ksa/Buggies.cs L689-L708
        double3 dv = impulse.Linear / mass;
        Symmetric3x3 inverse = Symmetric3x3.Invert(craft.TotalMassPropsBody.Inertia);
        double3 l = impulse.Angular;
        double3 dw = new((inverse.XX * l.X) + (inverse.YX * l.Y) + (inverse.ZX * l.Z),
                         (inverse.YX * l.X) + (inverse.YY * l.Y) + (inverse.ZY * l.Z),
                         (inverse.ZX * l.X) + (inverse.ZY * l.Y) + (inverse.ZZ * l.Z));

        // The wheels are off the ground, so the keys that drove them fly the car instead.
        // Under warp the wheels are not stepped, so the hull's own contact says whether it is down.
        bool airborne = warped ? !e.Scraping : !e.Drive.Grounded.Any(g => g);
        double3 groundUpBody = Vec.Unit(positionCcf).Transform(ccf2Body);
        LiftPush lift = Lift.Step(new LiftInput(e.Rockets, e.Input.Throttle, e.Input.Steer, e.Turn), airborne, up, forward,
                                  groundUpBody, spinBody, gravity, dt);
        dv += hovering ? Hover.Push(up, groundUpBody, e.Climb, gravity, dt) : lift.Velocity + Downforce.Push(pressed, up, dt);
        dv += Boost.Push(e.Boosting, forward, dt);
        dw += lift.Spin;
        if (!Vec.IsFinite(dv) || !Vec.IsFinite(dw)) return;

        states.Kinematic.VelocityPhys += dv.Transform(body2Phys);
        states.Kinematic.AngularVelocityPhys += dw.Transform(body2Phys);
```

   (The lift, hover, boost and downforce terms are KSACars' extras, covered by
   [inject forces and impulses, approach 4](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-4.md);
   drop them for a plain wheel model.) A landed craft is on rails, so the prefix wakes it the way KSA wakes a craft something
   has bumped into: `if (railed) craft.TakeOffRails();` after the write (see
   [keep a pushed craft off rails](../../solver/keep-a-pushed-craft-off-rails/overview.md)).

8. **Keep the guard in front of all of it.** The model is skipped for a long step:

```csharp
// src/KSACars/Ksa/Buggies.cs L568-L572
        // The engine integrates a frame as one impulse followed by its own sub-steps, so a spring is
        // only as stable as the frame is short. Under warp the springs are left out and the car rests on
        // its colliders; the rockets are a plain push, good for a step of any length, and carry on.
        if (!(dt > 0.0)) return;
        bool warped = dt > 0.1;
```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Sim/BuggyDrive.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyDrive.cs) | The model: suspension, tyre, steering, drive, air, RPM, pose helpers; no KSA types |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs) | Corner tables and tuned numbers for each car (spring rate, damping ratio, travel, anti-roll, grip, steering) |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L536-L714) | Builds the contacts, calls `Drive.Step`, writes the impulse into the vehicle |
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs) | The `PrepareWorker` prefix |
| [tests/KSACars.Tests/BuggyDriveTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BuggyDriveTests.cs) | Headless rig: settle, pull away, turn, brake, yaw kick, full-lock flick |

## Variations and gotchas

- **Colliders must not touch the ground at rest.** KSA's terrain contact uses one friction value for every collider; KSACars' wheel
  pads end 18 to 25 cm above the ground, and at 10 to 16 cm the outside pad touched in a hard turn and tripped the car.
- **Inertia matters.** The yaw inertia from the part's mass shape decides whether the side grip settles or rocks the car: KSACars'
  Cadillac uses a cuboid mass because a sphere's (about a third of a car's) made the grip overcorrect each step and the car rock.
- **The static shares assume two axles.** Corners split into steered (front) and unsteered (rear); a car with neither falls back to
  equal shares, and `AxlePartner` pairs wheels by matching hub Y within 5 cm.
- **The "engine" is a curve.** Drive force is a launch acceleration fading linearly to top speed; RPM and gears are computed
  afterwards for sound only and never limit torque.
- **Side grip is a fraction of slip, not a slip-angle curve.** It depends on `dt`; very long or short steps change the effective
  stiffness, which is why the springs are left out above 0.1 s.
- **Unverified in game since the repository split.** KSACars' own notes say behaviour changes are unverified until seen in game;
  treat the tuned numbers as starting points.

## See also

- [Overview](overview.md)
- [Sample terrain and pad surfaces under each wheel](../sample-the-ground-under-each-wheel/overview.md)
- [Inject forces and impulses at the physics-safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)
- Mod features: [Wheel, suspension and tyre model](../../../../mods/ksacars/features/wheel-suspension-tyre-model.md), [Terrain contact under each wheel](../../../../mods/ksacars/features/terrain-contact-under-wheels.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers the height query

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksacars-physicshook]: KSACars: src/KSACars/Ksa/PhysicsHook.cs
[^ksacars-buggies]: KSACars: src/KSACars/Ksa/Buggies.cs
[^ksacars-drivetests]: KSACars: tests/KSACars.Tests/BuggyDriveTests.cs
[^ksacars-buggydrive]: KSACars: src/KSACars/Sim/BuggyDrive.cs
