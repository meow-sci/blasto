---
type: How-To
title: Share one driver seam between the game and the headless rig and test with varying steps
description: Advance mod-simulated rounds through a single function that both the game and the tests call, so a failing test is a failing game, and vary the frame step so epoch and phase errors cannot hide behind a constant dt
category: testing-and-tooling
group: offline-tests
topic: run-mod-simulation-in-a-headless-rig
approach: 2
difficulty: advanced
prerequisites: [xunit, sim-boundary]
demonstrated_by:
  - mods/ksarmory/features/sim-boundary-headless-tests
  - mods/ksarmory/features/mod-simulated-rounds
  - mods/ksarmory/features/consume-applied-sim-step
techniques: [sim-boundary, headless-tests, step-variation]
hooks: []
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [Brutal.Numerics.double3, KSA.SimStep, KSA.Universe]
tags: [testing, rig, driver-seam, epoch, phase, step-variation, ballistics]
---

Outcome: whole engagements and ballistic shots are flown in an ordinary test, through the same function the game calls, at the frame steps the engine really produces. A test that fails there is a failure in the game, and a phase error that a constant step would hide changes the answer.
Demonstrated by KSArmory ([src/KSArmory/Sim/RoundDriver.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundDriver.cs#L64-L82)). Prerequisites: a `Sim/` boundary as in [Unit-test mod logic without launching the game](../unit-test-mod-logic-without-game/overview.md), and a mod that simulates its own objects.

## Background: how the game does it

The engine applies a simulation step to the world each frame and reports it afterwards: `Universe.GetLastSimStep()` returns the step just finished ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2384)), assigned from the next step once the vehicle results are applied on the main thread ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1948-L1950)). Its fields are `PreviousTime`, `NextTime` and `DeltaTime` ([current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs)). That is why the step varies: it follows the display's frame pacing, and it changes by a factor of 4 or 100 when the player changes simulation speed.
A mod that integrates its own objects across that step has to take each step exactly once and close the gap for frames it missed (see [Set the simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md) for the engine side of speed changes).

## Lifecycle and timing

In the game the mod integrates in a per-frame hook after the engine's step has been applied (`frame:after-on-frame`, with drawing work in `frame:draw-ui`); see the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for why the sample the mod sees is the end-of-step one. The rig reproduces that phase: the platform sample for frame k has advanced by exactly the step frame k's update will be given.

## Steps

1. **Put the frame's advance in one function that both sides call.** KSArmory's `RoundDriver.Fly` is "the one place a round is advanced by one frame". The game and the headless rig both come through it, so neither can hold its own opinion about which fields a round re-reads within a frame. A second implementation of the loop agrees with the first only while somebody keeps the two in step by hand:

   ```csharp
   // src/KSArmory/Sim/RoundDriver.cs L64-L82

   /// <summary>
   /// The one place a round is advanced by one frame.
   ///
   /// <para><b>A seam rather than tidiness.</b> The game and the headless rig both come through here,
   /// so neither can hold an opinion of its own about which fields a round re-reads within a frame. A
   /// second implementation of this loop agrees with the first only while somebody keeps the two in
   /// step by hand, and a rig that has drifted still flies — it just prices every budget against a
   /// round nothing flew.</para>
   ///
   /// <para>So a flight's configuration is <em>what the caller supplies</em>, never a flag something
   /// else interprets. The round the game flies is spelled by passing the game's lookups, which is
   /// the only way to spell it and not a thing anyone can arrive at by accident.</para>
   ///
   /// <para>What deliberately stays with the caller is everything KSA-shaped — the contact candidates
   /// and the hull test — because <c>Sim/</c> cannot name those types and the rig has no equivalent of
   /// them.</para>
   /// </summary>
   internal static class RoundDriver
   ```

2. **Pass the environment as lookups the caller supplies**, never as a flag the driver interprets. The game passes its lookups; the rig passes its own; the signature is the same:

   ```csharp
   // src/KSArmory/Sim/RoundDriver.cs L90-L93
       public static void Fly(IProjectile round, double dt, TargetState? target,
                              double3 gravity, double3 frameVelocityEcl, double3 platformEcl,
                              MunitionProfile munition, double mediumDensityRatio,
                              in RoundFields fields)
   ```

3. **Call it from the game with the real lookups.** The game side stays KSA-shaped (contact candidates, hull test) while the driver stays pure:

   ```csharp
   // src/KSArmory/Ksa/WeaponSystem.cs L3460-L3472
               // Through the one driver both this and the headless rig go through, so the two cannot
               // hold different opinions about which fields a round re-reads within a frame. The
               // lookups are cached rather than fresh method groups per round per frame: a cannon
               // burst is 150 shells and these are assigned to every one of them.
               RoundDriver.Fly(round, dt, Seek(round, SampleTarget(round), dt), gravity, airVelocity, PlatformEcl,
                               round.Munition, mediumDensity,
                               new RoundFields(_gravityAt ??= GravityIntoFrame,
                                               _airDensityAt ??= AirDensityIntoFrame,
                                               GroundTest.Shared,
                                               _groundDriftAt ??= GroundCentreDriftIntoFrame,
                                               _groundQueryDriftAt ??= GroundQueryDriftIntoFrame,
                                               _approachAt ??= ApproachAt,
                                               _airVelocityAt ??= AirVelocityIntoFrame));
   ```

4. **Call it from the rig with stand-in lookups.** The predictor tests fly a warhead "exactly as the round flies it", with density and gravity lookups, and never with the held-at-frame-start shortcut; the test's own remark records what the shortcut cost (13.75 m against 3.72 m on that geometry):

   ```csharp
   // tests/KSArmory.Tests/PredictorStepGapTests.cs L117-L129
           RoundFields fields = new(
               GravityAt: (point, _) => GravityAt(point),
               AirDensityAt: (point, _) => DensityAt(point),
               Ground: new Ball());

           for (int i = 0; i < (int)(3000.0 / dt) && round.State == RoundState.Flying; i++)
           {
               RoundDriver.Fly(round, dt, null, GravityAt(round.PositionEcl), Vec.Zero, Vec.Zero,
                               munition, DensityAt(round.PositionEcl), fields);
           }

           Assert.NotEqual(RoundState.Flying, round.State);
           return round.PositionEcl;
   ```

5. **Make the rig sensitive to phase by varying the step.** At a constant step a right and a wrong epoch are indistinguishable. This test fires a round from a platform carrying Earth's ecliptic motion (about 29.8 km/s), advances the platform sample before the update that uses it, and records the drawn offset each frame. The comment records that swapping those two lines makes every assertion pass against code that jumps in game:

   ```csharp
   // tests/KSArmory.Tests/OffsetPhaseTests.cs L26-L70
       // Earth-like: an enormous ecliptic position with the ~29.8 km/s of common motion that turns
       // sub-millisecond bookkeeping errors into hundreds of metres on screen.
       private static readonly double3 PlatformStart = new(1.4959e11, 0, 0);
       private static readonly double3 OrbitalVelocity = new(0, 29800, 0);

       private static MunitionProfile Munition() => Catalogue.MunitionNamed(BuiltIns.PantsirS1.Munition);

       /// <summary>
       /// Flies a round across the given steps, advancing the platform to its sample for each frame
       /// <em>before</em> the update that uses it — the phase the engine has — and returns the offset
       /// the renderer would have used on each frame.
       /// </summary>
       private static List<double3> Fly(IReadOnlyList<double> steps)
       {
           MunitionProfile munition = Munition();

           double3 platform = PlatformStart;
           double3 up = new(1, 0, 0);

           var round = new Interceptor(
               positionEcl: platform + up * 3.0,
               velocityEcl: OrbitalVelocity + up * munition.LaunchSpeed,
               target: null!,
               tube: 1,
               platformEcl: platform,
               frameVelocityEcl: OrbitalVelocity) { Munition = BuiltIns.Missile57E6 };

           var offsets = new List<double3>();

           foreach (double dt in steps)
           {
               // The platform sample for frame k, having advanced by exactly the step this frame's
               // update is about to be given. Reversing these two lines encodes the opposite phase,
               // and every assertion below then passes against code that jumps in game.
               platform += OrbitalVelocity * dt;

               round.Update(dt, target: null, gravity: double3.Zero,
                            frameVelocityEcl: OrbitalVelocity, platformEcl: platform,
                            munition: munition);

               offsets.Add(round.OffsetFromPlatform);
           }

           return offsets;
       }
   ```

6. **Assert on the steps the engine really produces.** A 16/20 ms alternation (display pacing), and a drop from 1x to 0.25x to 0.01x simulation speed, must never move the drawn offset more than 50 m in a frame, and it must stay on the launch axis:

   ```csharp
   // tests/KSArmory.Tests/OffsetPhaseTests.cs L99-L115
       [Fact]
       public void ChangingSimulationSpeedDoesNotThrowTheRoundAcrossTheSky()
       {
           // Dropping from 1x to 0.25x takes the engine's step from ~22 ms to ~5.6 ms in one frame,
           // and any form carrying a `v * dstep` term displaces the round by 29800 * 0.0169 = ~500 m
           // on that single frame -- measured at 507.37 m, which is a round jumping across the sky.
           var steps = new List<double>();
           for (int i = 0; i < 40; i++) steps.Add(0.0225);   // 1x
           for (int i = 0; i < 40; i++) steps.Add(0.0056);   // 0.25x
           for (int i = 0; i < 40; i++) steps.Add(0.0002);   // 0.01x
           for (int i = 0; i < 40; i++) steps.Add(0.0225);   // and back

           double worst = LargestStep(Fly(steps));

           Assert.True(worst < 50.0,
               $"changing the simulation speed moved the drawn offset {worst:F0} m in one frame");
       }
   ```

7. **Price a term instead of asserting it** where a fix would not be worth its risk: `BodySampleInvarianceTests` measures what a known epoch offset of the body sample is worth over a flown arc and records why a correction was not applied. And pin collapse-prone refactors: `DrawAnchorTests` fails if the two instants `DrawAnchor` samples are merged into one, a tidy-up that would move the overlay 500 m.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/RoundDriver.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundDriver.cs) | The shared driver |
| [src/KSArmory/Sim/StepGate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/StepGate.cs) | Takes each simulation step once and closes missed frames |
| [tests/KSArmory.Tests/OffsetPhaseTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/OffsetPhaseTests.cs) | Step-varying epoch tests |
| [tests/KSArmory.Tests/PredictorStepGapTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/PredictorStepGapTests.cs) | Rig flights through the driver |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3460-L3470) | The game-side call of the same driver |

## Variations and gotchas

- **A rig that has drifted still flies.** It "just prices every budget against a round nothing flew"; the single driver is how this mod avoids that, so resist adding a rig-only branch.
- **Held versus re-read.** Passing the frame's first sample instead of a lookup makes the rig price its own shortcut; the dedicated `Held` value exists to make that choice explicit.
- **Time scale.** Include warp changes in the step list; the 0.01x segment (0.2 ms steps) is what exposes terms that scale with the step.
- **Game updates.** If the engine's order of "apply results, then publish the step" changes, the `Ksa/` adapter is the only code to review; the tests still describe the phase the mod needs.

## See also

- [Overview](overview.md); [approach 1](run-mod-simulation-in-a-headless-rig-1.md), [approach 3](run-mod-simulation-in-a-headless-rig-3.md)
- [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md), [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- Mod features: [KSArmory sim-boundary headless tests](../../../../mods/ksarmory/features/sim-boundary-headless-tests.md), [Mod-simulated rounds](../../../../mods/ksarmory/features/mod-simulated-rounds.md), [Consume the applied sim step](../../../../mods/ksarmory/features/consume-applied-sim-step.md)
