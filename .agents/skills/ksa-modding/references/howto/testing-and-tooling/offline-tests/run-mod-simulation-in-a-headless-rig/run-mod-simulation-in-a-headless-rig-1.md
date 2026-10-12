---
type: How-To
title: Fly a rigid-body rig through the mod's own drive step with the engine's mass and inertia
description: Test a custom vehicle model by integrating a minimal rigid body that uses the mass, inertia and centre of mass KSA derives from the part XML, then assert settling, steering and braking behaviour
category: testing-and-tooling
group: offline-tests
topic: run-mod-simulation-in-a-headless-rig
approach: 1
difficulty: advanced
prerequisites: [xunit, sim-boundary]
demonstrated_by: [mods/ksacars/features/headless-sim-tests]
techniques: [headless-tests, rigid-body-rig]
hooks: []
lifecycle: [frame:after-on-frame]
game_types: [Brutal.Numerics.double3, KSA.SimStep]
tags: [testing, rig, rigid-body, vehicle-model, suspension, headless]
---

Outcome: a test class that drops, drives, steers and brakes the mod's car model on a flat plane with no game, and fails when a change makes the car bounce forever, roll when it turns or lose traction. The rig supplies exactly what the engine would: mass, inertia, centre of mass, gravity, ground contact and a per-frame step.
Demonstrated by KSACars ([tests/KSACars.Tests/BuggyDriveTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BuggyDriveTests.cs#L222-L284)). Prerequisites: a mod whose drive model is a pure function of contacts and inputs (see [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md)).

## Background: how the game does it

In the game, KSA's vehicle solver owns the body: the mod contributes an impulse each step and the engine integrates it with the mass and inertia it derives from the part data. The rig must therefore reproduce those inputs rather than invent them. KSACars copies them from its game-data XML: a solid sphere of radius 1.1 m for the buggy, a solid cuboid 1.0 m tall, 5.6 m long and 2.0 m wide for the Eldorado, with the centres of mass the XML gives. The step the rig imitates is what `Universe.GetLastSimStep()` reports each frame ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384)), and the order is the engine's: the impulse is applied at the start of the frame, then gravity and motion over it.

## Lifecycle and timing

Test time only; the code under test runs in the mod's per-frame step in the game ([frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). The rig's integration order mirrors what the engine does after the mod's contribution: a drive impulse first, then gravity and position. A step-length pattern can be supplied to mimic uneven frames, for example the alternating step KSA produces on a 120 Hz display at 60 fps.

## Steps

1. **Write the rig as a nested class of the test file.** It is a rigid body (position, velocity, attitude, spin) with the engine's mass and inertia and an optional step pattern:

   ```csharp
   // tests/KSACars.Tests/BuggyDriveTests.cs L222-L253
       // A rigid box on flat ground, +X up, integrated the way the engine does: the drive's impulse at the
       // start of the frame, then gravity and the motion over it.
       internal sealed class Rig(double dt, BuggyProfile? profile = null, double3? com = null)
       {
           private readonly BuggyProfile _p = profile ?? P;
           private readonly double3 _com = com ?? Com;
           // Set to alternate the step the way KSA's does on a 120 Hz display at 60 fps.
           public double[]? Pattern;
           private int _tick;
           private double Dt => Pattern is { } p ? p[_tick++ % p.Length] : dt;

           public BuggyDrive Drive { get; } = new(profile ?? P);
           // The centre of mass, which rests Com.X over the ground.
           public double3 Position = com ?? Com;
           public double3 Velocity = Vec.Zero;
           public doubleQuat Attitude = doubleQuat.Identity;
           public double3 Spin = Vec.Zero;

           // What the engine gives each car, from KSACarsGameData.xml: the buggy's solid sphere, 1.1 m in
           // radius, and the Eldorado's solid cuboid, 1.0 m tall, 5.6 m long and 2.0 m wide.
           public double RigMass = Mass;
           public double Gravity = G;
           private double3 Inertia => RigMass > 1000
               ? new double3((5.6 * 5.6) + (2.0 * 2.0), (1.0 * 1.0) + (2.0 * 2.0), (1.0 * 1.0) + (5.6 * 5.6)) * (RigMass / 12.0)
               : new double3(1, 1, 1) * (0.4 * RigMass * 1.1 * 1.1);

           // Where KSACarsGameData.xml puts the centre of mass.
           public static readonly double3 Com = new(0.60, -0.25, 0.0);
           public static readonly double3 EldoradoCom = new(0.55, 0.25, 0.0);
           private double3[] Hubs => _hubs ??= _p.Corners.Select(c => c.Hub - _com).ToArray();
           private double3[]? _hubs;
   ```

2. **Step it the way the engine does.** Build one contact per wheel against flat ground from the current pose, call the mod's pure `BuggyDrive.Step`, apply its linear and angular impulse, then add gravity and integrate:

   ```csharp
   // tests/KSACars.Tests/BuggyDriveTests.cs L259-L283
           public void Step(DriveInput input)
           {
               double dt = Dt;
               doubleQuat toBody = doubleQuat.Conjugate(Attitude);
               double3 comWorld = Position;
               double3 upBody = toBody * new double3(1, 0, 0);
               double3 velBody = toBody * Velocity;

               WheelContact[] contacts = new WheelContact[Hubs.Length];
               for (int i = 0; i < Hubs.Length; i++)
               {
                   double3 world = comWorld + (Attitude * Hubs[i]);
                   contacts[i] = new WheelContact(true, world.X, upBody, velBody + Vec.Cross(Spin, Hubs[i]));
               }

               DriveImpulse j = Drive.Step(input, contacts, Hubs, new double3(1, 0, 0), new double3(0, 1, 0),
                                           RigMass, Gravity, 1.225, dt);
               Velocity += Attitude * (j.Linear / RigMass);
               Spin += new double3(j.Angular.X / Inertia.X, j.Angular.Y / Inertia.Y, j.Angular.Z / Inertia.Z);

               Velocity += new double3(-Gravity * dt, 0, 0);
               Position += Velocity * dt;
               double rate = Vec.Len(Spin);
               if (rate > 0.0) Attitude *= doubleQuat.CreateFromAxisAngle(Spin / rate, rate * dt);
           }
   ```

3. **Settle before you assert.** A helper steps the rig for six simulated seconds with no input so every test starts from rest on the springs:

   ```csharp
   // tests/KSACars.Tests/BuggyDriveTests.cs L254-L257
           public void Settle()
           {
               for (int i = 0; i < (int)(6.0 / dt); i++) Step(default);
           }
   ```

4. **Assert behaviour at more than one step size.** Theories run the same scenario at 1/60 s and 1/30 s so a dependence on the frame length shows up:

   ```csharp
   // tests/KSACars.Tests/BuggyDriveTests.cs L66-L87
       [Theory]
       [InlineData(1.0 / 60.0)]
       [InlineData(1.0 / 30.0)]
       public void DroppedItBouncesThenSettlesOnItsSprings(double dt)
       {
           Rig rig = new(dt);
           rig.Position = Rig.Com + new double3(0.25, 0, 0);

           double lowest = double.PositiveInfinity, highestAfterLowest = double.NegativeInfinity;
           for (int i = 0; i < (int)(8.0 / dt); i++)
           {
               rig.Step(default);
               if (rig.Position.X < lowest) { lowest = rig.Position.X; highestAfterLowest = double.NegativeInfinity; }
               highestAfterLowest = Math.Max(highestAfterLowest, rig.Position.X);
           }

           // Springy: from the bottom of the landing it comes back up past where it will end.
           Assert.True(highestAfterLowest - lowest > 0.03, $"rebound only {highestAfterLowest - lowest:F3} m");
           Assert.True(Math.Abs(rig.Position.X - Rig.Com.X) < 0.02, $"rests {rig.Position.X - Rig.Com.X:F3} m off its rest height");
           Assert.True(Vec.Len(rig.Velocity) < 0.01, $"still moving at {Vec.Len(rig.Velocity):F3} m/s");
           Assert.True(Vec.Len(rig.Spin) < 0.01, $"still turning at {Vec.Len(rig.Spin):F3} rad/s");
       }
   ```

5. **Assert in physical terms.** Steering must turn the car without rolling it; braking must stop it within a bound:

   ```csharp
   // tests/KSACars.Tests/BuggyDriveTests.cs L104-L115
       [Fact]
       public void SteeringLeftTurnsItLeftWithoutRollingIt()
       {
           Rig rig = new(1.0 / 60.0);
           rig.Settle();
           for (int i = 0; i < 180; i++) rig.Step(new DriveInput(1.0, 0.0));
           for (int i = 0; i < 90; i++) rig.Step(new DriveInput(0.3, 1.0));

           double3 heading = rig.Attitude * new double3(0, 1, 0);
           Assert.True(heading.Z > 0.2, $"heading {heading}");
           Assert.True((rig.Attitude * new double3(1, 0, 0)).X > 0.9, "rolled over");
       }
   ```

6. **Encode the engine's quirks as tests.** The engine never hands a parked car a yaw of exactly zero, and a side grip that removes more yaw in a step than there was flips it every step instead. The test kicks a settled car and demands the spin die away, on both profiles and at two step sizes:

   ```csharp
   // tests/KSACars.Tests/BuggyDriveTests.cs L139-L151
       // The engine never hands a parked car a yaw of exactly zero. A side grip that removes more yaw in a
       // step than there was flips it every step instead, and the slip that rocking puts under the front
       // hubs spends the friction circle the drive needed: in game, 1 m/s^2 where 4 was asked for.
       [Theory]
       [InlineData(1.0 / 60.0)]
       [InlineData(1.0 / 40.0)]
       public void AYawKickDiesAwayRatherThanRockingTheDriveAway(double dt)
       {
           Rig buggy = new(dt);
           buggy.Settle();
           buggy.Spin += new double3(0.2, 0, 0);
           for (int i = 0; i < (int)(3.0 / dt); i++) buggy.Step(new DriveInput(1.0, 0.0));
           Assert.True(Math.Abs(buggy.Spin.X) < 0.01, $"the buggy still yaws at {buggy.Spin.X * 180 / Math.PI:F1} deg/s");
   ```

   The rig also uses different inertia for the two cars, so a model tuned only against the sphere cannot hide on the cuboid.

## Complete example

| File | Role |
|---|---|
| [tests/KSACars.Tests/BuggyDriveTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/BuggyDriveTests.cs#L222-L284) | The rigid-body rig |
| [tests/KSACars.Tests/KSACars.Tests.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/KSACars.Tests.csproj) | Links `Sim/**`, references no KSA assembly |
| [tools/check-boundary.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-boundary.sh) | Boundary check for CI without the game |

## Variations and gotchas

- **The rig is a model of the engine.** Its mass, inertia and centres of mass are copied from XML by hand; when the part XML changes, the rig must change with it, or the tests price a car nothing flies. Approach 2 solves this by sharing the stepping code instead of mirroring it.
- **Flat ground only.** Terrain contact, ray queries and pad surfaces have their own test classes (`TerrainRayTests`, `PadSurfaceTests`); the rig supplies contacts directly.
- **Passing here is necessary, not sufficient.** KSACars keeps a checklist of what has and has not been driven in game.
- **Game updates.** If the engine changes how it integrates impulses or derives inertia, the rig will not notice; use a [HeadlessHarness](../headless-test-harness-for-game-mods/overview.md) test as an oracle for the stock side.

## See also

- [Overview](overview.md); [approach 2](run-mod-simulation-in-a-headless-rig-2.md), [approach 3](run-mod-simulation-in-a-headless-rig-3.md)
- [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
- Mod feature: [KSACars headless sim tests](../../../../mods/ksacars/features/headless-sim-tests.md)
