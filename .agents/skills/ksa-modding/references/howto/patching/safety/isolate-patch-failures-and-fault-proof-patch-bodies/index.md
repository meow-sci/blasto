# Game Feature

* [Isolating patch failures and keeping patch bodies from throwing](overview.md) - How KSA mods localise damage when a game update breaks one Harmony target and how they write patch bodies that cannot take the game's frame loop down, from per-class application to fault counters

# How-To

* [Consolidated patcher with TryApply and ordered shared seams](isolate-patch-failures-and-fault-proof-patch-bodies-2.md) - One Harmony id for thirty-odd features, each library's static Apply and Remove wrapped in TryApply and TryRemove, order only where seams are shared, idempotent shared helpers and removal by patch method instead of by id
* [CreateClassProcessor per patch class, required versus optional](isolate-patch-failures-and-fault-proof-patch-bodies-1.md) - Apply each Harmony patch class on its own inside try/catch so one drifted target costs one feature, classify patches by what the player loses, log each exception type once, and warn about reflection drift at load
* [Fault counter that pulls every patch after a limit](isolate-patch-failures-and-fault-proof-patch-bodies-5.md) - Count exceptions in the frame wrappers, and after ten failures disable the mod for the session, release everything it holds and remove every patch class so every later hook returns at its first line
* [Reflection-only mod with parameter-name and field-type pre-checks](isolate-patch-failures-and-fault-proof-patch-bodies-4.md) - Compile against StarMap.API and 0Harmony only, reach every game member by name, verify parameter names and field types before each patch and wrap each subsystem in its own try so a game update costs one feature
* [Static patch classes that look up the target, log once and stand down](isolate-patch-failures-and-fault-proof-patch-bodies-3.md) - KSAGolf keeps each Harmony patch in its own static class with its own id, an Installed flag and a written consequence for being absent; the body catches everything and nulls its delegate on a repeating fault so the patch stands down
