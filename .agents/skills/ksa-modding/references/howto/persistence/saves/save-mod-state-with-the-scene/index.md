# Game Feature

* [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](overview.md) - How to persist many features with native objects to rebuild - versioned JSON records per feature, validated before the world is touched, reset in reverse order and replayed in order - and the patches that make it work

# How-To

* [Hook native save and load with five patches, eight callbacks and a deferred world change](save-mod-state-with-the-scene-3.md) - Patch GameSave.Populate, UncompressedSave.Write and Load, and Universe.DeserializeSave and LoadSystem with exact-signature binding, defer every load to the PrepareFrame handoff, join the workers before reset, validate incoming data first and turn silent read failures into errors
* [Pair each scene capture with its native write by object identity](save-mod-state-with-the-scene-2.md) - Connect the coordinator to the save hooks so a Populate postfix stores the document in a ConditionalWeakTable keyed by the GameSave, the Write postfix publishes it only after a successful native write, and a missing sidecar means reset everything and restore nothing
* [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](save-mod-state-with-the-scene-1.md) - Define a participant contract with capture, reset and prepare-restore, drive it from a game-independent coordinator, store versioned records in one hash-bound atomic JSON document, and keep unknown or failed records so older builds round-trip them
