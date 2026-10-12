# Game Feature

* [Store per-save mod data next to a KSA save and notice when the player saves](overview.md) - Why per-save mod data lives in a sidecar, where the sidecar can go, how to detect that the game wrote a save, and the three layouts mods use - JSON in the save folder, a per-save store written on Make, and TOML in the mod folder keyed by save and vehicle

# How-To

* [Keep a JSON sidecar inside the save folder and write it when universe.xml changes](save-mod-state-with-the-game-save-1.md) - Store per-craft settings in saves/<id>/<Mod>/systems.json, adopt a loose file on the first save, never create a missing save folder, and poll the universe.xml write time to learn that the game saved
* [Keep TOML in the mod folder keyed by (save id, vehicle id) and rekey it on save-as](save-mod-state-with-the-game-save-3.md) - Leave the stock save untouched, store execution state in the mod folder, key every entry by save id and vehicle id, load it at startup, and move entries to the new save id after a successful UncompressedSave.Write
* [Write a per-save settings store from a postfix on UncompressedSave.Make](save-mod-state-with-the-game-save-2.md) - Track the current save id with postfixes on Load and Make and keep a generic store that reads every save folder's file and writes one atomically right after the game's own write
