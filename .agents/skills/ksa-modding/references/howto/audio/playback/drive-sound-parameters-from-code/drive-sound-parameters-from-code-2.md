---
type: How-To
title: Play Core's explosion sound on your own schedule with hand-set parameters
description: Strip the SoundEvent from a copied Core explosion preset so the engine stays silent, then play the same MultiSound yourself with Distance, Pressure and Intensity set so its layers mix correctly
category: audio
group: playback
topic: drive-sound-parameters-from-code
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/burst-sound-at-blast-arrival]
techniques: [fmod-audio, xml-asset]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle:
  - startup:asset-load
  - frame:draw-ui
  - frame:celestials
  - frame:after-on-frame
  - frame:post-render
  - event:load
game_types:
  - KSA.MultiSound
  - KSA.MultiChannelWrapper
  - KSA.ExplosionSoundSystem
  - KSA.ExplosionSystem
  - KSA.Rendering.Particles.ExplosionReference
  - KSA.SoundBehavior
  - KSA.SpatialAudio
  - KSA.KeyHash
  - KSA.GameAudio
  - KSA.AssetBundle
tags: [audio, fmod, parameters, explosion, multisound, game-audio, reuse]
---

Outcome: Core's big explosion sound (`ExplosionBig`) plays when the mod decides, not at the instant of the explosion, with
the same layered mix the engine would give it. Prerequisites: StarMap, and a mod-declared explosion preset. KSArmory
demonstrates it for nuclear bursts that are watched from kilometres away, whose bang should arrive when the pressure
front does ([delay a world sound until the front arrives](../delay-a-sound-until-the-blast-front-arrives/overview.md)
is the timing half; this article is the sound half).

## Background: how the game does it

Core's explosion presets name a sound with `<SoundEvent SoundId="..."/>`. `Explosion_Conflagration` names
`ExplosionBig`, a `<MultiSound>` of three layers, each a `<Sound>` with its own spatial data and `<Param>` curves:[current/Content/Core/ExplosionAssets.xml](Core/ExplosionAssets.xml (build 2026.10.10.5554)#L219-L249)[^ksa-core-explosionassets]

| Layer | Priority | Curves |
|---|---|---|
| Close crack (`ExplosionBigClose*.ogg`) | 124 | Volume on `Distance`, reversed: full at 100 m and below, 0 at 1000 m |
| Far report (`ExplosionBigFar*.wav`) | 124 | Volume on `Distance`: 0 at 100 m, full at 1000 m; low-pass reversed on `Distance`; high-pass on `Distance` |
| Echo (`ExplosionEcho*.ogg`, sound group `ExplosionEcho`, 2 channels) | 123 | Volume on `Pressure`: 0.25 at 0 atm up to 1 at 0.05 atm |

The engine's path: `ExplosionSystem.SpawnPresetInternal` spawns the flash and then
`ExplosionSoundSystem.Spawn(anchor, sound, intensity)` for the preset's `Sound`.[current/decomp/KSA/ExplosionSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSystem.cs#L253-L257) `Spawn` refuses beyond 50 km
(`MaxAudibleDistanceMeters`) or when 64 sounds are already tracked, plays through
`GameAudio.PlaySound(SoundEvent ...)` (so also refused above 10x), sets the spatial data, `Distance`, `Pressure` and
`Intensity` on the paused channel, unpauses it, and then re-places and re-parametrises it every frame in
`ExplosionSoundSystem.UpdateAudio`, which `GameAudio.UpdateAudio` calls.[current/decomp/KSA/ExplosionSoundSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ExplosionSoundSystem.cs#L37-L108)[^ksa-explosionsoundsystem]
The preset-to-sound link is a field of `ExplosionReference` (`[XmlElement("SoundEvent")] public SoundEvent? Sound`).[current/decomp/KSA.Rendering.Particles/ExplosionReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Particles/ExplosionReference.cs#L21-L22)[^ksa-rendering-particles-explosionreference]
If a preset has no `SoundEvent`, the engine plays nothing for it.

`MultiSound.Play` plays every layer and wraps the channels in one `MultiChannelWrapper`; its `SetSpatialAudio`,
`SetParameter`, `PitchMultiplier` and `SetPaused` forward to all of them.[current/decomp/KSA/MultiSound.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MultiSound.cs#L51-L63)[current/decomp/KSA/MultiChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MultiChannelWrapper.cs#L19-L86)[^ksa-multisound][^ksa-multichannelwrapper]

## Lifecycle and timing

| Moment | Where | Vocabulary |
|---|---|---|
| Presets and sounds registered | the mod's particles XML and Core's `ExplosionAssets.xml` load as assets | `startup:asset-load` |
| Start and per-frame re-place | `BurstSound.Update` at the tail of the mod's one-per-frame step: `[StarMapAfterGui]` normally, the `OnFrameCelestials` prefix when the HUD is hidden, `[StarMapAfterOnFrame]` as the last resort | `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` |
| Parameters consumed | `GameAudio.UpdateAudio` in `PostRender` | `frame:post-render` |
| Cleared | world reload: `ForgetTheWorld` -> `BurstSound.Clear()`, mirroring `Universe.DeserializeSave`'s `ExplosionSoundSystem.StopAll()` | `event:load` |

The step runs before the render, so a bang started this frame has its position and parameters in place when
`PostRender` calls FMOD.[src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L155-L201) See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Copy the preset without its sound.** A mod-declared `<Explosion>` can be a copy of a Core preset with
   `<SoundEvent>` left out, which is how to get Core's flash with no automatic bang. KSArmory's `KSArmoryNuclearBurst`
   keeps Core's flash and carries neither a `SoundEvent` nor a `VolumeRef` (the latter for an unrelated cloud
   reason):

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

   (The mod's own block comment above this element speaks of keeping the fireball volume and shockwave; the element
   itself contains only the flash.)

2. **Hash the three inputs and know what an unset one does.** Left unset they read zero: the close crack is at full at
   every range (reversed `Distance` at 0), the far report is silent at every range, and the echo sits at its 0.25 floor:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L46-L51
       // The parameters Core's explosion sounds are mixed on, set exactly as ExplosionSoundSystem sets
       // them. Left unset they read zero: the far report -- the rumble -- is silent at every range, the
       // close crack is at full at every range, and the echo sits at its floor.
       private static readonly KeyHash DistanceHash = KeyHash.Make("Distance".AsSpan());
       private static readonly KeyHash PressureHash = KeyHash.Make("Pressure".AsSpan());
       private static readonly KeyHash IntensityHash = KeyHash.Make("Intensity".AsSpan());
   ```

   In this build no `<Param>` in Core's XML reads `Intensity`; the engine sets it and so does the mod, but it has no
   audible effect today.

3. **Play it paused, pitch it, place it, parametrise it, unpause.** `SoundBehavior.Play` is called directly (so the
   engine's 10x and 50 km gates do not apply), with `startPaused: true`, and everything is set before the first sample
   is let go:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L292-L315
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
   ```

   `Apply` is the per-frame update shared with step 4:

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L284-L290
       private static void Apply(IChannel channel, SpatialAudio spatial)
       {
           channel.SetSpatialAudio(spatial);
           channel.SetParameter(DistanceHash, (float)spatial.Distance());
           channel.SetParameter(PressureHash, (float)spatial.AtmosphericPressure);
           channel.SetParameter(IntensityHash, 1f);
       }
   ```

   Pitch is `cbrt(0.3 kt / yield)` clamped to 0.35..1, so a bigger burst is slower and deeper; `MultiChannelWrapper`
   hands the multiplier to all three layers. Because the sound is played directly, `channel.SubscribedClusterSound` is
   always null here (only `PlaySound(SoundEvent ...)` attaches a cluster); the cluster test is a copy of the engine's.

4. **Keep re-placing it while it plays.** A thirty-second sound is heard from wherever the camera has got to, so every
   frame the mod rebuilds the spatial data from the burst's ground anchor and re-applies it, dropping the entry when
   `GameAudio.IsStillActive(channel)` is false (the public helper the engine uses: playing or virtual):

   ```csharp
   // src/KSArmory/Ksa/BurstSound.cs L182-L200
           for (int i = _playing.Count - 1; i >= 0; i--)
           {
               (IChannel channel, double3 anchor, object? body) = _playing[i];

               try
               {
                   if (!GameAudio.IsStillActive(channel) || !TrySpatial(body, anchor, out SpatialAudio spatial))
                   {
                       _playing.RemoveAt(i);
                       continue;
                   }

                   Apply(channel, spatial);
               }
               catch
               {
                   _playing.RemoveAt(i);
               }
           }
   ```

   The position is `atEcl - camera.PositionEcl` (Ego is a pure translation of Ecl) with the pressure at the camera;
   see `TrySpatial` at L266-L282 and [approach 2 of the world-position topic](../play-a-sound-at-a-world-position-and-hold-its-channel/play-a-sound-at-a-world-position-and-hold-its-channel-2.md).

5. **Clear on world reload.** `BurstSound.Clear()` stops every playing bang with `Stop(stopImmediate: true)`, as
   `Universe.DeserializeSave` does for the engine's own (`ExplosionSoundSystem.StopAll()`).[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2446)[^ksa-universe]

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs) | Fronts, `Play`, `Apply`, `Update`, `Clear` |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L522-L556) | The preset with no `SoundEvent` |
| [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L457-L495) | `BangAnchorKt`, `BangPitchFloor`, `BangPitch` |

## Variations and gotchas

- **Gates you now own.** `GameAudio.PlaySound(SoundEvent ...)` refuses above 10x; `ExplosionSoundSystem` refuses beyond
  50 km and caps tracked sounds at 64, stealing the quietest. Playing directly gives none of this: the bang still
  starts above 10x, and each of its `ChannelWrapper`s is stopped by `StopForSimSpeed` unless its `<Sound>` sets
  `SimSpeed="Ignore"`, which Core's explosion layers do not. See
  [keep mod sounds sensible under time warp](../../channels/keep-mod-sounds-sensible-under-time-warp/overview.md).
- **The echo layer is long.** Core's echoes run 10.6 to 10.8 s at pitch 1; at the mod's 0.35 pitch floor the echo rolls
  for about 30 s, which is why the channel is re-placed every frame.
- **Voice priority.** The layers carry priorities 123 and 124; FMOD treats a lower number as more important, so the 110 KSArmory gives its guns outranks them; see
  [ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md).
- **Source velocity convention.** The engine's `ExplosionSoundSystem` passes a velocity relative to the object the camera
  follows (`Camera.GetVelocityEgo` plus the anchor's own local velocity); KSArmory's `TrySpatial` passes the ground's
  absolute ecliptic velocity instead. With a Doppler scale of 1 in this build (`Set3dSettings(1f, 1f, 0.1f)`), the
  difference may colour the pitch of a late bang; the mod's comment calls it far below what anyone could place, and it was
  not measured here (unverified).
- **Volume mapping.** The engine's volume mapping from explosion energy (`VolumeForIntensity`) is not part of
  the sound; the mod supplies its own `loudness` as the `volume` argument.
- **Ids can disappear.** `ExplosionBig` is Core content; a renamed id leaves `ModLibrary.Get` throwing, which `Play`
  catches and logs once (`the bang could not be played`).

## See also

- [Approach 1: engine parameters mirrored from Vehicle.UpdateAudio](drive-sound-parameters-from-code-1.md)
- [Approach 3: stock engine loop for a projectile with Throttle pinned open](drive-sound-parameters-from-code-3.md)
- [Overview of this topic](overview.md)
- Mod feature: [burst sound at blast arrival](../../../../mods/ksarmory/features/burst-sound-at-blast-arrival.md)
- [Delay a world sound by the time sound takes to reach the listener](../delay-a-sound-until-the-blast-front-arrives/overview.md)
- [Spawn particle effects](../../../rendering/lights-and-particles/spawn-particle-effects/overview.md)

[^ksa-core-explosionassets]: Core/ExplosionAssets.xml (build 2026.10.10.5554)
[^ksa-explosionsoundsystem]: KSA.ExplosionSoundSystem (decompiled, build 2026.10.10.5554)
[^ksa-rendering-particles-explosionreference]: KSA.Rendering.Particles.ExplosionReference (decompiled, build 2026.10.10.5554)
[^ksa-multisound]: KSA.MultiSound (decompiled, build 2026.10.10.5554)
[^ksa-multichannelwrapper]: KSA.MultiChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
