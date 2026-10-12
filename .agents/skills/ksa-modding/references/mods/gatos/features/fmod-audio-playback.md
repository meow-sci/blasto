---
type: Mod Feature
title: FMOD audio playback
description: Play user-uploaded audio clips through KSA's own FMOD system and channel groups by creating in-memory sounds, tracking channels on the main thread, and releasing sounds only after no channel uses them, without ever touching the game's System lifecycle
mod: gatos
category: audio
techniques: [fmod-audio, command-queue]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame, frame:post-render, thread:background]
game_types:
  - KSA.GameAudio
  - Brutal.FmodApi.FmodSystem
  - Brutal.FmodApi.Sound
  - Brutal.FmodApi.Channel
  - KSA.ChannelGroupType
tags: [audio, fmod, sound, channels, uploads, main-thread]
---

`/sim/audio` lets a guest program or HTTP client upload a sound file (`cat boom.ogg >
/sim/audio/file/boom.ogg` or `PUT /v1/audio/file/boom.ogg`) and play it with volume, pitch, loop,
start and end offsets on the game's SFX, music or UI channel groups, so it mixes with game audio and
respects the player's volume sliders. The actuator is a compact reference for adding sounds to KSA
from code without shipping sound XML.

## How it works

- **Upload** happens on transport threads into the game-free `AudioStore` (in-memory bytes with
  per-clip, total and count caps, and a version per clip).
- **Create** ([gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs#L156-L170)): on first play,
  `GameAudio.System.TryCreateSound(bytes, Mode.OpenMemory | Mode._2d | CreateSample-or-CreateCompressedSample, in exInfo{Length}, out sound)`.
  FMOD copies the bytes at create, so re-uploading or evicting the clip never disturbs a playing
  channel. Clips up to 1 MiB are decoded fully (`CreateSample`); larger ones stay compressed
  (`CreateCompressedSample`) so create stays well inside the command timeout and one sound can still
  play on several channels (a stream could not). Sounds are cached by `(name, version)`.
- **Play** (`Play`, from L66): `GameAudio.System.TryPlaySound(sound, GameAudio.GetChannelGroup(group), paused: true, out channel)`,
  then set position, loop mode and count, volume and pitch, then unpause, so the first audible sample
  already has the right settings.
- **Tick** ([gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs#L278-L320)): prune channels that
  stopped, enforce `end=` by position, release cached sounds whose clip version was evicted and that no
  channel still plays, publish `/sim/audio/status`, and queue `audio.finished` events for the
  sampler. It self-gates to nothing when no channel or cached sound exists.
- **Never** call `System.Update`, `Close` or `Release`: the game owns the FMOD system.

## Lifecycle and timing

- `audio.play/set/stop` run in the Frame-phase command drain, which with the default camera hook is
  the main-viewport `GameViewport.OnFrame` prefix (`frame:viewports`). `DriveAudio()` runs later, in
  `DrivePerFrame` from `StarMapBeforeGui` (`frame:draw-ui`), or from `StarMapAfterOnFrame` on
  hidden-HUD frames. The code comment "right after the drain" only holds when the viewport hook is
  absent and `DrivePerFrame` does the drain itself. All of it is on the main thread.
- KSA pumps FMOD with `GameAudio.UpdateAudio(dtPlayer)` -> `System.Update()` in `Program.PostRender`
  later the same frame ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2336-L2364)), so commands issued in the
  drain are processed by FMOD within the frame and from the same thread. On hidden-HUD frames the
  tick runs after `PostRender`, so its `end=` enforcement and channel pruning reach FMOD one update later.
- Uploads and status reads are on transport threads (`thread:background`) and touch only the store.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs) | FMOD sound cache, channel table, tick, teardown |
| [gatOS.SimFs/Audio/AudioStore.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Audio/AudioStore.cs) | game-free clip store with caps and versions |
| [gatOS.SimFs/Audio/AudioCommands.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Audio/AudioCommands.cs) | `play`/`set`/`stop` grammar |
| [gatOS.SimFs/Audio/AudioDirectory.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Audio/AudioDirectory.cs) | writable upload directory over 9p |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L465-L478) | `DriveAudio` with fault latch |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/AudioActuator.cs L156-L170
    private Sound? CreateOrGetSound(AudioClip clip)
    {
        var key = (clip.Name, clip.Version);
        if (_sounds.TryGetValue(key, out var cached))
            return cached;

        var mode = Mode.OpenMemory | Mode._2d
                   | (clip.Bytes.Length <= CreateSampleMaxBytes ? Mode.CreateSample : Mode.CreateCompressedSample);
        var exInfo = new CreateSoundExInfo { Length = (uint)clip.Bytes.Length };
        if (GameAudio.System.TryCreateSound(clip.Bytes, mode, in exInfo, out var sound) != Result.Ok)
            return null;

        _sounds[key] = sound;
        return sound;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GameAudio.System` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L55-L55) | the game's FMOD system |
| `GameAudio.GetChannelGroup(ChannelGroupType)` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L660-L670) | SFX, music, UI groups |
| `GameAudio.UpdateAudio` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L150-L155) | where FMOD is pumped |
| `GameAudio.CreateFmodSound` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L270-L280) | the game's own in-memory sound recipe gatOS mirrors |

## Pitfalls and version notes

- Releasing a `Sound` while a channel plays it cuts the channel audibly; release only when no live
  channel references it.
- Use the game's channel groups so the player's volume settings apply.
- `CreateStream` sounds cannot play concurrently on several channels.
- In-game validation for `/sim/audio` is listed as pending in the repo.

## See also

- [Game-thread command queue](game-thread-command-queue.md)
- [HTTP /v1 API server](http-v1-api-server.md)
- [Snapshot-diff flight events](snapshot-diff-flight-events.md)
- [External API clients and SDK](external-api-clients.md)
- [gatOS overview](../overview.md)
- [Play in-memory sounds with a channel table and reference-counted release](../../../howto/audio/channels/play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-1.md) - how-to article demonstrating this feature
- [Release FMOD sounds only after no channel plays them](../../../howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-4.md) - how-to article demonstrating this feature

