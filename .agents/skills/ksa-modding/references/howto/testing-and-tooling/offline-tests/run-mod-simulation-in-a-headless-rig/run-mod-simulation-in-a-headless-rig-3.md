---
type: How-To
title: Test ball and contact physics with an epoch bookkeeping layer and missed-frame invariance
description: Run a body-fixed ball and a swept contact rule on a planet-sized sphere in plain tests, then prove the frame latch, the step gate and the draw anchor survive skipped frames and varying steps
category: testing-and-tooling
group: offline-tests
topic: run-mod-simulation-in-a-headless-rig
approach: 3
difficulty: advanced
prerequisites: [xunit, sim-boundary]
demonstrated_by:
  - mods/ksagolf/features/headless-sim-tests
  - mods/ksagolf/features/swept-hull-contact
  - mods/ksagolf/features/dual-hook-frame-stepping
techniques: [headless-tests, invariance-tests]
hooks: []
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [Brutal.Numerics.double3, KSA.SimStep, KSA.Universe]
tags: [testing, rig, ball-physics, contact, skipped-frames, epoch, latch]
---

Outcome: a golf ball is dropped, bounced, rolled and parked on a sphere with Earth's radius and on tilted planes, a fast body is shown to be swept rather than sampled, and the bookkeeping that decides how much time each frame integrates is proved correct across missed frames, uneven steps and a hidden UI, all in plain tests.
Demonstrated by KSAGolf ([tests/KSAGolf.Tests/GolfBallTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/GolfBallTests.cs#L10-L53)). Prerequisites: a `Sim/` boundary as in [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md).

## Background: how the game does it

The engine reports the step it just applied (`Universe.GetLastSimStep()`, [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384)) and the mod is called from per-frame hooks that the engine does not treat alike. `Program.OnFrame` wraps the UI pass in an `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2270)), and the UI toggle key flips it ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1802)), so a simulation living only in the GUI hook stops dead while the UI is hidden and the world does not. KSA's screenshot capture also sets `Program.DrawUI = false` for its warm-up frames ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L231)), so a hook can be absent for a frame while the world keeps stepping. See [Dual-hook frame stepping](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md) for the mod side.

## Lifecycle and timing

Test time only. The code under test is stepped from the GUI hook (`frame:draw-ui`) and from an ungated frame postfix (`frame:after-on-frame`); the tests drive the same classes frame by frame, including frames in which one hook never runs. Frame order: [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the body free of large coordinates.** `GolfBall` is simulated in the body-fixed frame of the world it lies on, so it carries none of the planet's orbit or spin and its position is epoch-free; contact maths uses differences only, so no game type and no 1e11 m coordinate is needed. The ground is an interface (`IBodyGround`) the test implements:

   ```csharp
   // tests/KSAGolf.Tests/GolfBallTests.cs L12-L41
       private const double R = 6_371_000.0;
       private const double G = 9.81;
       private const double Mu = G * R * R;

       /// <summary>A sphere, optionally tilted: the surface rises by <c>slope</c> per metre along <c>uphill</c>.</summary>
       private sealed class Ground(double3 uphill, double slope = 0.0) : IBodyGround
       {
           public bool TrySurfaceRadius(double3 dirCcf, out double radius)
           {
               radius = R + (slope * R * Vec.Dot(dirCcf, uphill));
               return true;
           }
       }

       private static readonly double3 Up = new(0, 0, 1);
       private static readonly double3 East = new(1, 0, 0);

       private static GolfBall OnSurface(double3 up)
       {
           var ball = new GolfBall();
           ball.Place(up * (R + GolfBall.RadiusM));
           return ball;
       }

       private static void Run(GolfBall ball, IBodyGround ground, double seconds)
       {
           for (double t = 0; t < seconds && !ball.Resting; t += 1.0 / 60.0) ball.Step(1.0 / 60.0, ground, Mu, 1.0);
       }

       private static double Clearance(GolfBall ball) => Vec.Len(ball.PositionCcf) - R - GolfBall.RadiusM;
   ```

2. **Assert the physical behaviour a player would notice.** The ball settles, bounces lower than it fell by about the restitution squared, rolls as far as its resistance allows, runs down a slope and stays on a gentle one:

   ```csharp
   // tests/KSAGolf.Tests/GolfBallTests.cs L43-L73
       [Fact]
       public void ADroppedBallSettlesOnTheGround()
       {
           var ball = new GolfBall();
           ball.Place(Up * (R + 0.5));

           Run(ball, new Ground(East), 10.0);

           Assert.True(ball.Resting, "a dropped ball never came to rest");
           Assert.True(Math.Abs(Clearance(ball)) < 0.002, $"it rests {Clearance(ball) * 1000:F1} mm off the ground");
       }

       [Fact]
       public void ADroppedBallBouncesLowerThanItFell()
       {
           var ball = new GolfBall();
           ball.Place(Up * (R + GolfBall.RadiusM + 1.0));
           var ground = new Ground(East);

           bool landed = false;
           double highest = 0.0;
           for (int i = 0; i < 180; i++)
           {
               ball.Step(1.0 / 60.0, ground, Mu, 1.0);
               if (Vec.Dot(ball.VelocityCcf, Up) > 0.0) landed = true;
               if (landed) highest = Math.Max(highest, Clearance(ball));
           }

           // e² of the drop, give or take the step it lands in.
           Assert.InRange(highest, 0.5 * GolfBall.Restitution * GolfBall.Restitution, 2.0 * GolfBall.Restitution * GolfBall.Restitution);
       }
   ```

3. **Prove the contact rule sweeps instead of sampling.** A fast body crosses metres between samples, so a distance measured at both ends of a step says nothing about the middle:

   ```csharp
   // tests/KSAGolf.Tests/ContactSweepTests.cs L12-L37
       /// <summary>
       /// A fast body crosses metres between samples, so a distance measured at both ends of a step
       /// says nothing about what happened in the middle.
       /// </summary>
       [Fact]
       public void AStepIsNotSteppedOver()
       {
           // Ends 500 m short and 500 m past, and dead centre in between.
           double3 separation = new(500, 0, 0);
           double3 closing = new(-1000, 0, 0);

           Assert.True(Vec.Len(separation) > 5.0);
           Assert.True(ContactSweep.TryContact(separation, closing, 1.0, 5.0,
                                               out double when, out double miss));
           Assert.Equal(0.5, when, 6);
           Assert.Equal(0.0, miss, 6);
       }

       /// <summary>Passing wide is not a contact, however briefly it was the nearest thing.</summary>
       [Fact]
       public void PassingWideIsAMiss()
       {
           Assert.False(ContactSweep.TryContact(new double3(500, 40, 0), new double3(-1000, 0, 0),
                                                1.0, 5.0, out _, out double miss));
           Assert.Equal(40.0, miss, 6);
       }
   ```

4. **Prove the two-hook latch always releases.** Claiming twice costs one duplicated frame; a latch that is never released stops the mod for the rest of the session with nothing that looks like an error. The test claims twice, ends the frame and demands a fresh claim succeed:

   ```csharp
   // tests/KSAGolf.Tests/FrameLatchTests.cs L35-L53
       /// <summary>
       /// The failure worth a test of its own. Claiming twice costs one duplicated frame; a latch that
       /// is never released stops the mod for the rest of the session — nothing stepped, nothing
       /// driven — and nothing about that looks like an error from outside.
       /// </summary>
       [Fact]
       public void EndingTheFrameAlwaysReleasesItHoweverManyTimesItWasClaimed()
       {
           FrameLatch latch = new();

           latch.Claim();
           latch.Claim();
           Assert.True(latch.Claimed);

           latch.EndFrame();

           Assert.False(latch.Claimed);
           Assert.True(latch.Claim());
       }
   ```

5. **Prove missed frames are integrated, not lost.** Frame 2 is a capture and its hook never runs; frame 3 receives only the last step, and the gate must hand back the whole span since the last integration. The leak is stated in metres at the ecliptic speed so the test fails loudly if the effect is not large enough to matter:

   ```csharp
   // tests/KSAGolf.Tests/SkippedFrameTests.cs L19-L54
       [Fact]
       public void AMissedFrameIsIntegratedOnTheNextOne()
       {
           StepGate<Stamp> gate = new();

           // Frame 1 runs: the world stepped 22.5 ms and so do we.
           Assert.Equal(0.0225, gate.Consume(At(0.0225), 0.0225, Span), 9);

           // Frame 2 is the capture -- the hook never runs, so nothing is consumed.

           // Frame 3: the engine reports only the LAST step, 26.5 ms. The world has advanced
           // 22.5 + 26.5 = 49 ms since we integrated, and the missed 22.5 ms is what a moving body
           // must still be given.
           double taken = gate.Consume(At(0.0715), 0.0265, Span);

           Assert.Equal(0.049, taken, 9);
       }

       [Fact]
       public void TheDeficitIsTheDistanceTheBombWasThrown()
       {
           StepGate<Stamp> gate = new();
           gate.Consume(At(0.0225), 0.0225, Span);

           double reported = 0.0265;
           double taken = gate.Consume(At(0.0715), reported, Span);

           // Everything on Earth carries the planet's ecliptic motion. A body that fails to
           // integrate an interval the platform did move across puts the whole of it into the
           // drawn offset -- 656 m for a single missed frame.
           const double EclipticSpeed = 29_800.0;
           double leaked = (taken - reported) * EclipticSpeed;

           Assert.InRange(leaked, 670.0, 671.0);
           Assert.True(leaked > 500.0, "a missed frame must be worth hundreds of metres, or this test proves nothing");
       }
   ```

6. **Prove step-size invariance with the engine's own arithmetic.** The engine's clock advances by the step rounded to the nanosecond while the step is reported unrounded; a randomised run at warps that are not whole nanoseconds must integrate the elapsed time to within a nanosecond:

   ```csharp
   // tests/KSAGolf.Tests/SkippedFrameTests.cs L101-L139
       [Theory]
       [InlineData(4.72)]
       [InlineData(1.9)]
       [InlineData(10.9)]
       public void TheRoundsKeepThePlanetsClockAtAWarpThatIsNotAWholeNanosecond(double speed)
       {
           StepGate<Stamp> gate = new();
           Random frames = new(20260917);

           long clock = 1_000_000_000_000L;
           long startedAt = 0;
           double integrated = 0.0;
           double longerOfTheTwo = 0.0;

           for (int i = 0; i < 20_000; i++)
           {
               double step = (0.0167 + 0.004 * frames.NextDouble()) * speed;
               long next = clock + (long)Math.Round(step * 1e9);

               double taken = gate.Consume(new Stamp(next), step, Span);

               if (i == 0) startedAt = next;
               else
               {
                   integrated += taken;
                   longerOfTheTwo += Math.Max(step, (next - clock) / 1e9);
               }

               clock = next;
           }

           double elapsed = (clock - startedAt) / 1e9;

           // The longer of the two runs ahead by an eighth of a nanosecond a frame, which is what this
           // test exists to catch -- if the arithmetic stops producing it, nothing here is tested.
           Assert.True((longerOfTheTwo - elapsed) * 1e9 > 2_000.0,
                       $"the longer of the two should run ahead; ran {(longerOfTheTwo - elapsed) * 1e9:F1} ns");

           Assert.InRange((integrated - elapsed) * 1e9, -1.0, 1.0);
   ```

7. **Guard the invisible invariant.** The overlay anchor samples two instants (the ego position this frame, the ecliptic reference one update earlier); collapsing them into one leaves a frame of ecliptic motion (about 500 m at 60 fps near Earth) between overlay and craft. No other test can see it, so `DrawAnchorTests` pins it ([tests/KSAGolf.Tests/DrawAnchorTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/DrawAnchorTests.cs#L6-L41)).

## Complete example

| File | Role |
|---|---|
| [tests/KSAGolf.Tests/GolfBallTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/GolfBallTests.cs) | Ball on a sphere and on tilted planes |
| [tests/KSAGolf.Tests/ContactSweepTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ContactSweepTests.cs) | Swept contact against stepped-over bodies |
| [tests/KSAGolf.Tests/FrameLatchTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/FrameLatchTests.cs) | Two-hook latch |
| [tests/KSAGolf.Tests/SkippedFrameTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/SkippedFrameTests.cs) | Step gate across missed frames |
| [tests/KSAGolf.Tests/DrawAnchorTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/DrawAnchorTests.cs) | Two-instant overlay anchor |
| [src/KSAGolf/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/FrameLatch.cs) | The latch under test |

## Variations and gotchas

- **Test the failure that has no symptom.** A latch that never clears and an anchor that collapses to one instant both look fine to every other test; give each one a test of its own.
- **Say how big the error is.** `SkippedFrameTests` converts a missed frame into metres; a test that cannot show the effect is large "proves nothing".
- **Differences-only maths.** Anything that needs absolute universe time must difference it as integer nanoseconds first, because a double cannot subtract two universe times.
- **Game updates.** The latch exists because of the `DrawUI` guard in `Program.OnFrame`; if the engine changes which hooks fire when the UI is hidden, the dual-hook feature page and these tests are what to revisit.

## See also

- [Overview](overview.md); [approach 1](run-mod-simulation-in-a-headless-rig-1.md), [approach 2](run-mod-simulation-in-a-headless-rig-2.md)
- [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md)
- Mod features: [KSAGolf headless sim tests](../../../../mods/ksagolf/features/headless-sim-tests.md), [Swept hull contact](../../../../mods/ksagolf/features/swept-hull-contact.md), [Dual-hook frame stepping](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md)
