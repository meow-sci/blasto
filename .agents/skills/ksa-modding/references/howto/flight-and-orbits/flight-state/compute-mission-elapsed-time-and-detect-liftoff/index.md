# Game Feature

* [Show mission elapsed time from actual liftoff and pick the right clock under time warp](overview.md) - Start a mission clock at real liftoff instead of scene load, and choose between universe time, the applied simulation step and wall clock so timers, schedulers and cooldowns behave under time warp

# How-To

* [Advance simulated objects on Universe.GetLastSimStep, deduplicated on NextTime](compute-mission-elapsed-time-and-detect-liftoff-2.md) - Integrate mod-simulated objects across exactly the interval the engine just applied, taking each step once and closing the gaps from frames the mod missed, so pause and time warp are right by construction
* [Choose render, wall or sim time for timed command playback](compute-mission-elapsed-time-and-detect-liftoff-3.md) - Drive a schedule of timed commands from the clamped player delta, a wall stopwatch or the sim-time delta, with a catch-up policy that never drops triggers
* [Run a T-/T+ mission clock keyed on LaunchGameTime with contact-loss liftoff detection](compute-mission-elapsed-time-and-detect-liftoff-1.md) - Start the clock when the vehicle actually leaves the ground, key it on LaunchGameTime so separated stages share it, detect reverts and persist epochs across reloads
