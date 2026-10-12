# Game Feature

* [Keep mod-played sounds sensible under time warp and the player's volume](overview.md) - What the engine does to a sound at high simulation speed (pitch rule, stop above 10x, PlaySound refusing) and to the player's Master, Sfx, Ui and Music sliders, and how a mod follows, overrides or bypasses each

# How-To

* [Cut your own loops below the engine's time-warp threshold](keep-mod-sounds-sensible-under-time-warp-2.md) - Play loops through SoundBehavior.Play, which skips the 10x entry gate, and cut them yourself above a lower warp threshold where input swings would only make noise, then restart them when the world slows again
* [Declare a SimSpeed rule and let the engine handle time warp](keep-mod-sounds-sensible-under-time-warp-1.md) - Choose a SimSpeedRule per sound in XML, call GameAudio.PlaySound, handle the missing channel above 10x, and know when SimSpeed Ignore plus alwaysPlay is needed to keep a sound playing
* [Play channels the game never registers so warp rules do not touch them](keep-mod-sounds-sensible-under-time-warp-3.md) - Create FMOD channels directly on GameAudio.System and the game's Sfx group so HandleSimSpeedPitch and StopForSimSpeed never see them, keeping stable pitch and sound in warp while still following the player's Sfx and Master sliders
