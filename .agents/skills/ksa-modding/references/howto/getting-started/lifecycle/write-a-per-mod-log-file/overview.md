---
type: Game Feature
title: "Log from a mod: per-mod log file, game log category and log-once"
description: The ways a StarMap mod can log - its own rotated file, the game's Brutal log system, a game-free interface - and how to keep per-frame problems from flooding the output
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
demonstrated_by:
  - mods/ksagolf/features/mod-log-file-with-rotation
  - mods/ksacars/features/mod-log-file
  - mods/ksarmory/features/own-log-file-and-developer-switch
  - mods/ksastructures/features/mod-log-file
  - mods/purrtty/features/brutal-logging-category
  - mods/gatos/features/logging-and-perfstats
  - mods/ksa-advancedflightcomputer/features/warn-once-logging-and-diagnostics
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/ksa-public-telemetry/features/error-budget-self-disable
lifecycle:
  - startup:immediate-load
  - startup:all-mods-loaded
  - thread:background
  - event:shutdown
  - frame:draw-ui
  - frame:apply-solvers
  - physics:vehicle-solver
game_types: [KSA.Constants, Brutal.Logging.LogCategory, Brutal.Logging.LogSystem]
tags: [logging, log-file, brutal, logcategory, warn-once, diagnostics]
---

A mod has four places to send a log line: stdout (visible only to whoever launched the game), the game's own
logging system (console window plus log file), a file of its own, or a game-free interface that adapts to either.
The corpus uses all of them, usually combined with a dedup helper so per-frame failures do not drown the log. The
seven articles are the variations; pick by what you must support.

## Engine background

The `Program` constructor creates the log folders, then initializes `LogSystem` with three sinks: the process console
(`KSALogFormatter`), the in-game `ConsoleWindow` and the Brutal monitor, which writes the game's log file into
`Constants.LogsFolderPath`; the minimum level is Debug.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L725)

| Member | File | Note |
|---|---|---|
| `Constants.DocumentsFolderPath`, `LogsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) | `<Personal>/My Games/Kitten Space Agency` and its lower-case `logs` subfolder; StarMap patches the first for instances |
| `LogCategory.Make`, `Debug`/`Info`/`Warning`/`Error` | [current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L44-L135) | Interpolated-string handlers; a category made early is given a logger when `LogSystem.Initialize` runs, but calls made before `LogSystem.IsEnabled` is set are dropped |
| `DefaultCategory.Log` | [current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs) | Ready-made public category (`Make("Default")`) |

Timing: `StarMapBeforeMain` runs before any of this exists (use stdout); from `StarMapImmediateLoad` on, the game log
works; file-log shutdown belongs last in `StarMapUnload` ([Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md) | KSAGolf, KSACars | `logging` | `startup:immediate-load`, `thread:background`, `event:shutdown` | Own file with stable name and previous session kept; folder is probed |
| [Constants.LogsFolderPath and a developer marker file](./write-a-per-mod-log-file-2.md) | KSArmory | `logging` | `startup:immediate-load` | Same class plus build identity and a deploy-written `developer` marker; note KSArmory itself does not read the constant |
| [Path probing across Documents and XDG locations](./write-a-per-mod-log-file-3.md) | KSAStructures | `logging` | `startup:immediate-load`, `thread:background` | Works without game types; misses `-InstancePath` and the `logs` case |
| [One LogCategory in a tiny shared assembly](./write-a-per-mod-log-file-4.md) | purrTTY | `logging` | `startup:all-mods-loaded` | Least code, shows in the in-game console; no file of your own; anything logged before the `Program` constructor is dropped |
| [Game-free logging interface swapped for a Brutal sink at load](./write-a-per-mod-log-file-5.md) | gatOS | `logging` | `startup:all-mods-loaded` | Test-friendly layered code; extra interface and a compile-gated seam |
| [Thread-safe warn-once set reset on unload](./write-a-per-mod-log-file-6.md) | KSA-AdvancedFlightComputer | `logging` | `frame:draw-ui`, `physics:vehicle-solver`, `thread:background` | Safe from workers; a lock and bounded keys |
| [HashSet-keyed WarnOnce and ErrorOnce with the exception type](./write-a-per-mod-log-file-7.md) | KSA-MeasureTools | `logging` | `frame:draw-ui`, `startup:all-mods-loaded` | Tiny and main-thread only; new exception types still log |

## Choosing

- Need a file players can attach: approach 1 (with 2's constant instead of 3's probing if you can reference `KSA.dll`).
- Want the game's console and file with no code: approach 4, or `DefaultCategory.Log` directly.
- Core library under test: approach 5.
- Anything that can fire every frame: add 6 or 7 on top of whichever sink you chose, and a fault budget
  ([Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)).

## See also

- [Warn when the running game build differs from the one a mod was tested on](../check-the-game-version/overview.md) (the build lines every log should start with), [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md),
  [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md)
- [Mod log file with rotation and a verbose toggle](../../../../mods/ksagolf/features/mod-log-file-with-rotation.md), [Per-mod log file, build stamp and fault limiter](../../../../mods/ksacars/features/mod-log-file.md),
  [Own log file, build stamps and a developer marker file](../../../../mods/ksarmory/features/own-log-file-and-developer-switch.md), [Per-mod log file beside KSA's logs](../../../../mods/ksastructures/features/mod-log-file.md),
  [Logging through a Brutal LogCategory in a shared assembly](../../../../mods/purrtty/features/brutal-logging-category.md), [Logging shim and perf stats](../../../../mods/gatos/features/logging-and-perfstats.md),
  [Warn-once logging, debug switches and a perf scope](../../../../mods/ksa-advancedflightcomputer/features/warn-once-logging-and-diagnostics.md),
  [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md),
  [Error budget and self-disable](../../../../mods/ksa-public-telemetry/features/error-budget-self-disable.md)
