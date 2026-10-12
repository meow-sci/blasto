---
type: Mod Feature
title: Play imported sound files in 3D at a vessel through nonblocking FMOD streams on the game's Sfx group
description: BYO Music plays imported OGG, WAV or MP3 files as nonblocking 3D FMOD streams on KSA's own system and Sfx group, positioned at a vessel each frame through SpatialAudio, with loops, real-time gaps and scene saves
mod: unscience
submod: byo-music
category: audio
techniques: [fmod-audio, imgui-window, save-sidecar]
hooks:
  - starmap StarMapBeforeGui (ByoMusicSubmod.Update ticks every VesselSound; hidden-HUD replay in unscience)
  - starmap StarMapAfterGui (panel in ISubmod.RenderContent, import browser in RenderFloatingWindows)
  - unscience save participant byo-music (order 220) via the shared native save hooks
lifecycle: [frame:draw-ui, frame:post-render, event:save, event:load, frame:prepare]
game_types:
  - KSA.GameAudio
  - KSA.SpatialAudio
  - KSA.ChannelGroupReference
  - KSA.ChannelWrapper
  - KSA.ModLibrary
  - KSA.MusicPlayList
  - KSA.Vehicle
  - KSA.Program
  - Brutal.FmodApi.FmodSystem
  - Brutal.FmodApi.Sound
  - Brutal.FmodApi.Channel
  - Brutal.FmodApi.Mode
  - Brutal.FmodApi.OpenState
  - Brutal.FmodApi.Result
tags: [audio, fmod, 3d-audio, streaming, spatial-audio, sfx, repeat, imgui, scene-saves]
---

BYO Music ("bring your own") attaches any imported sound to any vessel in 3D. Import an Ogg Vorbis,
WAV or MP3 file, pick it and a vessel from filterable lists, set volume and audible range, and press
"Play on vessel": the sound comes from that vessel, falls off with distance from the camera, keeps a
stable pitch, plays in space and during time warp, and follows KSA's SFX and master volume. "Repeat"
with a zero gap loops seamlessly; a positive gap waits that many real seconds after each completed
play. Several vessels and files can play at once, every entry can be paused, edited or stopped live,
and a vessel that disappears stops its sounds.[^byo-lib-readme][^byo-submod]

Mechanism in one line: each `VesselSound` owns one FMOD stream created with
`Mode._3d | Mode.NonBlocking | Mode.LoopOff` on the game's own `GameAudio.System`, polls its open state
each frame instead of blocking, plays it paused on the `Sfx` channel group, sets 3D attributes from
`new SpatialAudio(vessel)` before unpausing, and repeats that positioning every frame.[^vessel-sound][^ksa-gameaudio]

## How it works

1. **Library.** Files are copied into the shared `sounds` catalog (`.ogg`, `.wav`, `.mp3`) under
   the unscience data folder by the shared import browser, which BYO Music opens as a floating window;
   the list is rescanned every two seconds in `Update`. The catalog and browser are documented in
   [Shared copied-import media catalogs](./lib-shared-file-library.md).[^sound-library][^byo-submod]
2. **Play** ([byo-music.lib/ByoMusicSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/ByoMusicSubmod.cs#L70-L84)).
   Any existing entry for the same file on the same vessel is stopped and removed (so gain does not
   double), then a new `VesselSound(target, file, repeat, gap, volume, range)` is added.
3. **Open** ([byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs#L30-L47)).
   The constructor checks the vessel, the catalog path and extension and that `GameAudio.System` is
   not null, then calls `FmodSystem.TryCreateStream` with a NUL-terminated UTF-8 path. KSA installed
   its own FMOD file callbacks at startup, and its open callback simply `File.OpenRead`s the name, so
   an absolute path works.[^ksa-gameaudio]
4. **Decode without blocking.** Each `Update` asks `Sound.TryGetOpenState`; `Error` aborts with a
   "use PCM WAV, MP3 or Ogg Vorbis" message, anything but `Ready` waits for the next frame, and 15
   seconds without readiness is a timeout. Stock `GameAudio.CreateFmodSound` instead loops on
   `TryGetOpenState` with `Thread.Sleep(1)` for streamed sounds.[^vessel-sound][^ksa-gameaudio]
5. **Start** (`Start`). `ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup` is the group KSA
   created and registered at audio start-up; `TryPlaySound(sound, group, paused: true, out channel)`,
   then `ApplySettings()`, then `TrySetPaused(false)`, so the first audible sample is already at the
   vessel instead of at the listener origin.[^ksa-channelgroupreference]
6. **Every frame while playing** (`ApplySettings`). `Mode._3d | Mode._3dLinearRolloff` plus
   `LoopNormal` with loop count -1 when repeating with no gap, else `LoopOff` and 0; volume; 3D
   min/max distance `(min(10, range * 0.1), range)`; `TrySet3dAttributes(spatial.PositionView(),
   spatial.VelocityView())`; spread 0 so stereo files act as a point source; Doppler level 0 so pitch
   stays stable.[^vessel-sound]
7. **Repeat scheduler** ([byo-music.lib/RepeatPlayback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/RepeatPlayback.cs)).
   `Advance(dt, playing, repeat, gap)` returns `Play` on the first call, `None` while playing,
   `Finish` when a play ended and repeat is off, and otherwise counts down the gap from the moment
   completion was first observed. Editing the gap mid-wait keeps the silence already elapsed, and a
   NaN or negative `dt` is ignored. A zero gap with repeat never reaches it, because FMOD loops
   natively.[^repeat-playback][^byo-tests]
8. **Stop.** `TryStop` on the channel, `TryRelease` on the sound, `Finished = true`. This happens on
   Stop, Stop all, a finished one-shot, any error, target loss (disposed, or
   `VehicleProvider.FindVehicle(id)` no longer returns the same object) and `Dispose`.[^vessel-sound]
9. **Scene saves** ([byo-music.lib/ByoMusicSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/ByoMusicSubmod.Saves.cs)).
   Participant `byo-music`, order 220. Capture stores (vehicle id, file name, repeat, gap, volume,
   range) for every unfinished sound whose vessel is alive. Reset is `Dispose` (all streams stopped
   and released before KSA replaces the vehicles). Restore re-creates each sound paused, so Resume
   starts it from the beginning; missing vessels or files become warnings. The playback cursor and
   the remaining gap are not saved.[^byo-saves]

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Decode poll, repeat scheduler, 3D attributes, target checks | `ByoMusicSubmod.Update` from `[StarMapBeforeGui]` (prefix of `OnDrawUiFrame`, `Program.cs` L2267); in unscience also replayed while the HUD is hidden | `frame:draw-ui` |
| Panel, Play, Stop, live edits | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| FMOD mixes and applies the new attributes | `GameAudio.UpdateAudio` -> `System.Update()`, called from `PostRender` (L2362) | `frame:post-render` |
| Capture / reset and restore | Native save; native load at the `PrepareFrame` handoff | `event:save`, `event:load`, `frame:prepare` |

Why this point works: the main camera for this frame has already been updated in
`OnFrameViewports` (L2263), so `SpatialAudio`, which measures the vessel relative to
`GameAudio.GetAudioCamera()` (`Program.MainViewport.BaseCamera`) and rotates into view space, uses
current camera state; FMOD then commits it in the same frame's `System.Update()`. KSA never sets
FMOD listener attributes, it places every source in camera view space, and BYO Music follows the
same convention as `ChannelWrapper.SetSpatialAudio`.[^ksa-spatialaudio][^ksa-channelwrapper][^ksa-program]

Real time: the `dt` handed to the GUI hooks is `dtPlayer`, so gaps count wall-clock seconds and
continue while paused or warping. These channels are not registered in `GameAudio`'s active channel
list, so KSA's per-channel simulation-speed pitch and stop handling (`HandleSimSpeedPitch`,
`StopForSimSpeed`) never touch them.[^ksa-gameaudio]

## Key code

| Path | Role |
|---|---|
| [byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs) | Stream ownership, decode polling, start, 3D settings, stop |
| [byo-music.lib/RepeatPlayback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/RepeatPlayback.cs) | Completion-based gap scheduler |
| [byo-music.lib/ByoMusicSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/ByoMusicSubmod.cs) | Panel, pickers, live entries, rescan |
| [byo-music.lib/ByoMusicSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/ByoMusicSubmod.Saves.cs) | Save participant |
| [ksa-abstractions.lib/SoundLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SoundLibrary.cs) | The `sounds` catalog definition |
| [byo-music.lib/MusicPlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/MusicPlayer.cs) | Legacy playlist helper (not used by the panel) |
| [byo-music/Assets.xml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/Assets.xml) | Development-host demo `MultiSound` and `MusicPlaylist` |
| [byo-music.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.tests/Program.cs) | Managed checks for the scheduler and the copied catalog |

## Snippets

Opening a stream without blocking the game thread:

```csharp
// byo-music.lib/VesselSound.cs L30-L47
    public VesselSound(Vehicle target, string fileName, bool repeat, float gap, float volume, float range, bool paused = false)
    {
        if (target.IsDisposed) throw new InvalidOperationException("That vessel is no longer available.");
        string path = SoundLibrary.Files.FullPath(fileName);
        if (!SoundLibrary.Files.Supports(path) || !File.Exists(path)) throw new IOException("Choose an available OGG, WAV or MP3 file.");
        if (GameAudio.System.IsNull()) throw new InvalidOperationException("The game's audio system is not ready.");
        Paused = paused;
        Status = paused ? "Restored — paused; press Resume to play from the beginning." : "Loading…";
        Target = target;
        FileName = fileName;
        Repeat = repeat;
        GapSeconds = gap;
        Volume = volume;
        RangeMetres = range;
        byte[] name = Encoding.UTF8.GetBytes(path + "\0");
        Check(GameAudio.System.TryCreateStream(name.AsSpan(), Mode._3d | Mode.NonBlocking | Mode.LoopOff,
            new CreateSoundExInfo(), out _sound), "open audio file");
    }
```

Per-frame tick after the settings are clamped:

```csharp
// byo-music.lib/VesselSound.cs L61-L95
            if (!_ready)
            {
                Check(_sound.TryGetOpenState(out var state, out _, out _, out _), "decode audio file");
                if (state == OpenState.Error) throw new InvalidOperationException("The file could not be decoded. Use PCM WAV, MP3, or Ogg Vorbis.");
                if (state != OpenState.Ready)
                {
                    if (_loading.Elapsed.TotalSeconds > 15) throw new TimeoutException("Audio loading timed out.");
                    return;
                }
                _ready = true;
            }
            bool playing = false;
            if (_hasChannel)
            {
                Result result = _channel.TryIsPlaying(out var isPlaying);
                playing = result == Result.Ok && isPlaying;
                if (result != Result.Ok && result != Result.ErrInvalidHandle && result != Result.ErrChannelStolen)
                    Check(result, "check playback");
            }
            switch (_repeat.Advance(dt, playing, Repeat, GapSeconds))
            {
                case PlaybackAction.Play:
                    Start();
                    playing = true;
                    break;
                case PlaybackAction.Finish:
                    Stop("Finished");
                    return;
            }
            if (playing)
            {
                ApplySettings();
                Status = Repeat && GapSeconds == 0 ? "Looping" : "Playing";
            }
            else Status = $"Next play in {_repeat.Remaining:0.0}s";
```

Start paused on the game's Sfx group, then place it at the vessel:

```csharp
// byo-music.lib/VesselSound.cs L104-L125
    private void Start()
    {
        var group = ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup;
        Check(GameAudio.System.TryPlaySound(_sound, group, true, out _channel), "play sound");
        _hasChannel = true;
        ApplySettings(); // Configure position and gain while paused: no one-frame sound at the origin.
        Check(_channel.TrySetPaused(false), "start sound");
    }

    private void ApplySettings()
    {
        bool continuous = Repeat && GapSeconds == 0;
        Check(_channel.TrySetMode(Mode._3d | Mode._3dLinearRolloff | (continuous ? Mode.LoopNormal : Mode.LoopOff)), "set repeat");
        Check(_channel.TrySetLoopCount(continuous ? -1 : 0), "set repeat count");
        Check(_channel.TrySetVolume(Volume), "set volume");
        Check(_channel.TrySet3dMinMaxDistance(Math.Min(10, RangeMetres * .1f), RangeMetres), "set audible range");
        // Match stock SpatialAudio's camera-relative positions. Keep music pitch stable in warp.
        var spatial = new SpatialAudio(Target);
        Check(_channel.TrySet3dAttributes(spatial.PositionView(), spatial.VelocityView()), "follow vessel");
        Check(_channel.TrySet3dSpread(0), "position stereo files as a point source");
        Check(_channel.TrySet3dDopplerLevel(0), "keep playback pitch stable");
    }
```

Completion-based gaps:

```csharp
// byo-music.lib/RepeatPlayback.cs L7-L29
internal sealed class RepeatPlayback
{
    private bool _started;
    private bool _waiting;
    private double _lastGap;
    public double Remaining { get; private set; }
    public PlaybackAction Advance(double dt, bool playing, bool repeat, double gap)
    {
        if (!_started) { _started = true; return PlaybackAction.Play; }
        if (playing) { _waiting = false; return PlaybackAction.None; }
        if (!repeat) return PlaybackAction.Finish;
        if (!_waiting) { _waiting = true; Remaining = Math.Max(0, gap); }
        else
        {
            // Editing the gap while waiting keeps elapsed silence rather than restarting it.
            Remaining += gap - _lastGap;
            if (double.IsFinite(dt) && dt > 0) Remaining -= dt;
        }
        _lastGap = gap;
        if (Remaining > 0) return PlaybackAction.None;
        _waiting = false;
        return PlaybackAction.Play;
    }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `GameAudio.System`, `GetAudioCamera`, `OnApplicationStart`, `UpdateAudio`, `CreateFmodSound`, `OpenStream` | [current/decomp/KSA/GameAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameAudio.cs#L55-L320) | The game's FMOD system (right-handed 3D, custom file callbacks), Sfx group registration, per-frame update |
| `SpatialAudio(Astronomical)`, `PositionView`, `VelocityView` | [current/decomp/KSA/SpatialAudio.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SpatialAudio.cs#L36-L63) | Camera-relative position and velocity in view space |
| `ChannelWrapper.SetSpatialAudio`, `HandleSimSpeedPitch`, `StopForSimSpeed` | [current/decomp/KSA/ChannelWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelWrapper.cs#L434-L497) | Stock pattern followed; stock behaviour bypassed |
| `ChannelGroupReference.ChannelGroup` | [current/decomp/KSA/ChannelGroupReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChannelGroupReference.cs#L26) | `Sfx` group looked up through `ModLibrary.Get` |
| `FmodSystem.TryCreateStream`, `TryPlaySound`; `Sound.TryGetOpenState`, `TryRelease`; `Channel.TrySetPaused`, `TryIsPlaying`, `TryStop`, `TrySetMode`, `TrySetLoopCount`, `TrySetVolume`, `TrySet3dMinMaxDistance`, `TrySet3dAttributes`, `TrySet3dSpread`, `TrySet3dDopplerLevel` | [current/decomp/Brutal.FmodApi/Fmod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.FmodApi/Fmod.cs#L4795) | Brutal's FMOD Core wrappers (`TryPlaySound` L4894, `TryGetOpenState` L6042, channel calls L1561-L2401) |
| `Mode._3d`, `NonBlocking`, `LoopOff`, `LoopNormal`, `_3dLinearRolloff` | [current/decomp/Brutal.FmodApi/Mode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.FmodApi/Mode.cs#L9-L29) | Stream and channel modes |
| `MusicPlayList.PlayMusic` | [current/decomp/KSA/MusicPlayList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MusicPlayList.cs#L21) | Legacy `MusicPlayer` helper only |
| `Program.OnFrame`, `PostRender` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2362) | Viewports, GUI and audio update order |

## Pitfalls and version notes

- **Use the game's FMOD system.** A second system would mean a second listener and output device;
  `GameAudio.System` already has 3D settings, the file callbacks and the volume groups.[^scope-audio]
- **Never spin on decode in a frame hook.** Poll `TryGetOpenState` once per frame, as here; the stock
  helper's `Thread.Sleep(1)` loop is acceptable at load time, not mid-frame.
- **Possible finish-path issue (static reading).** `Update` calls `TrySetPaused` through the strict
  `Check` before the tolerant `TryIsPlaying`. If FMOD has already invalidated a finished one-shot's
  channel handle (the reason `TryIsPlaying` tolerates `ErrInvalidHandle` and `ErrChannelStolen`),
  that call would turn a normal finish, or the start of a positive gap, into an error stop. Not
  verified in game; the authors list native repeat and stop behaviour as still needing an in-game
  audio pass.[^vessel-sound][^byo-lib-readme]
- **No atmospheric muffling** is applied, by design: sounds play in vacuum and in warp.[^scope-audio]
- **Extensions are not codecs.** A `.ogg` that is not Ogg Vorbis, or a compressed WAV, ends as a
  per-entry error rather than a stuck "Loading".[^byo-lib-readme]
- **Same file, same vessel replaces.** Different files or vessels play independently; there is no
  global cap on simultaneous streams in the mod.
- **Restores start paused from the beginning.** Exact cursor and remaining gap are not saved.[^byo-saves]
- **Demo asset is development-only.** `byo-music/Assets.xml` (a `MultiSound` and a `MusicPlaylist`
  with `LocationId="Sol"`) is loaded only by the standalone development host's `mod.toml`; the shipped
  `unscience/mod.toml` declares no assets, the referenced `Music/Sabotage.mp3` is git-ignored, and the
  panel does not use it.[^byo-assets][^byo-mod-toml]
- 5482 and 5438 checks found the whole audio surface byte-identical; no Harmony patch or reflection is
  involved, so typed compile errors are the update signal.[^scope-audio]

## See also

- [Shared copied-import media catalogs](./lib-shared-file-library.md) (the `sounds` catalog and file browser)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md) (keeps sounds following vessels with the HUD hidden)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [BYO Music sub-mod](../submods/byo-music.md) and the [mod overview](../overview.md)
- [Play channels the game never registers so warp rules do not touch them](../../../howto/audio/channels/keep-mod-sounds-sensible-under-time-warp/keep-mod-sounds-sensible-under-time-warp-3.md) - how-to article demonstrating this feature
- [Stream a file path on the Sfx group, opened non-blocking and polled each frame](../../../howto/audio/channels/play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-2.md) - how-to article demonstrating this feature
- [Attach a stream to a vessel with per-frame 3D attributes and a completion-based repeat scheduler](../../../howto/audio/channels/play-sound-files-through-the-games-fmod-system/play-sound-files-through-the-games-fmod-system-3.md) - how-to article demonstrating this feature
- [Dispose all streams on save reset before the vehicles are replaced](../../../howto/audio/channels/release-sound-channels-at-scene-change-and-exit/release-sound-channels-at-scene-change-and-exit-3.md) - how-to article demonstrating this feature
- [Declare a MusicPlaylist in a mod asset XML](../../../howto/audio/sound-assets/add-a-music-playlist-for-a-location/add-a-music-playlist-for-a-location.md) - how-to article demonstrating this feature
- [Read the sound type map through the features that use each type](../../../howto/audio/sound-assets/understand-the-games-sound-and-music-xml-types/understand-the-games-sound-and-music-xml-types.md) - how-to article demonstrating this feature

[^vessel-sound]: byo-music.lib/VesselSound.cs
[^repeat-playback]: byo-music.lib/RepeatPlayback.cs
[^byo-submod]: byo-music.lib/ByoMusicSubmod.cs
[^byo-saves]: byo-music.lib/ByoMusicSubmod.Saves.cs
[^sound-library]: ksa-abstractions.lib/SoundLibrary.cs
[^byo-lib-readme]: byo-music.lib/README.md
[^byo-assets]: byo-music/Assets.xml
[^byo-mod-toml]: byo-music/mod.toml
[^byo-tests]: byo-music.tests/Program.cs
[^scope-audio]: scope/audio.md
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-spatialaudio]: KSA.SpatialAudio (decompiled, build 2026.10.10.5554)
[^ksa-channelwrapper]: KSA.ChannelWrapper (decompiled, build 2026.10.10.5554)
[^ksa-channelgroupreference]: KSA.ChannelGroupReference (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
