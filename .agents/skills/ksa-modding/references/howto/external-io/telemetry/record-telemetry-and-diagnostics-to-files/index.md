# Game Feature

* [Record telemetry and diagnostics to files without stalling the frame](overview.md) - Write flight telemetry to files for later analysis, as an on-demand export of a history buffer, a continuous per-step recorder that never throws and never waits on the disk, or one-shot latched validation dumps

# How-To

* [Latched dumps from StarMapAfterGui behind a DebugConfig flag](record-telemetry-and-diagnostics-to-files-3.md) - Run validation dumps once per session after the universe exists, write text and SVG files built by pure code shared with offline tests, and log each error only once per key
* [Static single-owner buffered sink with per-step CSV, plan snapshots and event log](record-telemetry-and-diagnostics-to-files-2.md) - Record one CSV row per guidance step into memory, flush every 60 rows, swallow every I/O error, allow one owner vehicle per run and log only when a bucketed state signature changes
* [Wall-clock player-time interval feeding a fixed-length history array](record-telemetry-and-diagnostics-to-files-1.md) - Sample six flight metrics every 0.25 s of player time from the HUD draw into shifted arrays for a graph, and export them as CSV to the KSA documents folder when the player clicks Export
