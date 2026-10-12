---
type: Mod Feature
title: The burst's bang, played when its blast front reaches the listener
description: The nuclear preset ships without Core's SoundEvent, and the mod follows each burst's blast front out to the audio camera, then plays Core's ExplosionBig itself, pitched by yield, as loud as the pressure there, with coincident bursts merged
mod: ksarmory
category: audio
techniques:
  - fmod-audio
  - xml-asset
  - starmap-lifecycle
  - harmony-prefix
  - ground-anchored-source
  - live-front-arrival
  - event-deduplication
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.GameAudio
  - KSA.IChannel
  - KSA.MultiChannelWrapper
  - KSA.ClusterChannelWrapper
  - KSA.ChannelWrapper
  - KSA.SoundBehavior
  - KSA.MultiSound
  - KSA.SoundReference
  - KSA.SoundEvent
  - KSA.SpatialAudio
  - KSA.KeyHash
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphereReference
  - KSA.ExplosionSoundSystem
  - KSA.ExplosionSystem
  - KSA.Rendering.Particles.ExplosionReference
  - KSA.AssetBundle
  - KSA.ModLibrary
  - KSA.Camera
  - KSA.Celestial
  - KSA.Universe
  - KSA.Program
tags:
  - audio
  - fmod
  - explosion
  - nuclear
  - blast-front
  - speed-of-sound
  - spatial-audio
  - sound-parameters
  - deduplication
---

A nuclear burst in KSArmory is seen at once and heard later. The flash comes with the light; the
bang comes when the blast front gets to the listener: about seven seconds from the 2.5 km a cloud is
watched from, about a minute from 50 Mt at 37 km overhead, and never from a burst too high for its
front to carry 50 Pa to the ear or from one on a body with no air.[^ksarmory-burstsound][^ksarmory-nuclear-altitude]

KSA's explosion presets play their sound at the instant of the explosion, which is right for a
conventional blast heard from inside its own radius. So the mod's nuclear preset is Core's
`Explosion_Conflagration` with the `<SoundEvent>` left out, and `BurstSound` plays the same Core
sound, `ExplosionBig`, by hand: it follows each burst's front on the simulated step against where the
audio camera is now, and on arrival starts the sound paused through `SoundBehavior.Play`, pitches it
down by yield, sets the `Distance`, `Pressure` and `Intensity` parameters Core's layers are mixed on,
and re-places it every frame the way KSA's own `ExplosionSoundSystem` does.[^ksarmory-particles-xml][^ksa-explosionsoundsystem]

## How it works

### 1. The sound taken out of the preset

| Piece | Detail |
|---|---|
| Core's preset | `Explosion_Conflagration` in `Core/ExplosionAssets.xml` (L99-L116) carries `<SoundEvent SoundId="ExplosionBig"/>` (L105). `AssetBundle` maps `<Explosion>` to `ExplosionReference` (L72), whose `Sound` field is that element (ExplosionReference.cs L21-L22).[^ksa-explosionassets][^ksa-assetbundle][^ksa-explosionreference] |
| Where the engine plays it | `ExplosionSystem.SpawnPresetInternal` calls `ExplosionSoundSystem.Spawn(anchor, sound, intensity)` right after the flash, in the same call that spawns the explosion (ExplosionSystem.cs L253-L257).[^ksa-explosionsystem] |
| The mod's preset | `KSArmoryNuclearBurst` (KSArmoryParticles.xml L539-L556): Core's flash unchanged, no `SoundEvent`, no `VolumeRef`. `WarheadExplosion.PresetFor` returns it for a charge of at least `MushroomCloud.ThresholdKg` (1,000 kg TNT), the same threshold under which `NuclearClouds.Begin` returns at once, so every burst that goes off silently has a front queued for it.[^ksarmory-warheadexplosion][^ksarmory-nuclearclouds] |
| The sound itself | `ExplosionBig` is a Core `<MultiSound>` (ExplosionAssets.xml L219-L249) of three `<Sound>` layers: a close crack whose volume fades out over `Distance` 100-1,000 m; a far report that fades in over the same range, with low- and high-pass filters on `Distance`; and an echo layer (sound group `ExplosionEcho`, two channels at most) whose volume runs 0.25-1 on `Pressure` 0-0.05. The mod finds it with `ModLibrary.Get<SoundBehavior>("ExplosionBig")`.[^ksa-explosionassets][^ksa-modlibrary] |

### 2. Following the front

`NuclearClouds.Begin` calls `BurstSound.Begin(body, burstEcl, chargeKg, height)` for every burst in
air that is not under the sea, both when it starts a new cloud (L958-L961) and when it merges into a
burst still going off (L889-L893), so a bomb dropped on a standing fireball is still heard.[^ksarmory-nuclearclouds]

| Step | What `BurstSound.Begin` does |
|---|---|
| Vacuum gate | Returns if `KsaWorld.HasAtmosphere(body)` is false (`GetAtmosphereReference()?.Physical` with `Height > 0`), or if `BlastFront.ArrivalSeconds(1.0)` is not finite: no density at the burst, or no blast left after `BlastAltitude.Efficiency`.[^ksarmory-ksaworld][^ksarmory-blastaltitude] |
| Air | Burst air from `KsaWorld.AirAt` (KSA's isothermal `P0 exp(-h/H)` profile), one speed of sound per body, `sqrt(gamma x P0 / rho0)`, from `GetAtmosphericPressureAtAltitude(0)` and `GetAtmosphericDensityAtAltitude(0)` with `gamma` from the mod's own body traits (default 1.4).[^ksarmory-bodyair][^ksa-physicalatmosphere] |
| Anchor | `KsaWorld.TryAnchorToGround` stores the burst as an offset in the nearest body's own frame (`Celestial.GetBodyFixed2Ecl`), because a front minutes out would otherwise be left behind by the planet's orbital speed.[^ksa-celestial] |
| Merge | A front on the same body, no more than 0.5 s old, anchored within 1 km, takes the new charge (`ChargeKg +=`) instead of a new front being queued: six warheads of a bus are one louder, deeper bang, not six copies. |
| Front law | `BlastFront.For(charge, burstAir, c, height)`: energy multiplied by `1 + GroundCoupling(kt, height)`, radius by Sedov-Taylor on the blast share of the energy until it slows to Mach 1.2, then a sonic run eased from that Mach; arrival is the bisected inverse.[^ksarmory-blastfront][^ksarmory-mushroomcloud] |

`Update(dt)` then ages every front by the simulated step and compares its radius with the live
distance from the burst to the eye, `KsaWorld.TryMainCameraPose` (the main viewport's
`BaseCamera`, which is also what `GameAudio.GetAudioCamera()` returns). The front is followed, never
timed from the burst, so a camera flying toward it hears it sooner.[^ksarmory-burstsound][^ksa-gameaudio]

| Event | Test | Result |
|---|---|---|
| Direct report | First step with `law.Radius(age) >= range` | `Arrive`: overpressure at the ear by `BlastAltitude.PeakPascals` (the blast charge Sachs-scaled to the ear's air, zero if the ear has none, times the ground's gain there); `BurstHearing.Heard` at 50 Pa or more, loudness `min(1, sqrt(p / 1000 Pa))`; plays the bang and strikes the view shake with the front's positive phase; under 50 Pa it only logs[^ksarmory-bursthearing] |
| Ground-reflected report | Air bursts only (`Height > 0`); ear above the ground plane, outside the Mach stem (`GroundReflection.InReflection <= 0.5`), and the mirror path (source `2h` below the burst) at least 0.15 s longer | When the radius reaches the mirror distance, a second bang at 0.6 x the loudness of `PeakPascals(..., image, 1.0)`; otherwise the two count as one[^ksarmory-groundreflection] |
| Done | Both reports handled, or 1,800 s old, or a throw while following | Front dropped |

The author flew it: 20 kt at 6 km arrived in 16.5 s at 5.1 kPa; 50 Mt at 37 km overhead in 59.3 s at
11.4 kPa; 3.8 Mt at 77 km in 233 s at 150 Pa, faintly, and unheard from about 88 km. From above
the stem an air burst is heard twice, 3.5 s apart from 7.7 km up.[^ksarmory-nuclear-altitude][^ksarmory-nuclear-effect]

### 3. Playing it the way the engine does

`Play` copies `ExplosionSoundSystem.Spawn` and `UpdateAudio` step for step, with three changes:
the start time, the pitch, and the gates.[^ksa-explosionsoundsystem]

| Step | KSA's `ExplosionSoundSystem` | `BurstSound` |
|---|---|---|
| Position | `ExplosionFlashSystem.ResolveAnchorEgo` of the explosion's `BubbleOrigin`, wrapped in a `SpatialAudio`[^ksa-spatialaudio] | Ground anchor back to Ecl, minus `GetAudioCamera().PositionEcl` (Ego is a pure translation of Ecl) |
| Velocity | `audioCamera.GetVelocityEgo(parent)` (minus the followed object's) plus the anchor's own | The ground's ecliptic velocity, orbit plus spin, not made camera-relative |
| Pressure | `PhysicalAtmosphereReference.GetAtmosphericPressure(audioCamera)`, in atmospheres | The same call |
| Start | `GameAudio.PlaySound(SoundEvent, ...)`, `startPaused: true`, volume from `VolumeForIntensity`; refused above 10x simulation speed and beyond `MaxAudibleDistanceMeters` (50,000 m) | `SoundBehavior.Play(spatial, loudness, out channel, startPaused: true)` directly, so neither gate applies |
| Pitch | None | `channel.PitchMultiplier = MushroomCloud.BangPitch(kt)`: `cbrt(0.3 kt / kt)` clamped to 0.35-1, so eight times the yield is an octave down and twice as long; `MultiChannelWrapper` forwards it to all three layers[^ksa-multichannelwrapper] |
| Parameters | `SetParameter` of `Distance`, `Pressure`, `Intensity` by `KeyHash.Make` | The same three, `Intensity` fixed at 1[^ksa-keyhash] |
| Unpause | `SetPaused(false)`, then the subscribed cluster channel if it is paused | The same |
| Each frame | Re-placed while `GameAudio.IsStillActive` | The same, from `BurstSound.Update` |

`MultiSound.Play` plays every layer and wraps the channels in one `MultiChannelWrapper`, which is
the `IChannel` the mod holds.[^ksa-multisound][^ksa-ichannel] The echo layer runs 10.6-10.8 s at
pitch 1, so at the 0.35 floor (reached near 7 kt) it rolls for about thirty seconds.[^ksarmory-mushroomcloud]

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `startup:asset-load` | `ModLibrary.LoadAll` reads `KSArmoryParticles.xml` and Core's `ExplosionAssets.xml` | `KSArmoryNuclearBurst` registered without a `SoundEvent`; `ExplosionBig` registered as a `SoundBehavior`[^ksarmory-particles-xml] |
| Burst | Normally earlier in the same `StepSimulation`: the weapon update (KSArmoryMod.cs L776) -> `WeaponSystem.Detonate` -> `NuclearClouds.Begin` -> `BurstSound.Begin` | Front queued or merged |
| `frame:draw-ui` (or `frame:celestials`, or `frame:after-on-frame`) | `BurstSound.Update(_lastSimStep)` in the tail of `StepSimulation` (KSArmoryMod.cs L943), run once per frame by `StepOnce`: from the `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (Program.cs L2268); with the HUD hidden from the Harmony prefix on `OnFrameCelestials` (L2320); and from `[StarMapAfterOnFrame]` only if that prefix did not install[^ksarmory-mod][^ksarmory-prerenderhook][^ksa-program] | Fronts advanced; bangs started; playing bangs re-placed against the camera |
| `frame:post-render` (engine) | `GameAudio.UpdateAudio` in `PostRender` (Program.cs L2362): FMOD `System.Update`, `ExplosionSoundSystem.UpdateAudio`, then per channel fade, simulation-speed pitch and stop, parameters, volume, filters (GameAudio.cs L150-L208) | The parameters the mod set this frame are applied; the mod runs nothing here[^ksa-gameaudio] |
| Save load | `WorldReloadHook` -> `ForgetTheWorld` -> `NuclearClouds.Clear()` -> `BurstSound.Clear()` (KSArmoryMod.cs L377, NuclearClouds.cs L1124) | Fronts forgotten, channels `Stop(stopImmediate: true)`, as `Universe.DeserializeSave` does for the engine's own (`ExplosionSoundSystem.StopAll`, Universe.cs L2446)[^ksa-universe] |

Why there:

- **On the simulated step.** The front is a thing in the world: it waits through a pause and
  hurries under timewarp. `_lastSimStep` is the step the engine applied, not `dtPlayer`.
- **Before the render.** On both normal paths the step runs before `PostRender`, so a bang started
  this frame has its position and parameters in place when `GameAudio.UpdateAudio` calls FMOD's
  update; on the `[StarMapAfterOnFrame]` fallback they reach FMOD a frame later.
- **In the same step as the shake.** `Arrive` calls `BlastShake.Struck` with the same pressure and
  phase, so the bang and the view shake land on the same frame (see the flash and shake feature).

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L98-L141) | `Begin`: vacuum gate, ground anchor, merge of coincident bursts |
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L155-L264) | `Update`, `Follow` (direct and reflected reports), `Arrive` |
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L266-L327) | `TrySpatial`, `Apply`, `Play` |
| [src/KSArmory/Sim/BurstHearing.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BurstHearing.cs) | 50 Pa gate and loudness |
| [src/KSArmory/Sim/BlastFront.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastFront.cs) | One front per burst, shared with damage and the drawn ring |
| [src/KSArmory/Sim/BlastAltitude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastAltitude.cs#L14-L160) | Blast efficiency by burst air, Sachs-scaled pressure and phase, front radius and arrival |
| [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L457-L495) | `BangAnchorKt`, `BangPitchFloor`, `BangPitch` |
| [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L616-L660) | `ShockRadiusIn` and its bisected inverse |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L522-L556) | The nuclear preset without a `SoundEvent` |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L885-L961) | The two `BurstSound.Begin` call sites |
| [tests/KSArmory.Tests/BurstHearingTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/BurstHearingTests.cs) | Gate and loudness tests[^ksarmory-hearing-tests] |
| [tests/KSArmory.Tests/BlastAltitudeTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/BlastAltitudeTests.cs#L78-L98) | 50 Mt at 37 km heard on the ground; Teak at 77 km not[^ksarmory-altitude-tests] |

## Snippets

```xml
<!-- src/KSArmory/KSArmoryParticles.xml L539-L556 -->
    <Explosion Id="KSArmoryNuclearBurst">
        <Flash PeakIntensity="5000" Range="200" DecaySeconds="0.2" LifeSeconds="0.55" ColorMidAt="0.3">
            <ColorStart R="1" G="0.97" B="0.9"/>
            <ColorMid R="1" G="0.72" B="0.28"/>
            <ColorEnd R="1" G="0.25" B="0.05"/>
        </Flash>
        <!--
          NO SoundEvent. The engine plays one at the instant of the burst, which is right for
          everything it ships: a conventional explosion is heard from inside its own blast radius
          or not at all. A nuclear one is watched from kilometres off, where the flash and the bang
          are separated by the whole of the time sound takes to cross the gap. Ksa/BurstSound.cs
          plays it when it arrives.

          And NO VolumeRef. KSA raymarches explosion volumes into the same low-resolution images
          as its weather clouds, and CloudPass reads those images as weather: a volume here would
          occlude the burst's own cloud, in blocks.
        -->
    </Explosion>
```

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
            // ...
```

```csharp
// src/KSArmory/Ksa/BurstSound.cs L284-L315
    private static void Apply(IChannel channel, SpatialAudio spatial)
    {
        channel.SetSpatialAudio(spatial);
        channel.SetParameter(DistanceHash, (float)spatial.Distance());
        channel.SetParameter(PressureHash, (float)spatial.AtmosphericPressure);
        channel.SetParameter(IntensityHash, 1f);
    }

    private static void Play(object? body, double3 anchor, double chargeKg, double loudness)
    {
        try
        {
            if (!TrySpatial(body, anchor, out SpatialAudio spatial)) return;
            if (ModLibrary.Get<SoundBehavior>(BangId) is not { } sound) return;

            // Started paused, pitched and placed before it is let go, so no part of it is heard at
            // the pitch of a rocket going off or mixed as though it were at the listener. The
            // multiplier reaches every layer KSA's bang is made of -- the crack, the far report and
            // the ten-second echo -- through the multi-channel wrapper.
            double kt = MushroomCloud.KilotonsFor(chargeKg);
            float pitch = (float)MushroomCloud.BangPitch(kt);

            sound.Play(spatial, (float)Math.Clamp(loudness, 0.0, 1.0), out IChannel? channel, startPaused: true);
            if (channel is null) return;

            channel.PitchMultiplier = pitch;
            Apply(channel, spatial);
            channel.SetPaused(false);

            if (channel.SubscribedClusterSound is { } cluster && cluster.IsPaused()) cluster.SetPaused(false);

            _playing.Add((channel, anchor, body));
            // ...
```

```csharp
// current/decomp/KSA/ExplosionSoundSystem.cs L93-L108
	private static SpatialAudio BuildSpatialAudio(in BubbleOrigin anchor)
	{
		Camera audioCamera = GameAudio.GetAudioCamera();
		double3 posEgo = ExplosionFlashSystem.ResolveAnchorEgo(in anchor, audioCamera);
		anchor.GetStatesCci(out var _, out var velocityCci);
		double3 velEgo = audioCamera.GetVelocityEgo(anchor.Parent) + velocityCci.Transform(anchor.Parent.GetCci2Cce());
		return new SpatialAudio(posEgo, velEgo, PhysicalAtmosphereReference.GetAtmosphericPressure(audioCamera));
	}

	private static void ApplySpatialAudio(IChannel channel, in SpatialAudio spatial, float intensity)
	{
		channel.SetSpatialAudio(spatial);
		channel.SetParameter(DistanceHash, (float)spatial.Distance());
		channel.SetParameter(PressureHash, (float)spatial.AtmosphericPressure);
		channel.SetParameter(IntensityHash, intensity);
	}
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `ExplosionReference.Sound` (`<SoundEvent>`), `SoundEvent.SoundId` | [current/decomp/KSA.Rendering.Particles/ExplosionReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ExplosionReference.cs#L21-L22) | The element left out of the mod's preset |
| `ExplosionSystem.SpawnPresetInternal` | [current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L229-L257) | Where KSA plays a preset's sound at the instant of the burst |
| `ExplosionSoundSystem.Spawn`, `UpdateAudio`, `BuildSpatialAudio`, `ApplySpatialAudio`, `MaxAudibleDistanceMeters` | [current/decomp/KSA/ExplosionSoundSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSoundSystem.cs#L19-L108) | The engine's own explosion audio, copied step for step |
| `ModLibrary.Get<SoundBehavior>` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1059-L1110) | Finds `ExplosionBig`; throws `NullReferenceException` for a missing id (L1102-L1109) |
| `MultiSound.Play` | [current/decomp/KSA/MultiSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MultiSound.cs#L51-L63) | Plays every layer, returns a `MultiChannelWrapper` |
| `IChannel.PitchMultiplier`, `SetSpatialAudio`, `SetParameter`, `SetPaused`, `SubscribedClusterSound`, `Stop` | [current/decomp/KSA/IChannel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IChannel.cs) | The handle the bang is pitched, placed and stopped through ([current/decomp/KSA/MultiChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MultiChannelWrapper.cs#L35-L86) forwards each to every layer) |
| `GameAudio.GetAudioCamera`, `IsStillActive`, `PlaySound`, `UpdateAudio` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L65-L68) | Listener camera (`Program.MainViewport.BaseCamera`), liveness (L217), the 10x gate the mod bypasses (L383-L389), the per-frame update (L150) |
| `SpatialAudio(posEgo, velEgo, atmosphericPressure)`, `Distance()` | [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L29-L73) | Source placement and the `Distance` parameter |
| `PhysicalAtmosphereReference.GetAtmosphericPressure(Camera)`, `GetAtmosphericPressureAtAltitude`, `GetAtmosphericDensityAtAltitude` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L50-L88) | Pressure at the listener in atmospheres; sea-level terms for the speed of sound |
| `ChannelWrapper.SetSpatialAudio`, `StopForSimSpeed`; `SoundReference.SimSpeedRule` | [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L433-L503) | Low-pass from listener pressure (L441); channels stopped above 10x unless `SimSpeedRule` is `Ignore` ([current/decomp/KSA/SoundReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SoundReference.cs#L23) L23, default `All`)[^ksa-channelwrapper][^ksa-soundreference] |
| `KeyHash.Make` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15) | Parameter ids |
| `Camera.PositionEcl`, `GetVelocityEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L261) | Ego origin; the engine's relative source velocity[^ksa-camera] |
| `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2446) | The engine stops its own explosion sounds on load |

## Pitfalls and version notes

- **Played by hand, the layers mix wrong unless the parameters are set.** Core's layers fade on
  `Distance` and `Pressure`, which KSA sets only inside `ExplosionSoundSystem`. Left at zero, the far
  report is silent at every range, the crack at full and the echo at its floor; the author found this
  after the first version.[^ksarmory-nuclear-next]
- **A missing sound throws rather than returning null.** `ModLibrary.Get<SoundBehavior>` throws
  `NullReferenceException`, so the `is not { } sound` test never fires; the surrounding `catch`
  logs one warning and every later bang is silent. A renamed `ExplosionBig` in Core would do that.
- **High warp still silences it.** Calling `SoundBehavior.Play` skips `GameAudio.PlaySound`'s 10x
  gate, but `GameAudio.UpdateAudio` runs `StopForSimSpeed` on every active channel and
  `ExplosionBig`'s layers leave `SimSpeedRule` at its default `All`, so a front that arrives above
  10x is stopped on the next update. The shake is not affected.
- **Source velocity is not camera-relative.** The engine passes `Camera.GetVelocityEgo` (the followed
  object's velocity subtracted); `TrySpatial` passes the ground's ecliptic velocity, orbit included,
  and KSA initialises FMOD with a Doppler scale of 1 (`Set3dSettings(1f, 1f, 0.1f)`, GameAudio.cs
  L109). How that changes what is heard was not measured here (unverified).
- **No 50 km cap.** The engine drops an explosion sound beyond `MaxAudibleDistanceMeters`; the mod's
  only gate is 50 Pa at the ear, so a large burst can be heard much further out.
- **Turning clouds off turns the bang off.** With `Config.NuclearClouds` false the step calls
  `NuclearClouds.Clear()` every frame (KSArmoryMod.cs L935), which also clears `BurstSound`, and the
  nuclear preset has no sound of its own; the setting's comment says only the standing cloud goes.[^ksarmory-config]
- **Map view hears from the world camera.** Both the front and the listener use
  `MainViewport.BaseCamera`, which in map mode keeps the last world pose.
- **`StarMapUnload` does not clear it.** `Unload` never calls `NuclearClouds.Clear`, so a bang
  playing at unload runs out on KSA's own channel list.
- **Doc drift.** `docs/NUCLEAR-ALTITUDE.md` L47 and L56 list a 45 s cap (`FurthestSeconds`) and four
  sound speeds; those are the pre-fix audit, removed by step 9 and step 14 (L216, L221). The code
  follows a front for 1,800 s with one speed per body.

## See also

- [Blast damage and part health](./blast-damage-and-part-health.md), which shares the same `BlastFront`
- [Burst flash and view shake](./burst-flash-and-view-shake.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [KSA explosions from a point](./ksa-explosions-from-a-point.md)
- [Spatial gun and motor sounds](./spatial-gun-and-motor-sounds.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Detect save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Stop channels on world reload with stopImmediate and treat unload-time StopAll as moot](../../../howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-1.md) - how-to article demonstrating this feature
- [Follow a burst's front against the live camera and play its bang on arrival](../../../howto/audio/playback/delay-a-sound-until-the-blast-front-arrives/delay-a-sound-until-the-blast-front-arrives.md) - how-to article demonstrating this feature
- [Play Core's explosion sound on your own schedule with hand-set parameters](../../../howto/audio/playback/drive-sound-parameters-from-code/drive-sound-parameters-from-code-2.md) - how-to article demonstrating this feature
- [Read the sound type map through the features that use each type](../../../howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/understand-the-games-sound-and-music-xml-types.md) - how-to article demonstrating this feature

[^ksarmory-burstsound]: src/KSArmory/Ksa/BurstSound.cs
[^ksarmory-bursthearing]: src/KSArmory/Sim/BurstHearing.cs
[^ksarmory-blastfront]: src/KSArmory/Sim/BlastFront.cs
[^ksarmory-blastaltitude]: src/KSArmory/Sim/BlastAltitude.cs
[^ksarmory-mushroomcloud]: src/KSArmory/Sim/MushroomCloud.cs
[^ksarmory-bodyair]: src/KSArmory/Sim/BodyAir.cs
[^ksarmory-groundreflection]: src/KSArmory/Sim/GroundReflection.cs
[^ksarmory-warheadexplosion]: src/KSArmory/Sim/WarheadExplosion.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-prerenderhook]: src/KSArmory/Ksa/PreRenderHook.cs
[^ksarmory-particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^ksarmory-hearing-tests]: tests/KSArmory.Tests/BurstHearingTests.cs
[^ksarmory-altitude-tests]: tests/KSArmory.Tests/BlastAltitudeTests.cs
[^ksarmory-nuclear-next]: docs/NUCLEAR-NEXT.md
[^ksarmory-nuclear-altitude]: docs/NUCLEAR-ALTITUDE.md
[^ksarmory-nuclear-effect]: docs/NUCLEAR-EFFECT.md
[^ksa-explosionassets]: Core/ExplosionAssets.xml (game data)
[^ksa-explosionreference]: KSA.Rendering.Particles.ExplosionReference (decompiled)
[^ksa-explosionsystem]: KSA.ExplosionSystem (decompiled)
[^ksa-explosionsoundsystem]: KSA.ExplosionSoundSystem (decompiled)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-gameaudio]: KSA.GameAudio (decompiled)
[^ksa-ichannel]: KSA.IChannel (decompiled)
[^ksa-multisound]: KSA.MultiSound (decompiled)
[^ksa-multichannelwrapper]: KSA.MultiChannelWrapper (decompiled)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled)
[^ksa-soundreference]: KSA.SoundReference (decompiled)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled)
[^ksa-keyhash]: KSA.KeyHash (decompiled)
[^ksa-physicalatmosphere]: KSA.PhysicalAtmosphereReference (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-program]: KSA.Program (decompiled)
