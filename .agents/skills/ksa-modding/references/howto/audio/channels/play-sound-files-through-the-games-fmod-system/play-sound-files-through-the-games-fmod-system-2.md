---
type: How-To
title: Stream a file path on the Sfx group, opened non-blocking and polled each frame
description: Open a long audio file with TryCreateStream and Mode.NonBlocking, poll TryGetOpenState once per frame instead of blocking, play it paused on the game's Sfx group and release the stream when done
category: audio
group: channels
topic: play-sound-files-through-the-games-fmod-system
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/byo-music-vessel-3d-audio]
techniques: [fmod-audio]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:post-render]
game_types:
  - KSA.GameAudio
  - Brutal.FmodApi.FmodSystem
  - Brutal.FmodApi.Sound
  - Brutal.FmodApi.Channel
  - Brutal.FmodApi.Mode
  - Brutal.FmodApi.OpenState
  - Brutal.FmodApi.Result
  - KSA.ChannelGroupReference
  - KSA.ModLibrary
tags: [audio, fmod, streaming, nonblocking, open-state, sfx]
---

Outcome: play an arbitrary audio file (Ogg Vorbis, WAV or MP3) from an absolute path through the game's mixer
without loading it into memory and without stalling the frame while FMOD opens it. Prerequisite: a per-frame hook in
a StarMap mod. Demonstrated by BYO Music in the unscience mod, which imports files into a library folder and plays
them on vessels. This article covers the stream and open-state handling; the 3D placement is
[article 3](play-sound-files-through-the-games-fmod-system-3.md).

## Background: how the game does it

At start-up KSA installs its own FMOD file callbacks (`System.SetFileSystem`): open, close, read and seek on a
`FileStream`. The open callback reads the name FMOD passes as a UTF-8 string and calls `File.OpenRead` on it, so any
absolute path the process can read can be streamed:

```csharp
// current/decomp/KSA/GameAudio.cs L309-L320
private unsafe static Result OpenStream(byte* name, uint* filesize, nint* handle, nint userdata)
{
	string text = Marshal.PtrToStringUTF8((nint)name);
	if (text == null)
	{
		return Result.ErrFileNotFound;
	}
	FileStream fileStream = File.OpenRead(text);
	*filesize = (uint)fileStream.Length;
	*handle = GCHandle.ToIntPtr(GCHandle.Alloc(fileStream));
	return Result.Ok;
}
```

For its own streamed assets (`StreamFromDisk="true"` in XML) the game opens the stream and then **spins**:

```csharp
// current/decomp/KSA/GameAudio.cs L272-L292
if (soundReference.StreamFromDisk)
{
	int byteCount = Utf8.GetByteCount(soundReference.ModPath);
	Span<byte> span = stackalloc byte[byteCount + 1];
	span[byteCount] = 0;
	Utf8.GetBytes(soundReference.ModPath.AsSpan(), span);
	result = System.TryCreateStream(span, soundReference.Mode, new CreateSoundExInfo(), out sound);
	if (result == Result.Ok)
	{
		bool flag = false;
		while (!flag)
		{
			sound.TryGetOpenState(out var openState, out var _, out var _, out var _);
			if (openState == OpenState.Ready)
			{
				flag = true;
			}
			Thread.Sleep(1);
		}
	}
}
```

That busy-wait is acceptable on a loader thread at startup; in a frame hook it would stall the frame.[^ksa-gameaudio]
The mod instead creates the stream with `Mode.NonBlocking`, which returns at once with the sound in the `Loading`
state, and checks `OpenState` once per frame.[^brutal-fmod-openstate][^brutal-fmod-mode]

## Lifecycle and timing

The tick runs from `[StarMapBeforeGui]` (the prefix of `OnDrawUiFrame`, `frame:draw-ui`) on the main thread: the main
camera was updated earlier in the frame, and FMOD commits what the tick set in `System.Update()` at `frame:post-render`
in the same frame. With the HUD hidden (F2) `StarMapBeforeGui` does not fire. The shipped unscience host replays the same
update from a Harmony prefix on `Program.OnDrawUiConsole` (`HiddenUiFrameHook`, active only while `Program.DrawUI` is
false); the standalone `byo-music` host has no such fallback, so there the open-state poll and everything else in the tick
simply wait until the HUD returns (a stream already playing keeps playing). Nothing runs on a worker thread; FMOD's own
thread does the decoding. See
[the game loop](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve an absolute path and check it.** BYO Music's constructor refuses a missing file, an unsupported
   extension and a not-yet-created audio system (`GameAudio.System.IsNull()`), then opens the stream with a
   NUL-terminated UTF-8 name through `FmodSystem.TryCreateStream`:[^brutal-fmod]

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

   `Mode._3d | Mode.NonBlocking | Mode.LoopOff` is the stream mode; a 2D sound would use `Mode._2d`.

2. **Poll the open state once per frame until `Ready`.** `Error` is final, any other state means wait a frame, and a
   stopwatch gives up after 15 seconds:

   ```csharp
   // byo-music.lib/VesselSound.cs L61-L71
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
   ```

   The extension is not a codec check: a mislabelled `.ogg`, or a compressed WAV, ends here as `OpenState.Error`
   and a per-entry error message, not as a stuck "Loading".[^unscience-readme]

3. **Play paused on the game's group.** `ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup` is the group the
   game created and registered at audio start-up (`GameAudio.GetChannelGroup(ChannelGroupType.Sfx)` returns the same
   group).[^ksa-channelgroupreference] Playing there puts the stream under the player's Sfx and Master sliders. Start paused, configure, then
   unpause:

   ```csharp
   // byo-music.lib/VesselSound.cs L104-L111
   private void Start()
   {
       var group = ModLibrary.Get<ChannelGroupReference>("Sfx").ChannelGroup;
       Check(GameAudio.System.TryPlaySound(_sound, group, true, out _channel), "play sound");
       _hasChannel = true;
       ApplySettings(); // Configure position and gain while paused: no one-frame sound at the origin.
       Check(_channel.TrySetPaused(false), "start sound");
   }
   ```

4. **Stop and release.** Stop the channel, release the sound, mark the entry finished; this runs on every exit path (a
   finished one-shot, an error, target loss, Dispose):

   ```csharp
   // byo-music.lib/VesselSound.cs L134-L141
   public void Stop(string status = "Stopped")
   {
       if (_hasChannel) { _channel.TryStop(); _hasChannel = false; }
       if (!_sound.IsNull()) { _sound.TryRelease(); _sound = default; }
       Finished = true;
       Status = status;
   }
   public void Dispose() => Stop();
   ```

5. **Do not make a second FMOD system.** A second system means a second listener and output device; the game's already
   has 3D settings, the file callbacks and the volume groups.

## Complete example

| File | Role |
|---|---|
| [byo-music.lib/VesselSound.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/VesselSound.cs) | Stream ownership, decode polling, start, stop |
| [byo-music.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/README.md) | What the mod promises and which checks remain in-game |

## Variations and gotchas

- **Do not `Thread.Sleep` in a frame hook.** The game's own `CreateFmodSound` does, but only while loading assets.
- **Check `Result` on every call.** The `Try*` forms return a `Result`; BYO Music wraps them in a `Check` that throws and
  turns any non-Ok result into a stopped entry with a message.
- **Handles go stale.** A finished one-shot's `Channel` handle can answer `ErrInvalidHandle` or `ErrChannelStolen`;
  treat those as "not playing", as BYO Music's `TryIsPlaying` branch does.
- **Mode flags on the stream and on the channel.** The stream is created `_3d | NonBlocking | LoopOff`; the channel's
  mode is set again each tick (`_3d | _3dLinearRolloff` plus `LoopNormal` or `LoopOff`).
- **Authors' own caveat.** The mod's README says native decoding, 3D listening, SFX volume and stop/unload behaviour
  still need an in-game audio check.[^unscience-readme]

## See also

- [Overview of this topic](overview.md)
- [Approach 1: in-memory sounds, a channel table and reference-counted release](play-sound-files-through-the-games-fmod-system-1.md)
- [Approach 3: a vessel-attached sound with per-frame 3D attributes](play-sound-files-through-the-games-fmod-system-3.md)
- [Add background music for a body with a MusicPlaylist asset](../../sound-assets/add-a-music-playlist-for-a-location/overview.md)
- [Build searchable lists and browsers](../../../ui/windows/build-searchable-lists-and-browsers/overview.md) (the import browser and pickers)
- Mod feature: [BYO Music](../../../../mods/unscience/features/byo-music-vessel-3d-audio.md)

[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^brutal-fmod-openstate]: Brutal.FmodApi.OpenState (decompiled, build 2026.10.10.5554)
[^brutal-fmod-mode]: Brutal.FmodApi.Mode (decompiled, build 2026.10.10.5554)
[^brutal-fmod]: Brutal.FmodApi.Fmod (decompiled, build 2026.10.10.5554)
[^unscience-readme]: unscience: byo-music.lib/README.md
[^ksa-channelgroupreference]: KSA.ChannelGroupReference (decompiled, build 2026.10.10.5554)
