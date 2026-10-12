# Game Feature

* [Read KSA's star binary files and reload or replace the star field at runtime](overview.md) - The 16-byte star record format named by mod.toml, how to read it for catalog joins, and how to swap the star field at runtime by rewriting Mod.StarBinaries, resetting the instanced star technique and refilling it within its capacity

# How-To

* [Mirror Mod.LoadStarBinaries when parsing](read-and-replace-star-binaries-1.md) - Read a star binary with the same count-then-16-byte-record layout the game uses, keep the float direction bits as identity for joining catalog entries, and cap the read by the file length
* [Reflect Mod.StarBinaries, reset and refill the star technique, confirm through a game Popup](read-and-replace-star-binaries-2.md) - Switch the star field at runtime by listing star binaries in every loaded mod, rewriting the private StarBinaries, resetting every viewport's star instances, refilling within capacity by proportional stride sampling and uploading the instance buffer
