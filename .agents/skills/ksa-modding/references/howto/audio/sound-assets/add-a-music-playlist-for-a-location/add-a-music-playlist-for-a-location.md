---
type: How-To
title: Declare a MusicPlaylist in a mod asset XML
description: Declare a MusicPlaylist with MusicFile entries, loop and sync points, a fade and a LocationId so LocationMusicPlayer picks it while the camera follows that body or a child of it
category: audio
group: sound-assets
topic: add-a-music-playlist-for-a-location
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/byo-music-vessel-3d-audio]
techniques: [xml-asset]
hooks: [xml Assets root listed in mod.toml assets]
lifecycle: [startup:asset-load, startup:game-loaded, frame:post-render]
game_types:
  - KSA.MusicPlayList
  - KSA.MusicFileReference
  - KSA.LocationMusicPlayer
  - KSA.MusicPlayer
  - KSA.GameAudio
  - KSA.ChannelGroupType
tags: [audio, music, xml, playlist, location, crossfade, sync-points]
---

Outcome: a mod-declared playlist that the game plays while the camera is on a chosen body (or a child of it) and that
crossfades into the next track the way the stock soundtrack does. Prerequisites: a content mod with an asset XML
listed in `mod.toml` ([ship content and code in one mod](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md)).

**Evidence status.** No shipped mod runs this path in game. Everything below about behaviour is read from the
decompiled `MusicPlayList` and `LocationMusicPlayer` and from Core's playlists, and has not been run in game. The
only mod-side example, BYO Music's development `Assets.xml`, shows the element names and is not loaded by the
shipped mod.[^unscience-assets][^unscience-mod]

## Background: how the game does it

The player is a static class that runs every frame:

```csharp
// current/decomp/KSA/LocationMusicPlayer.cs L86-L108
public static void Update(double deltaTime)
{
	IFollowable following = GameAudio.GetAudioCamera().Following;
	MusicPlayList locationPlaylist = GetLocationPlaylist(following);
	if (locationPlaylist == null)
	{
		Stop();
		return;
	}
	if (GameAudio.GetAudioCamera().Following is Vehicle vehicle)
	{
		CameraReferenceFrame referenceFrame = vehicle.OrbitView.ReferenceFrame;
		if (referenceFrame != CameraReferenceFrame.Orbit && referenceFrame != CameraReferenceFrame.Parent)
		{
			Stop();
			return;
		}
	}
	if (locationPlaylist != CurrentPlaylist || CurrentChannel == null || !CurrentChannel.IsPlaying())
	{
		ScheduleNext(locationPlaylist);
	}
}
```

- `GameAudio.GetAudioCamera().Following` is the object the main camera follows. `GetLocationPlaylist` hashes it and
  looks for a playlist whose `LocationId` has the same hash, and when there is none, recurses into `Parent` for an
  `IOrbiter`. So a playlist on a planet covers its moons and its vehicles, unless one of them has a playlist of its
  own (Core gives the Moon its own).[^ksa-locationmusicplayer]
- A `Vehicle` only gets music while its camera reference frame is `Orbit` or `Parent`; in other frames `Stop()` runs.
- If the wanted playlist differs from the current one, or nothing is playing, `ScheduleNext` starts it.

`PlayMusic` picks a file with the playlist's `SelectionRule`, starts a **paused** channel on the playlist's own
channel group, sets a delay on the group's DSP clock, then unpauses and registers the wrapper in
`GameAudio.ActiveChannels`, so the playlist's `SimSpeed` and `FadeOut` apply:[^ksa-musicplaylist]

```csharp
// current/decomp/KSA/MusicPlayList.cs L21-L37
public void PlayMusic(out ChannelWrapper? iChannel, ulong delaySamples = 0uL)
{
	SoundFileReference nextSound = GetNextSound();
	ChannelGroup channelGroup = base.ChannelGroup;
	iChannel = null;
	if (GameAudio.System.TryPlaySound(nextSound.FmodSound, channelGroup, true, out var channel) == Result.Ok)
	{
		channelGroup.TryGetDspClock(out var _, out var parentClock);
		channel.TrySetDelay(parentClock + delaySamples, 0uL, true);
		ChannelWrapper channelWrapper = new ChannelWrapper(channel, nextSound, this);
		channelWrapper.UpdateVolume();
		channelWrapper.UpdatePitch();
		channelWrapper.SetPaused(isPaused: false);
		GameAudio.Register(channelWrapper);
		iChannel = channelWrapper;
	}
}
```

## Lifecycle and timing

| Moment | What happens | Vocabulary |
|---|---|---|
| Game start | The playlist registers in `ModLibrary` (twice: as a sound and as a playlist) and its music file loads | `startup:asset-load` |
| End of loading | `PopulateSounds`, then `LocationMusicPlayer.Initialise` indexes playlists by `LocationId` hash[^ksa-program] | before `startup:game-loaded` |
| Every frame | `LocationMusicPlayer.Update` from `GameAudio.UpdateAudio` | `frame:post-render` |

`Initialise` runs once, so a playlist registered after loading is never indexed. All of it is the main thread.[^ksa-gameaudio]

## Steps

1. **Put the music in the mod.** Core uses `.ogg` files in `Music/`. Use a format FMOD decodes; BYO Music's example
   uses an `.mp3`.

2. **Copy the shape of a Core playlist.** `EarthSOIMusic` is the full-featured one: shuffle, a loop that starts after
   the intro, sync points, a fade-out:

   ```xml
   <!-- current/Content/Core/Sounds.xml L522-L547 -->
   <MusicPlaylist
           Id="EarthSOIMusic"
           LocationId="Earth"
           SimSpeed="Ignore"
           SelectionRule="Shuffle"
           ChannelId="Music"
   >
       <MusicFile Path="Music/08_SunSynchronous.ogg" StreamFromDisk="true" TwoD="true" Volume="0.2">
           <Loop>
               <Start Value="5666" Unit="Ms" />
           </Loop>
           <SyncPoint Value="5666" Unit="Ms" />
           <SyncPoint Value="15800" Unit="Ms" />
           <SyncPoint Value="29233" Unit="Ms" />
           <SyncPoint Value="56600" Unit="Ms" />
           <SyncPoint Value="68533" Unit="Ms" />
           <SyncPoint Value="83066" Unit="Ms" />
           <SyncPoint Value="116433" Unit="Ms" />
           <SyncPoint Value="139600" Unit="Ms" />
           <SyncPoint Value="177200" Unit="Ms" />
           <SyncPoint Value="194666" Unit="Ms" />
           <SyncPoint Value="220233" Unit="Ms" />
           <SyncPoint Value="242800" Unit="Ms" />
       </MusicFile>
       <FadeOut Time="5" />
   </MusicPlaylist>
   ```

   `Sol`, `Mars` and `Jupiter` are the plain ones, with a bare `<Loop />` and no sync points:

   ```xml
   <!-- current/Content/Core/Sounds.xml L576-L586 -->
   <MusicPlaylist
           Id="SolSOIMusic"
           LocationId="Sol"
           SimSpeed="Ignore"
           SelectionRule="Shuffle"
           ChannelId="Music"
   >
       <MusicFile Path="Music/02_LiminalSpace.ogg" StreamFromDisk="true" TwoD="true" Volume="0.2">
           <Loop />
       </MusicFile>
       <FadeOut Time="5" />
   ```

3. **Adapt it to your mod.** Adapted from Core's `SolSOIMusic`; replace the ids, the body and the path (`MyBodyId` must
   be the id of a body that exists in the loaded system):

   ```xml
   <!-- adapted from current/Content/Core/Sounds.xml L576-L586 -->
   <MusicPlaylist
           Id="MyModBodyMusic"
           LocationId="MyBodyId"
           SimSpeed="Ignore"
           SelectionRule="Shuffle"
           ChannelId="Music"
   >
       <MusicFile Path="Music/MyTrack.ogg" StreamFromDisk="true" TwoD="true" Volume="0.2">
           <Loop />
       </MusicFile>
       <FadeOut Time="5" />
   </MusicPlaylist>
   ```

   What each part does:[^ksa-musicplaylist][^ksa-musicfilereference]

   | XML | Meaning |
   |---|---|
   | `LocationId` | The body (or other followable) id the playlist belongs to; hashed and compared with the followed object's hash |
   | `ChannelId="Music"` | The `Music` channel group, which the player's music slider controls. Write `ChannelId`, not `Channel` (see the gotchas) |
   | `SimSpeed="Ignore"` | No pitch change or 10x stop under time warp (Core's choice) |
   | `SelectionRule="Shuffle"` | Which `MusicFile` starts next when there are several |
   | `StreamFromDisk`, `TwoD` | Open the file as a stream from disk (the loader waits until FMOD reports it ready) and play it without 3D positioning; Core's music uses both |
   | `Volume="0.2"` | Per-file gain; Core's tracks sit at 0.2 |
   | `<Loop>` | Loop mode on; `<Start Value Unit>` and `<End ...>` set the loop points |
   | `<SyncPoint Value Unit>` | A moment a crossfade may start; `Unit` must be `Ms` (`GetValueMs` throws `NotImplementedException` for any other unit)[^ksa-audiosyncpointdata] |
   | `<FadeOut Time="5" />` | Seconds the channel fades when stopped |

4. **List the XML in `mod.toml`.** As for any asset XML (BYO Music's development host does it like this):

   ```toml
   # byo-music/mod.toml L6-L6
   assets = [ "Assets.xml"  ]
   ```

5. **Know how the switch happens.** With a track already playing, `ScheduleNext` (default fade 4000 ms) finds the
   next `SyncPoint` that is more than the fade plus a small buffer ahead, fades the old channel out to end at that
   point, and starts the new playlist's channel with a delay so it arrives as the old one fades. A file with no sync
   points makes that switch happen almost at once, so put sync points in tracks that should hand over smoothly.[^ksa-locationmusicplayer]

6. **Check it in game.** The debug menu has a **Show Audio Debug** item (`Program.ShowAudioDebug`) that opens an
   "Audio Debug" window listing "Previous Music" and "Current Music" playlist ids and every playing channel.[^ksa-gameaudio]

## Complete example

There is no complete shipped example. The files to read are:

| File | Role |
|---|---|
| [current/Content/Core/Sounds.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Sounds.xml#L522-L611) | Core's five playlists |
| [current/decomp/KSA/LocationMusicPlayer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationMusicPlayer.cs) | Lookup, `Update`, `ScheduleNext` |
| [byo-music/Assets.xml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/Assets.xml) | The development-host demo (`MultiSound` plus `MusicPlaylist`, not shipped) |

## Variations and gotchas

- **`Channel` is not an attribute.** BYO Music's development asset writes `Channel="Music"`; `SoundReference` maps
  `ChannelId`, so the engine would not see the music group there (inferred from the type, not run in game).
- **Same `LocationId` replaces.** In `Initialise`, a playlist whose id is already indexed replaces the earlier one
  (`TryAdd` fails, then `Remove` and `Add`), so the last playlist in registration order wins; a mod playlist for
  `Earth` would override Core's, assuming the mod's XML registers after Core's (not tested).
- **`MusicPlayList.OnDataLoad` also sets the legacy `MusicPlayer.CurrentPlaylist`.** In this build nothing calls
  `MusicPlayer.Update`; only `LocationMusicPlayer` runs.[^ksa-musicplayer]
- **Calling `PlayMusic` yourself** (BYO Music's legacy `MusicPlayer.Play` does) starts a channel outside
  `LocationMusicPlayer`, which does not track it; the helper discards the returned wrapper, so nothing stops it later.
  The shipped BYO panel does not use that helper.[^unscience-musicplayer]
- **No spatial music, no per-vessel music.** Playlists are 2D and keyed by body. Music from a vessel in 3D uses the
  FMOD route in [the FMOD sound-files topic](../../channels/play-sound-files-through-the-games-fmod-system/overview.md).
- **Replacing the stock soundtrack wholesale** needs a playlist for each body Core covers; nothing in the decomp
  disables Core's.
- **Teardown.** Music channels are in `ActiveChannels`; nothing needs stopping at exit, and FMOD is closed before
  `StarMapUnload` anyway
  ([release sound channels at scene change and exit](../../channels/release-sound-channels-at-scene-change-and-exit/overview.md)).

## See also

- [Overview of this topic](overview.md)
- [Read the sound type map through the features that use each type](../understand-the-games-sound-and-music-xml-types/overview.md)
- [Ship custom sounds with a mod as XML sound assets](../ship-custom-sounds-as-xml-assets/overview.md)
- [Play your own sound files through KSA's FMOD system without blocking](../../channels/play-sound-files-through-the-games-fmod-system/overview.md)
- Mod feature: [BYO Music](../../../../mods/unscience/features/byo-music-vessel-3d-audio.md)

[^unscience-assets]: unscience: byo-music/Assets.xml
[^unscience-mod]: unscience: byo-music/mod.toml
[^ksa-locationmusicplayer]: KSA.LocationMusicPlayer (decompiled, build 2026.10.10.5554)
[^ksa-musicplaylist]: KSA.MusicPlayList (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameaudio]: KSA.GameAudio (decompiled, build 2026.10.10.5554)
[^ksa-musicfilereference]: KSA.MusicFileReference (decompiled, build 2026.10.10.5554)
[^ksa-audiosyncpointdata]: KSA.AudioSyncPointData (decompiled, build 2026.10.10.5554)
[^ksa-musicplayer]: KSA.MusicPlayer (decompiled, build 2026.10.10.5554)
[^unscience-musicplayer]: unscience: byo-music.lib/MusicPlayer.cs
