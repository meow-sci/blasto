---
type: How-To
title: Follow a burst's front against the live camera and play its bang on arrival
description: Age an expanding front on the simulated step, compare its radius with the audio camera every frame, play the sound when it arrives, and fold coincident bursts into one front
category: audio
group: playback
topic: delay-a-sound-until-the-blast-front-arrives
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/burst-sound-at-blast-arrival]
techniques: [live-front-arrival, fmod-audio]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.GameAudio
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.Camera
  - KSA.Celestial
  - KSA.Universe
tags: [audio, sound-delay, speed-of-sound, atmosphere, explosion, simulated-time, ground-anchor]
---

Outcome: a large explosion is seen at once and heard when the pressure front reaches the player: about seven seconds
from the 2.5 km a cloud is watched from, about a minute from a 50 Mt burst at 37 km overhead, and never from a burst
too high for its front to carry 50 Pa to the ear or from one on a body with no air. If the player flies toward the
burst the bang comes sooner; if the world is paused it waits; under time warp it hurries. Prerequisites: StarMap, a body
with an atmosphere, and the sound-playing half in [Play Core's explosion sound on your own schedule](../drive-sound-parameters-from-code/drive-sound-parameters-from-code-2.md).
KSArmory demonstrates it for nuclear bursts.

The physics of the front (Sedov-Taylor blast law, Sachs scaling) is KSArmory's and is not the point. The reusable
technique is the shape: **anchor the event on the ground, age a front on the simulated step, compare its radius with
the live camera, play on arrival, fold coincident events together.**

## Background: how the game does it

Nothing in the engine delays a sound. A sound is placed with `SpatialAudio` and the volume depends on distance, but a
started channel plays immediately. The pieces a mod borrows:

- **The listener.** `GameAudio.GetAudioCamera()` is the main viewport's base camera. KSArmory's `TryMainCameraPose` reads
  the same camera (`Program.MainViewport?.BaseCamera`, whose `EgoToEcl(Vec.Zero)` is its ecliptic position), the base
  camera and not `GetMainCamera()`, which answers with the map camera in map mode.[current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L65-L68)[src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4708-L4734)[^ksa-gameaudio]
- **The air.** KSA's atmosphere is isothermal, `sea level * exp(-h / H)` for pressure and for density, cut at a boundary
  height.[current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)[^ksa-physicalatmospherereference] With one temperature, the speed of sound is one number per body, `sqrt(gamma * P0 / rho0)`. The game
  supplies `P0` and `rho0`; `gamma` is the mod's (default 1.4 in its `BodyTraits`).
- **The ground.** A planet rotates and orbits, so an ecliptic point written down at the instant of the burst is left behind
  at the planet's 29.8 km/s. The place has to be stored in the body's own frame and turned back into ecliptic
  coordinates each frame with `Celestial.GetBodyFixed2Ecl()`.[current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L174-L190)[^ksa-celestial]
- **The clock.** The front ages by the simulated step the engine applied, not by real time, so it waits through a pause
  and hurries under warp. See [deduplicate GetLastSimStep](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-3.md).

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Burst | inside the mod's one-per-frame step, the weapon update calls `NuclearClouds.Begin`, which calls `BurstSound.Begin` | `frame:draw-ui` (normally) |
| Follow and play | `BurstSound.Update(_lastSimStep)` in the tail of the same step, after the world is stepped | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| Apply | `GameAudio.UpdateAudio` in `PostRender` | `frame:post-render` |

The step runs once per frame from the `[StarMapAfterGui]` postfix, from the `OnFrameCelestials` prefix when the HUD is
hidden, or from `[StarMapAfterOnFrame]` as the last resort; `_lastSimStep` is the step the engine applied, consumed once
per frame.[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L713-L727)[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L938-L947) Everything is main-thread. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Gate on air.** If the body has no atmosphere, or the front never reaches the minimum distance, there is nothing to
   carry the sound; return and make no front. This is the whole vacuum rule:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L2401-L2413
       public static bool HasAtmosphere(Celestial body)
       {
           try
           {
               return body.GetAtmosphereReference()?.Physical is { } air && air.Height > 0.0;
           }
           catch
           {
               // Unreadable reads as having one, so a burst falls back to the cloud rather than to the
               // airless effect: a cloud that does not draw is a smaller fault than dust on Earth.
               return true;
           }
       }
   ```

2. **Get the body's speed of sound** from what the game declares. KSArmory builds a small value (`BodyAir`) from the body's
   atmosphere and takes `SoundMetresPerSecond = sqrt(gamma * P0 / rho0)`:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L2428-L2437
           try
           {
               // KSA's own IsValid() is an astronomical-scale check on the scale height and is false for
               // every real atmosphere (MediumDensityRatioAt), so the terms are checked instead.
               if (body.GetAtmosphereReference()?.Physical is not { } air) return BodyAir.Airless(gravity, radius, traits);

               return new BodyAir(air.GetAtmosphericPressureAtAltitude(0.0), air.GetAtmosphericDensityAtAltitude(0.0),
                                  air.ScaleHeight.InMeters(), air.Height, gravity, radius, traits,
                                  Wet: body.GetOceanReference() is { } ocean && ocean.Density > 0.0);
           }
   ```

   For a plain delay-by-distance the reduction is small. Adapted from `KsaWorld.BodyAirOf` and `BodyAir.SoundMetresPerSecond`
   (the gamma of 1.4 is an assumption, not a game value):

   ```csharp
   // adapted from src/KSArmory/Ksa/KsaWorld.cs L2420-L2442 and src/KSArmory/Sim/BodyAir.cs L72
   static double SoundMetresPerSecond(Celestial body, double gamma = 1.4)
   {
       if (body.GetAtmosphereReference()?.Physical is not { } air) return 0.0;
       double p0 = air.GetAtmosphericPressureAtAltitude(0.0);
       double rho0 = air.GetAtmosphericDensityAtAltitude(0.0);
       return p0 > 0.0 && rho0 > 0.0 ? Math.Sqrt(gamma * p0 / rho0) : 0.0;
   }
   ```

3. **Anchor the event to the ground**, as an offset in the nearest body's own frame, and turn it back each frame:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L944-L959
               for (int i = 0; i < system.Count; i++)
               {
                   if (system.GetIndex(i) is not Celestial candidate) continue;

                   double range = Vec.Len(pointEcl - candidate.GetPositionEcl()) - candidate.MeanRadius;
                   if (range >= nearestRange) continue;

                   nearest = candidate;
                   nearestRange = range;
               }

               if (nearest is null) return false;

               body = nearest;
               anchor = doubleQuat.Conjugate(nearest.GetBodyFixed2Ecl()) * (pointEcl - nearest.GetPositionEcl());
               return Vec.IsFinite(anchor);
   ```

   `TryGroundAnchorEcl` is the inverse: `positionEcl = body.GetPositionEcl() + GetBodyFixed2Ecl() * anchor`, with the
   ground's velocity (orbit plus spin) beside it.[src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L978-L1005) The mod's comment is why this matters: "an ecliptic
   coordinate is left behind at the body's ~29.8 km/s the instant it is written down".

4. **Start a front, merging coincident events.** A new burst on the same body, no more than 0.5 s after a queued front and
   anchored within 1 km, adds its charge to that front instead of queuing another (six warheads of a bus land metres
   apart in one frame, and six copies of one sound is a louder bang, not six). `Begin` is called both on the
   new-cloud path and on the merge path of the cloud code, so a bomb dropped on a standing fireball is still heard:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L98-L127
       public static void Begin(Celestial body, double3 burstEcl, double chargeKg, double burstHeight = 0.0)
       {
           try
           {
               if (!KsaWorld.HasAtmosphere(body)) return;

               AmbientAir air = KsaWorld.AirAt(body, burstEcl);
               double height = double.IsFinite(burstHeight) ? Math.Max(burstHeight, 0.0) : 0.0;
               if (!double.IsFinite(BlastFront.For(chargeKg, air, KsaWorld.BodyAirOf(body).SoundMetresPerSecond, height)
                                        .ArrivalSeconds(1.0))) return;

               // Anchored to the body, because the front is minutes out at worst and a bare ecliptic point
               // is left behind by the planet's 29.8 km/s long before it arrives.
               if (!KsaWorld.TryAnchorToGround(burstEcl, out object? anchored, out double3 anchor)) return;

               foreach (Front f in _fronts)
               {
                   if (!ReferenceEquals(f.Anchored, anchored) || f.Age > SameBangSeconds) continue;
                   if (Vec.Len(f.Anchor - anchor) > SameBangMetres) continue;

                   f.ChargeKg += chargeKg;
                   return;
               }

               Front front = new()
               {
                   Body = body, Anchored = anchored, Anchor = anchor, Height = height, BurstAir = air,
                   ChargeKg = chargeKg, Seed = ++_seeds,
               };
               _fronts.Add(front);
   ```

   (The remaining lines log the predicted arrival time.) The two call sites are
   [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L885-L892) and
   [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L958-L961).

5. **Age fronts on the simulated step and compare with the camera.** `Update(dt)` runs once a frame with the applied
   step. For every front it ages by `dt`, rebuilds the burst's ecliptic place from the anchor, and calls `Follow`; the
   first step where the radius reaches the live range is the arrival. Because the range is measured to the camera every frame,
   a listener who approaches the burst hears it earlier:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L155-L180
       public static void Update(double dt)
       {
           if (double.IsFinite(dt) && dt >= 0.0)
           {
               bool eye = KsaWorld.TryMainCameraPose(out double3 eyeEcl, out _);

               for (int i = _fronts.Count - 1; i >= 0; i--)
               {
                   Front front = _fronts[i];
                   front.Age += dt;

                   try
                   {
                       if (eye && KsaWorld.TryGroundAnchorEcl(front.Anchored, front.Anchor, out double3 burstEcl, out _))
                       {
                           Follow(front, burstEcl, eyeEcl);
                       }
                   }
                   catch
                   {
                       front.Direct = front.Reflected = true;
                   }

                   if ((front.Direct && front.Reflected) || front.Age > FollowSeconds) _fronts.RemoveAt(i);
               }
           }
   ```

   `Follow` marks the direct report when `radius >= range`:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L203-L214
       private static void Follow(Front front, double3 burstEcl, double3 eyeEcl)
       {
           BlastFront law = front.Law;
           double radius = law.Radius(front.Age);
           double range = Vec.Len(eyeEcl - burstEcl);
           double3 up = Vec.Unit(burstEcl - front.Body.GetPositionEcl());

           if (!front.Direct && radius >= range)
           {
               front.Direct = true;
               Arrive(front, burstEcl, eyeEcl, range);
           }
   ```

   Fronts are dropped when both reports are done or after `FollowSeconds` (1800 s) so nothing waits forever. For an
   ordinary event the front radius is `c * age`, with `c` from step 2; KSArmory's own law is Sedov-Taylor while the shock is
   strong, then a sonic run, and arrival times come from bisecting it ([src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L616-L660)).

6. **Gate at the ear on the pressure that arrives.** The bang plays only if the front still carries enough overpressure
   where the camera is (computed from the burst's air and the air at the camera). One gate serves the bang and the view
   shake because they are one wave:

   ```csharp
   // src/KSArmory/Sim/BurstHearing.cs L13-L27
       /// <summary>The weakest front heard and felt as the burst's (Pa): a boom, at about 128 dB.</summary>
       public const double FeltPascals = 50.0;

       /// <summary>The front at which the bang is at full loudness (Pa).</summary>
       public const double LoudPascals = 1_000.0;

       /// <summary>Whether a front of this peak is heard.</summary>
       public static bool Heard(double pascals) => pascals >= FeltPascals;

       /// <summary>
       /// How loud, from nothing to one: a square root of the pressure, as <see cref="ViewShake.Strength"/>
       /// takes it, and nothing under <see cref="FeltPascals"/>.
       /// </summary>
       public static double Loudness(double pascals)
           => Heard(pascals) ? Math.Min(1.0, Math.Sqrt(pascals / LoudPascals)) : 0.0;
   ```

   In `Arrive`, a front below the gate only logs `too weak to hear or feel`; otherwise the mod plays the bang at that
   loudness with `Play(front.Anchored, front.Anchor, front.ChargeKg, loudness)` and strikes the view shake.[src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L242-L264)

7. **Add the ground-reflected second report for air bursts** (optional). For a burst above the ground, a second report
   travels via the mirror path: the ground gives back most of what reaches it and it has come further. KSArmory plays it at
   0.6 of the direct loudness when the front reaches the mirror distance, but only if the ear is above the ground
   plane, outside the Mach stem and at least 0.15 s later than the direct report; otherwise the two count as one
   (L216-L238).

8. **Play the sound** when `Arrive` calls `Play`: start the stock sound paused, pitch it, set its parameters, unpause, and keep
   re-placing it for as long as it plays. That is the other article:
   [Play Core's explosion sound on your own schedule](../drive-sound-parameters-from-code/drive-sound-parameters-from-code-2.md).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs) | `Begin` (gate, anchor, merge), `Update`, `Follow`, `Arrive`, `Play` |
| [src/KSArmory/Sim/BurstHearing.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BurstHearing.cs) | 50 Pa gate and loudness |
| [src/KSArmory/Sim/BlastFront.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastFront.cs) | Per-burst front: radius and arrival time |
| [src/KSArmory/Sim/BlastAltitude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastAltitude.cs#L14-L160) | Blast efficiency by burst air, Sachs-scaled pressure at the ear, front radius and arrival |
| [src/KSArmory/Sim/BodyAir.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BodyAir.cs) | A body's air as numbers, including the speed of sound |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L932-L1005) | Ground anchor and its inverse; camera pose; `HasAtmosphere`, `BodyAirOf`, `AirAt` |

## Variations and gotchas

- **Doppler of the late sound.** `TrySpatial` passes the ground's absolute ecliptic velocity (orbit plus spin) as the source
  velocity; the engine's own explosion audio passes a velocity relative to the object the camera follows
  (`Camera.GetVelocityEgo`). FMOD's Doppler scale is 1 in this build (`Set3dSettings(1f, 1f, 0.1f)`), so the difference
  may bend the pitch of a late bang. Not measured here (unverified).
- **Time warp.** A front that arrives above 10x is stopped on the next `GameAudio.UpdateAudio` by `StopForSimSpeed`, because
  the bang's layers use the default `SimSpeed` rule and are played directly (no 10x gate, but the per-channel stop still
  applies). The view shake is unaffected. See
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **No 50 km cap.** The engine drops an explosion sound beyond `MaxAudibleDistanceMeters` (50,000 m); the only gate here is
  50 Pa at the ear, so a large burst is heard much further out.
- **Gamma is yours.** The game has no temperature; the mod's table gives a default gamma of 1.4 and per-body overrides from
  its own `Bodies.xml` entries. A different gamma changes every arrival time proportionally.
- **Map view.** Both the front and the listener use the main viewport's base camera, not the map camera
  (`Program.GetMainCamera()` returns the map camera in map mode), so the range is measured to the world camera's pose and
  not to the map view.
- **Turning the effect off turns the bang off.** The mod's cloud switch also clears fronts, because the preset has no
  sound of its own.
- **Not in `StarMapUnload`.** Program.Dispose closes FMOD (`GameAudio.Release`) before `StarMapUnload` runs, so there is nothing
  left to stop by then and a stop call would hit a closed system. See [release sound channels at scene change and exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md).

## See also

- [Play Core's explosion sound on your own schedule](../drive-sound-parameters-from-code/drive-sound-parameters-from-code-2.md)
- [Overview of this topic](overview.md)
- Mod feature: [burst sound at blast arrival](../../../../mods/ksarmory/features/burst-sound-at-blast-arrival.md)
- [Simulate mod-owned bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Spawn particle effects](../../../rendering/lights-and-particles/spawn-particle-effects/overview.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
