# Game Feature

* [Detect flight events when the game has no event bus](overview.md) - Derive liftoff, staging, flameout, SOI change, docking and similar events by diffing consecutive samples with allocation-free edge detection, and deliver them to consumers as an ordered stream

# How-To

* [Debounced heuristics on burning-engine and part counts for webcast callouts](detect-flight-events-from-snapshots-2.md) - Infer liftoff, max q, stage separation, MECO, ignition and landing from a few counters in consecutive samples, with debounces and per-vehicle baselines so a flickering count never repeats a callout
* [EventDiffer over consecutive immutable SimSnapshots](detect-flight-events-from-snapshots-1.md) - Compare each new snapshot with the previous one on the game thread and emit one event per edge, allocating nothing when nothing changed, then deliver the list to every transport
