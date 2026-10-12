# Game Feature

* [Debug XML patches with the step debugger and the session log](overview.md) - How to find out why a KittenExtensions XML patch did nothing, using the pre-game ImGui stepper and the per-session patch log

# How-To

* [Audit what patches changed from KittenExtensions.log](debug-xml-patches-2.md) - Read the per-session KittenExtensions.log to see every Set, Insert, InsertText and Remove a patch made with old and new values, and the per-file applied and failed summary
* [Debug a failing XML patch with the KittenExtensions step debugger](debug-xml-patches-1.md) - Set debug = true on the KittenExtensions manifest entry to stop startup in an ImGui stepper with Run All, Step, Step Over, Step Out, an XPath tester and a live document tree
