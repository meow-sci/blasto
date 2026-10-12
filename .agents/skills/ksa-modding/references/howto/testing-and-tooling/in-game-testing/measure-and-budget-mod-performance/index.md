# Game Feature

* [Measure what a mod costs per frame, throttle expensive work and warm up hot code](overview.md) - Wrap hot code in zero-cost-when-off scopes with periodic reports, read a mod pass's GPU cost from the game profiler with a pinned camera, throttle per-frame work on wall-clock intervals and state versions and pre-compile hot paths on a background thread

# How-To

* [A 45 ms wall-clock throttle with a state-version check for hover picks](measure-and-budget-mod-performance-5.md) - Cache an expensive per-frame pick and re-run it on a wall-clock interval, when the tool state changes or when a modifier flips, so cost does not scale with refresh rate
* [Always-on single-writer counters shown in a status window](measure-and-budget-mod-performance-2.md) - Record timing and allocation with an allocation-free accumulator written by one thread and read by the UI, live in any build
* [Nested budget spans with top-level totals, the worst frame and rate-limited overrun warnings](measure-and-budget-mod-performance-3.md) - Book named spans against the frame, add up only the outermost, report mean and worst per span every ten seconds and rate-limit a warning that would otherwise repeat every frame
* [Pre-compile hot code at load with a Task.Run PrepareMethod sweep](measure-and-budget-mod-performance-6.md) - Compile the mod's own methods and the engine types the first use will touch on a background thread so the first kill, shot or open does not pay the JIT in one frame
* [Read a mod pass's GPU cost from KSA's profiler while a pinned camera keeps the runs comparable](measure-and-budget-mod-performance-4.md) - Switch on the game's GPU profiler, tag the mod's dispatch, sample the newest complete frame and report the pass against the whole frame, with a control run and a pinned view
* [Time hot code with a DEBUG-only readonly ref struct scope and a periodic report](measure-and-budget-mod-performance-1.md) - Add a using-scope that stamps a timestamp only when a debug flag is on, accumulates average, minimum and maximum per key and logs them every few seconds, compiled out of release builds
