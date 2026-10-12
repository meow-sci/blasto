# Game Feature

* [Log from a mod: per-mod log file, game log category and log-once](overview.md) - The ways a StarMap mod can log - its own rotated file, the game's Brutal log system, a game-free interface - and how to keep per-frame problems from flooding the output

# How-To

* [Anchor the log on the game's logs folder and gate developer tools with a marker file](write-a-per-mod-log-file-2.md) - Resolve the log directory from Constants.LogsFolderPath instead of guessing, and switch developer-only tools on with an empty file that only the deploy script writes
* [Find a writable log folder by probing Documents, XDG and fallback locations](write-a-per-mod-log-file-3.md) - Resolve a log directory across Windows, Wine/Proton and Linux by trying existing candidate folders in order, with the temp folder as a last resort, plus immediate flush on warnings
* [Keep library code game-free with a logging interface swapped for a Brutal sink at load](write-a-per-mod-log-file-5.md) - Log through a tiny IModLogger interface that defaults to the console and is replaced by a LogCategory-backed sink at StarMapAllModsLoaded, so game-free projects stay testable
* [Log each problem once with a thread-safe warn-once set reset on unload](write-a-per-mod-log-file-6.md) - Keep per-frame and per-substep failures from flooding the log with keyed WarnOnce/DebugOnce sets behind one lock, a time throttle, and compile-time debug switches
* [Log through the game's logging system with one LogCategory in a tiny shared assembly](write-a-per-mod-log-file-4.md) - Make a static LogCategory in its own small assembly so every project and importing mod writes to the game's log sinks, including the in-game console
* [Log with HashSet-keyed WarnOnce and ErrorOnce that include the exception type](write-a-per-mod-log-file-7.md) - Deduplicate per-frame failures with separate warning and error key spaces keyed by site and exception type, and log only the first failure of a streak
* [Write a batched session log with .prev.log rotation, timer flush and a verbose toggle](write-a-per-mod-log-file-1.md) - Copy a small static Log class that rotates the last session to .prev.log, batches writes with a 100 ms timer, flushes warnings immediately and lets a UI checkbox switch to debug level
