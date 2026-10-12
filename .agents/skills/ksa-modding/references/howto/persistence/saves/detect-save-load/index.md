# Game Feature

* [Detect a save load or quickload and reset mod state safely](overview.md) - What a load destroys and keeps, the signals a mod can watch, and six ways mods react - latch on OnGameLoaded, pin the target, observe UncompressedSave.Load, postfix DeserializeSave, infer from the clock, or rebuild from the live world

# How-To

* [Infer a reload from universe time running backwards](detect-save-load-5.md) - Detect a quickload or revert with no patch by remembering the last universe time seen per mission and resetting that mission when the clock drops by more than a tolerance
* [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md) - Postfix OnGameLoaded to set a flag only, consume it once in the mod's frame step, and drop everything that belonged to the replaced world with Discard rather than a clear that writes settings
* [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](detect-save-load-3.md) - Capture UniverseData in a prefix and compare it in the postfix so refused and failed loads are ignored, learn the loaded save id, and reset every cache that holds vehicle, burn or body-index references
* [Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch](detect-save-load-2.md) - The same OnGameLoaded latch with a never-called method that references the target, so a later signature change fails to compile and a missing target degrades with one logged warning
* [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](detect-save-load-4.md) - Clear anchors held against the old world from a postfix on DeserializeSave, because a latch on the CurrentSystem reference cannot see a load
* [Skip detection: rebuild a derived registry from the live world every frame](detect-save-load-6.md) - Find cars by scanning the live vehicles each frame, resolve their parts once, restore state from the part's own saved fields and drop entries whose vehicle is disposed, so a load needs no special handling
