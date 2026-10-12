---
type: How-To
title: Declare a SimSpeed rule and let the engine handle time warp
description: Choose a SimSpeedRule per sound in XML, call GameAudio.PlaySound, handle the missing channel above 10x, and know when SimSpeed Ignore plus alwaysPlay is needed to keep a sound playing
category: audio
group: channels
topic: keep-mod-sounds-sensible-under-time-warp
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/world-positioned-sounds]
techniques: [fmod-audio, xml-asset]
hooks: [starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui, frame:post-render]
game_types:
  - KSA.GameAudio
  - KSA.ChannelWrapper
  - KSA.SimSpeedRule
  - KSA.SoundReference
  - KSA.ChannelGroupType
  - KSA.Universe
tags: [audio, time-warp, simspeed, xml, alwaysplay]
---

Outcome: one-shot sounds that slow down with a slowed world, never speed up beyond what you chose, and are handled
correctly above 10x. Prerequisites: declared sounds ([ship custom sounds as XML assets](../../sound-assets/ship-custom-sounds-as-xml-assets/ship-custom-sounds-as-xml-assets-1.md))
and a per-frame play call. Demonstrated by KSAGolf, whose putts and drops use `SimSpeed="OnlyLower"`.

## Background: how the game does it

The rule lives on the `Sound` (`SoundReference.SimSpeedRule`, default `All`)[^ksa-soundreference][^ksa-simspeedrule] and is read by the engine every frame for
every active channel: `HandleSimSpeedPitch` scales pitch, `StopForSimSpeed` stops the channel above 10x unless the
rule is `Ignore`. A call through `GameAudio.PlaySound(SoundBehavior, SpatialAudio, ...)` is also refused above 10x
unless the caller passes `alwaysPlay`.[^ksa-gameaudio][^ksa-channelwrapper] The table of factors per rule is in the
[overview](overview.md).

## Lifecycle and timing

| Moment | What happens | Vocabulary |
|---|---|---|
| Asset load | `SimSpeed` is parsed into `SoundReference.SimSpeedRule` | `startup:asset-load` |
| The mod's play call | `GameAudio.PlaySound` checks `Universe.SimulationSpeed` right then | `frame:draw-ui` (KSAGolf plays from its per-frame step, usually the `StarMapAfterGui` postfix) |
| Every frame while playing | `HandleSimSpeedPitch` and `StopForSimSpeed` run in `GameAudio.UpdateAudio` | `frame:post-render` |

The gate is evaluated once, at the call; the pitch and the stop are evaluated every frame. See the
[game loop](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the rule per sound.**

   | If the sound is... | Use | Effect |
   |---|---|---|
   | an impact or footstep that should drag in slow motion | `OnlyLower` | pitch follows speed below 1x (floor 0.25), 1 above; stopped above 10x |
   | a rocket-like sound that should rise with warp | `All` (default) | pitch rises to 3x by 10x, falls to 0.25x; stopped above 10x |
   | UI, music, narration | `Ignore` | pitch never changed; never stopped by warp |

2. **Declare it.** KSAGolf's four sounds all say `SimSpeed="OnlyLower"`; Core's UI click says `SimSpeed="Ignore"`:

   ```xml
   <!-- src/KSAGolf/KSAGolfSounds.xml L5-L5 -->
   <Sound Id="KSAGolf_PuttHard" ChannelId="ExternalChannel" SimSpeed="OnlyLower">
   ```

   ```xml
   <!-- current/Content/Core/Sounds.xml L93-L95 -->
   <Sound Id="LedButtonDown" SimSpeed="Ignore" ChannelId="Ui">
       <SoundFile Path="Sounds/LedButtonDown.wav" TwoD="true" />
   </Sound>
   ```

3. **Play through `GameAudio.PlaySound` and handle a null channel.** Above 10x the call returns without starting
   anything, so `channel` is null; KSAGolf records that in its status string instead of treating it as a failure:

   ```csharp
   // src/KSAGolf/Ksa/GolfSounds.cs L24-L25
   GameAudio.PlaySound(behavior, new SpatialAudio(near, atEcl - KsaWorld.PositionEcl(near)), out IChannel? channel, sound.Volume);
   LastPlayed = $"{sound.Id} at {sound.Volume:F2}{(channel is null ? ", no channel" : "")}";
   ```

4. **To keep a sound playing at any speed, set both knobs.** `SimSpeed="Ignore"` stops `StopForSimSpeed` from killing
   the channel, and `alwaysPlay: true` stops the entry gate from refusing the call:

   ```csharp
   // adapted from KSAGolf/src/KSAGolf/Ksa/GolfSounds.cs L24 (alwaysPlay added; the sound must declare SimSpeed="Ignore")
   GameAudio.PlaySound(behavior, spatial, out IChannel? channel, volume, startPaused: false, alwaysPlay: true);
   ```

   Core's music playlists take the same stance with `SimSpeed="Ignore"`. With `alwaysPlay` and a rule other than
   `Ignore`, the channel is created and then stopped at the next `UpdateAudio`.

5. **Watch it work.** The game's **Show Audio Debug** window lists every playing channel registered with the engine;
   change the simulation speed and watch the sound appear and disappear around 10x.[^ksa-gameaudio]

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/KSAGolfSounds.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfSounds.xml) | Four sounds, each `SimSpeed="OnlyLower"` |
| [src/KSAGolf/Ksa/GolfSounds.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GolfSounds.cs) | Play call that tolerates "no channel" |

## Variations and gotchas

- **Quiet failure.** Above 10x nothing plays and nothing is logged; the only trace is the null channel.
- **Mostly a long-sound problem.** The rule matters most for loops and for sounds that are playing when the speed
  changes; a short one-shot is usually over before it does.
- **`OnlyLower` stops above 10x too.** It only declines to raise pitch; it does not exempt the sound from the stop.
  Only `Ignore` does.
- **Speed zero.** A paused world (`Universe.IsPaused()`) sets the sim-pitch factor to 0 for every rule but `Ignore`,
  so a playing loop halts in place (pitch 0).
- **The `SoundEvent` overload has no `alwaysPlay`.** The declarative route (`<SoundEvent SoundId=...>`) is always
  refused above 10x.[^ksa-gameaudio]

## See also

- [Overview of this topic](overview.md)
- [Approach 2: cut your own loops below the engine's threshold](keep-mod-sounds-sensible-under-time-warp-2.md)
- [Approach 3: channels the game never registers](keep-mod-sounds-sensible-under-time-warp-3.md)
- [Ship custom sounds with a mod as XML sound assets](../../sound-assets/ship-custom-sounds-as-xml-assets/overview.md)
- [Set the simulation speed from a mod](../../../flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/overview.md)
- Mod feature: [world-positioned sounds](../../../../mods/ksagolf/features/world-positioned-sounds.md)

[^ksa-soundreference]: KSA.SoundReference (decompiled, build 2026.10.10.5554)
[^ksa-simspeedrule]: KSA.SimSpeedRule (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
