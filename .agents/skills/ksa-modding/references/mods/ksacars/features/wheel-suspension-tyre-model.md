---
type: Mod Feature
title: Wheel, suspension and tyre model
description: A per-wheel spring-damper with anti-roll bars, a friction-circle tyre, a speed-limited steering lock, a linear drive-force falloff and a cosmetic gearbox, stepped once a frame as one impulse for the whole car
mod: ksacars
category: physics
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle]
tags: [physics, wheels, suspension, tyres, friction-circle, spring-damper, steering, gearbox, rpm]
---

KSA has no wheel, tyre or suspension module, so a KSACars car is a single part whose colliders
stand clear of the ground, held up by springs the mod simulates.[^ksacars-blocked]
`Sim/BuggyDrive.cs` is the whole model: for each wheel it measures how far the ground is below the
hub, computes a spring-damper load and a tyre force inside a friction circle, and sums everything
into one linear and one angular impulse in the car's body frame.[^ksacars-buggydrive] The impulse is
written into the engine's state by the [PrepareWorker prefix](./prepareworker-impulse-injection.md).

Every car is data: `Sim/BuggyProfile.cs` holds the hub positions, arm pivots, coil-over eyes,
spring rates, grip and engine numbers for the beach buggy (`Manx`) and the Cadillac `Eldorado`, and
nothing in the model names a specific car.[^ksacars-buggyprofile]

## How it works

Inputs per step: `DriveInput(Throttle, Steer)` in -1..1, a `WheelContact` per corner (hub height
above the ground along the ground normal, the ground's up and the hub's velocity over the ground,
all in the body frame), the hub positions relative to the centre of mass, the car's up and forward,
mass, gravity, air density and `dt`.

**Suspension (per wheel)**

| Term | Formula in `Step` |
|---|---|
| Static share `m` | `mass * share[i]`, the wheel's share of weight from where the centre of mass sits between the axles (`StaticShares`, clamped 10-90 % front) |
| Stiffness and damping | `omega = 2 pi SpringHz`, `k = m omega^2`, `c = 2 DampingRatio m omega` |
| Compression | `Radius - HubHeight / tilt`, measured along the car's up (`tilt = up . groundUp`, contact ignored below 0.2) |
| Load | `m g + k * compression + c * closing` (preloaded with the wheel's weight, so rest is zero compression) |
| Anti-roll bar | `+ AntiRoll * k * (travel - partnerTravel)` across the axle |
| Roll damper | `+ RollDamping * m * omega * (closing - partnerClosing)`, damps roll and leaves bounce alone |
| Bump stop | `+ BumpStopFactor * k * (compression - BumpTravel)` past full bump |
| Clamp | `0 .. 4 m g` (`MaxLoadFactor`), so a car spawned with its wheels in the ground is not thrown |
| Droop | below `-DroopTravel` the wheel is airborne and eases to full droop at 1.5 m/s |

**Tyre (per wheel)**

- The wheel heading is the car's forward, turned by `SteerAngle` about the car's up for steered
  wheels, projected onto the ground plane to give `along` and `side`.
- Side force asks to cancel `Settle = 0.7` of the wheel's sideways speed within the step
  (`-vSide * m * 0.7 / dt`). Cancelling less than all of it is what keeps four wheels from
  overshooting through the yaw inertia.
- Friction circle of radius `grip * load` (`FrontGrip` or `RearGrip`): side force is clamped
  first, and drive or brake gets only `sqrt(limit^2 - side^2)`, so a tyre cornering hard gives up
  drive before it gives up its line.
- The side force also adds a moment as if it acted at `RollCentreHeight` above the ground, which
  sets how much the body leans in a corner.

**Longitudinal force**

| Case | Force |
|---|---|
| Throttle against the direction of travel | brake on all four: `min(BrakeG * load, abs(v) m / dt)` |
| Driven wheel with throttle | `DriveForce / drivenWheelsOnGround` |
| No throttle, under 1 m/s | cancel 0.7 of the rolling speed, so it parks on a slope |
| Otherwise | rolling resistance `RollingResistance * load` |

`DriveForce` is the "engine": `throttle * LaunchAccelG * g * mass * clamp(1 - v / TopSpeed)`, a
straight line from full pull at rest to nothing at top speed; reverse is 0.6 of that and fades at
`ReverseTopSpeed`. There is no torque curve and no driveline inertia.

**Steering**: the lock is the angle the front tyres can just hold at the current speed,
`atan(wheelbase * FrontGrip * g * SteerOverGrip / v^2)`, never below 3 degrees and never above
`MaxSteerDeg`, approached at `SteerRateDegPerSec`. That stops a full-lock flick at speed from
scrubbing the car to a standstill.

**Air**: `-v * 0.5 * rho * DragAreaM2 * abs(v)` at the centre of mass, using the first contact's hub
velocity; it only caps top speed. `rho` comes from the body's atmosphere, so a car on an airless
body has no drag.[^ksacars-buggies]

**Gearbox and RPM (sound only)**: `UpdateEngine` picks a gear from `GearTopSpeeds` (shifting at
95 % of each), interpolates RPM between `IdleRpm` and `RedlineRpm` within the gear, free-revs when
no driven wheel is on the ground, and slews RPM at 5000 rpm/s up and 3500 rpm/s down. `Rpm`, `Gear`
and `Load` drive the [engine sound](./engine-sound-crossfade.md); they feed nothing back into the
forces. `BuggyProfile` calls it "a gearbox nobody sees".[^ksacars-buggyprofile]

**Wheel spin and poses**: each wheel accumulates a roll angle from its along-speed, and
`ArmAngle`, `OnArm`, `CoilPose` and `WheelRotation` turn travel and steer into poses for the arm,
hub, coil-over (rotated and squashed along its own X) and wheel subparts, drawn by
[pose subparts at runtime](./pose-subparts-at-runtime.md).

**Tuned profiles** (values from `BuggyProfile.cs`, defaults where the car does not override):

| Field | Beach buggy | Eldorado |
|---|---|---|
| Mass (from GameData XML) | 650 kg `SolidSphereMass` | 2336 kg `SolidCuboidMass` 1.0 x 5.6 x 2.0 m |
| SpringHz / DampingRatio | 1.6 / 0.3 | 1.05 / 0.26 |
| Bump / droop travel | 0.14 / 0.14 m | 0.12 / 0.12 m |
| AntiRoll / RollDamping / RollCentreHeight | 0.35 / 0 / 0 | 0.1 / 0.5 / 0.2 m |
| Front / rear grip | 1.0 / 1.1 | 0.95 / 1.0 |
| Drive | rear wheels, 0.55 g, top 28 m/s | front wheels, 0.4 g, top 55 m/s |
| Steering | 32 deg lock, 140 deg/s, ratio 3 | 35 deg lock, 110 deg/s, ratio 6 |
| Idle / redline RPM, gears | 900 / 4800, 4 gears | 650 / 4400, 3 gears |

The Eldorado uses a cuboid mass because yaw inertia is what lets the side grip settle: with a
sphere's (about a third of a car's) the grip overcorrected each step and the car rocked at
13 deg/s; `BuggyDriveTests.AYawKickDiesAwayRatherThanRockingTheDriveAway` fails against the
sphere.[^ksacars-gamedata][^ksacars-claude]

## Lifecycle and timing

`BuggyDrive.Step` is pure C# with no KSA types (it lives in `Sim/`), called once per frame from
`Buggies.Step` inside the [`Vehicle.PrepareWorker` prefix](./prepareworker-impulse-injection.md),
on the main thread, between `Universe.ApplyVehicleSolvers` and the vehicle solver job being
queued (`frame:dispatch-solvers`). The engine then integrates the whole step with that velocity and
spin change already applied; the model never runs per physics substep. `dt` is the step the
worker is about to integrate, which is the frame time times the simulation speed, so:

- Under time warp the step grows and an explicit spring overshoots. `Buggies.Step` skips the drive
  entirely when `dt > 0.1` s and the car rests on its colliders.
- The tests run the model at several fixed `dt` values (for example `DroppedItBouncesThenSettlesOnItsSprings`
  is a `[Theory]` over step lengths) to prove it stays stable at real frame rates.[^ksacars-drivetests]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Sim/BuggyDrive.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyDrive.cs) | The model: suspension, tyre, steering, drive, air, RPM, pose helpers |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs) | `BuggyCorner`, `BuggyProfile` records and the two tuned cars; `All` lists every car the mod drives |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L647-L687) | Builds `WheelContact`s, calls `Drive.Step` with gravity and air density, records diagnostics |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L57-L66) | Mass and centre of mass the model's static shares and inertia come from |
| [tests/KSACars.Tests/BuggyDriveTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BuggyDriveTests.cs) | Headless rig: settle, pull away, turn, brake, yaw kick, full-lock flick, steering reversal |

## Snippets

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

```csharp
// src/KSACars/Sim/BuggyDrive.cs L305-L324
    private void UpdateEngine(double throttle, bool driven, double dt)
    {
        double speed = Math.Abs(ForwardSpeed);
        double[] tops = Profile.GearTopSpeeds;
        int gear = 0;
        while (gear < tops.Length - 1 && speed > tops[gear] * 0.95) gear++;
        Gear = gear;

        double lower = gear == 0 ? 0.0 : tops[gear - 1] * 0.95;
        double fraction = Math.Clamp((speed - (lower * 0.6)) / (tops[gear] - (lower * 0.6)), 0.0, 1.0);
        double fromWheels = Profile.IdleRpm + ((Profile.RedlineRpm - Profile.IdleRpm) * fraction);

        // Revved in the air or with the wheels spinning: the engine runs free of the road.
        double free = Profile.IdleRpm + ((Profile.RedlineRpm - Profile.IdleRpm) * 0.8 * Math.Abs(throttle));
        double wanted = driven ? Math.Max(fromWheels, Profile.IdleRpm + (Math.Abs(throttle) * 600.0)) : free;

        double rate = wanted > Rpm ? 5000.0 : 3500.0;
        Rpm = MoveToward(Rpm, wanted, rate * dt);
        Load = MoveToward(Load, Math.Abs(throttle), 4.0 * dt);
    }
```

## Game APIs touched

None directly: `BuggyDrive` and `BuggyProfile` live in `Sim/`, which by the repo's rule references
no KSA type (enforced by `tools/check-boundary.sh` and by the test project not referencing
`KSA.dll`). The KSA-facing inputs (mass, inertia, gravity from `IParentBody.Mu`, air density from
`AtmosphereReference`) are gathered in `Buggies.Step` and `KsaWorld`; see
[impulse injection](./prepareworker-impulse-injection.md) and
[terrain contact](./terrain-contact-under-wheels.md).

## Pitfalls and version notes

- **Colliders must not touch the ground at rest.** KSA's terrain contact uses one friction value
  for every collider, so a wheel-shaped collider would drag like a skid. Each car's wheel pads end
  18-25 cm above the ground at rest and only touch in a crash; at 10-16 cm the outside pad touched
  in a hard turn and tripped the car.[^ksacars-gamedata]
- **The static shares assume two axles.** Corners are split into steered (front) and unsteered
  (rear); a car with no steered or no unsteered corners falls back to equal shares, and
  `AxlePartner` pairs wheels by matching hub Y within 5 cm.
- **The drive force is not an engine model.** It is a launch acceleration fading linearly to top
  speed. RPM and gears are computed afterwards for sound and never limit torque.
- **Side grip per step is a fraction of the slip, not a slip-angle curve.** It depends on `dt`;
  very long or very short steps change the effective stiffness.
- **Inertia matters.** The yaw inertia from the part's mass shape (`SolidCuboidMass` vs
  `SolidSphereMass`) decides whether the side grip settles or rocks the car.
- The model has been driven through the mod's bridge before the repository split; CLAUDE.md says
  nothing has been seen in game since the split, and behaviour changes are "unverified until seen
  in game".[^ksacars-claude]

## See also

- [Inject wheel forces from a Vehicle.PrepareWorker prefix](./prepareworker-impulse-injection.md)
- [Terrain contact under each wheel](./terrain-contact-under-wheels.md)
- [Launch pad surface contact](./launch-pad-surface-contact.md)
- [Pose subparts at runtime](./pose-subparts-at-runtime.md)
- [Engine sound crossfade](./engine-sound-crossfade.md)
- [Car part game data](./car-part-gamedata.md)
- [Headless simulation tests](./headless-sim-tests.md)
- [Raycast-style suspension impulses in the PrepareWorker prefix](../../../howto/physics/aero-and-ground/simulate-wheels-and-suspension-without-a-wheel-module/simulate-wheels-and-suspension-without-a-wheel-module.md) - how-to article demonstrating this feature

[^ksacars-buggydrive]: src/KSACars/Sim/BuggyDrive.cs
[^ksacars-buggyprofile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-gamedata]: src/KSACars/KSACarsGameData.xml
[^ksacars-drivetests]: tests/KSACars.Tests/BuggyDriveTests.cs
[^ksacars-claude]: CLAUDE.md
[^ksacars-blocked]: docs/BLOCKED-ON-KSA.md
