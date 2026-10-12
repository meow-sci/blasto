---
type: Submod
title: BYO Music
description: Attaches imported OGG, WAV or MP3 files to vessels as 3D sounds on KSA's own FMOD system, with seamless loops, real-time gaps between plays, live edits and scene saves
mod: unscience
submod: byo-music
tags: [audio, fmod, 3d-audio, sounds, import, scene-saves]
mod_id: byo-music
---

BYO Music ("bring your own") lets players import their own sound files and play them from any vessel
in 3D: the sound follows the vessel, fades with distance from the camera, respects the game's SFX
and master volume, keeps its pitch in time warp and also plays in space. Each entry can loop
seamlessly or repeat with a real-time gap after every completed play, and volume, range, repeat and
gap can be changed while it plays. Playback ends when the vessel disappears or the mod unloads, and
imported files stay in a shared catalog that other sub-mods use too.[^byo-readme][^byo-lib-readme]

## Hosting

- Manifest id `byo-music`, entry assembly `MeowSci.ByoMusic`, and `assets = [ "Assets.xml" ]` in the
  development host's `mod.toml` (a demo `MultiSound` and `MusicPlaylist`). The shipped
  `unscience/mod.toml` declares no assets, so the demo is development-only.[^byo-mod-toml][^byo-assets][^unscience-mod-toml]
- Standalone host: F11 toggles a "BYO Music" window and calls `RenderFloatingWindows` for the import
  browser; its Patcher runs `PatchAll` on its own assembly (no patch classes) and applies
  `HotkeyGuard`. No game patches.[^byo-mod-cs][^byo-patcher]
- In unscience, `ByoMusicSubmod` is created in `Mod.OnFullyLoaded` and ticked by `UpdateSubmods`
  (also while the HUD is hidden); `unscience/Patcher.cs` has no BYO Music entry.[^unscience-mod-cs]

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Play imported sound files in 3D at a vessel through nonblocking FMOD streams on the game's Sfx group](../features/byo-music-vessel-3d-audio.md) | audio | `frame:draw-ui`, `frame:post-render`, `event:save`, `event:load`, `frame:prepare` | BYO Music plays imported OGG, WAV or MP3 files as nonblocking 3D FMOD streams on KSA's own system and Sfx group, positioned at a vessel each frame through SpatialAudio, with loops, real-time gaps and scene saves |

## Library classes

`ByoMusicSubmod` (panel, sound and vessel pickers, live entries, two-second catalog rescan;
`.Saves.cs` participant), `VesselSound` (one FMOD stream and channel per entry), `RepeatPlayback`
(completion-based gap scheduler) and the legacy `MusicPlayer` playlist helper. Shared from
`ksa-abstractions.lib`: `SoundLibrary` / `SharedFileLibrary` and `LibraryFileBrowser` (copied-import
catalog and file picker), `VehicleProvider`, `SubmodUI`, `HotkeyGuard`.[^byo-lib-readme]

## Scene saves and persistence

Participant `byo-music`, order 220: per unfinished sound the vehicle id, library file name, repeat,
gap, volume and range. Reset stops and releases every stream before KSA replaces the vehicles;
restore re-creates the sounds paused, to be resumed from the beginning; missing vessels or files are
warnings. Imported files persist in `.unscience/sounds`; playback state outside scene saves is
session-only. `byo-music.tests` checks the repeat scheduler and the copied catalog without FMOD.[^byo-saves][^byo-tests-readme]

## Further reading

- [byo-music/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/README.md)
- [byo-music.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.lib/README.md)
- [byo-music.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music.tests/README.md)
- [scope/audio.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/audio.md) (FMOD integration contract per build)[^scope-audio]

## Entry points

- [byo-music/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/mod.toml)
- [byo-music/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/Mod.cs)
- [byo-music/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/byo-music/Patcher.cs)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L77)

## See also

- [unscience overview](../overview.md)
- [Shared copied-import media catalogs](../features/lib-shared-file-library.md) (the `sounds` catalog and file browser)
- [Free Fallin](./free-fallin.md) (shares the same import browser for PNGs)

[^byo-readme]: byo-music/README.md
[^byo-lib-readme]: byo-music.lib/README.md
[^byo-tests-readme]: byo-music.tests/README.md
[^scope-audio]: scope/audio.md
[^byo-mod-toml]: byo-music/mod.toml
[^byo-assets]: byo-music/Assets.xml
[^byo-mod-cs]: byo-music/Mod.cs
[^byo-patcher]: byo-music/Patcher.cs
[^byo-saves]: byo-music.lib/ByoMusicSubmod.Saves.cs
[^unscience-mod-cs]: unscience/Mod.cs
[^unscience-mod-toml]: unscience/mod.toml
