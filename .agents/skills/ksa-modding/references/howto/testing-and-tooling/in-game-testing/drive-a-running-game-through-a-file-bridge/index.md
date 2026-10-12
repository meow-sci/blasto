# Game Feature

* [Drive a running game from scripts through a file-drop command bridge](overview.md) - Poll a folder for JSON command files from the frame postfix so a shell script, a test runner or an agent can pause, step, load saves and script gameplay in a live game, with the bridge kept out of player installs

# How-To

* [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](drive-a-running-game-through-a-file-bridge-1.md) - Build the reference command bridge that polls a JSON inbox from StarMapAfterOnFrame, steps an exact number of simulated seconds, loads a save and is constructed only on a developer install
* [A lean bridge with shader hot reload, held camera shots and a UI flag instead of work in the UI pass](drive-a-running-game-through-a-file-bridge-3.md) - Extend the file bridge with reload_shaders through the internal FileReference.DoLoad, a composed camera shot held every step, and capture requests that a UI button only flags
* [Gate developer features on a custom key in the player's manifest.toml entry](drive-a-running-game-through-a-file-bridge-4.md) - Read an extra debug key that the player adds to the mod's entry in the game manifest, and know that the game rewrites the manifest with only id and enabled
* [Get and set every config field by reflection, and script gameplay as composed commands](drive-a-running-game-through-a-file-bridge-2.md) - Grow a file bridge with name-based get and set over a settings object and with commands that feed scripted input frames into the mod's own gameplay state
* [Keep dumps and perf scopes out of release builds with a DEBUG flag and a runtime switch](drive-a-running-game-through-a-file-bridge-5.md) - Compile validation dumps and timing scopes only into Debug builds and gate their runtime use behind constant flags so a release build pays one boolean check
