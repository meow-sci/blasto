# Game Feature

* [Schedule timed command sequences on render, wall or sim time](overview.md) - Play back a script of commands at millisecond offsets against a chosen clock, ticked on the game thread just before the command drain

# How-To

* [Scheduler posting SimCommands before the drain, with render, wall and ut clocks](schedule-timed-commands.md) - Parse a timed script once, advance one playback clock per tick on the game thread and post due commands into the game-thread queue with catch-up that never skips a trigger
