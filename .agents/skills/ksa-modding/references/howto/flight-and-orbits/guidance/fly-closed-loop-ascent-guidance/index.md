# Game Feature

* [Fly a closed-loop ascent to a target orbit](overview.md) - Guide a launch to a target orbit with closed-loop ascent guidance, optionally seeded by a propellant-optimal pre-planned trajectory

# How-To

* [Fly a UPFG ascent using a stage list built from the game's SequencePerformance phases](fly-closed-loop-ascent-guidance-1.md) - Build the multi-stage vehicle model that Space Shuttle style closed-loop ascent guidance needs from the game's staging simulator, solve it once per guidance second, and steer a phase machine from vertical rise to a frozen terminal attitude
* [Plan a propellant-optimal ascent with SCvx on a private thread and fly the published plan](fly-closed-loop-ascent-guidance-2.md) - Gather the stage model, drag table, atmosphere and solid-motor tables on the main thread, solve a minimum-propellant ascent for seconds on a background thread, publish an immutable plan and fly it against air-relative speed until UPFG takes over
