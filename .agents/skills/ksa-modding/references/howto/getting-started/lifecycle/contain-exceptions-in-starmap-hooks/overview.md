---
type: Game Feature
title: Keep mod exceptions out of KSA's frame loop
description: Why an exception in a StarMap hook ends the game, and two fault-budget designs that log, disable the mod and undo its side effects
category: getting-started
group: lifecycle
topic: contain-exceptions-in-starmap-hooks
demonstrated_by:
  - mods/ksarmory/features/starmap-entry-and-fault-containment
  - mods/ksacars/features/mod-log-file
  - mods/ksastructures/features/frame-step-plumbing
  - mods/ksa-public-telemetry/features/error-budget-self-disable
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame, event:shutdown]
game_types: [KSA.Program, KSA.App]
tags: [starmap, exceptions, fault-containment, frame-loop, stand-down]
---

KSA's frame loop has no exception handler, and StarMap calls your hooks with `MethodInfo.Invoke` and no handler. A
bug in a frame hook therefore does not skip a frame; it unwinds out of `Program.Main` and closes the game, possibly
with the player's session unsaved. Mods that run per-frame code in a pre-alpha game need a containment policy.

## Engine background

| Where | Fact |
|---|---|
| [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L70) `Run` | `while (!_exitCode.HasValue) { ... OnFrame(totalSeconds, dtPlayer); }` with no `try` |
| [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2290) `OnFrame` | `PrepareFrame`, gauge canvases, ImGui, `OnDrawUiFrame`, `OnDrawUiViewports`, render in sequence; no handler. `OnDrawUiViewports` (L3162) has none either |
| [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L12-L48) | prefix on `OnDrawUiFrame`, postfixes on `OnDrawUiViewports` and `OnFrame` each loop over your methods with `method.Invoke(instance, args)` |
| Load-time hooks | `OnImmediateLoad` and `OnFullyLoaded` are not wrapped either; an exception propagates into `PrepareAll` or the `LoadAll` postfix |

`StarMapAfterOnFrame` fires on every return path of `OnFrame` (including the exit frame) while the two GUI hooks
only fire when `DrawUI` is true; timing detail in [StarMap lifecycle attributes](../game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Per-hook try/catch with a shared fault counter, unpatch at ten](./contain-exceptions-in-starmap-hooks-1.md) | KSArmory, KSACars | `starmap-lifecycle` | `frame:draw-ui`, `frame:after-on-frame` | Every fault surfaces and is counted; a rare fault eventually retires a long session since the counter never resets; stand-down must remove every installed patch |
| [Consecutive-failure budget that restores the stock HUD](./contain-exceptions-in-starmap-hooks-2.md) | KSA-Public-Telemetry | `starmap-lifecycle` | `startup:all-mods-loaded`, `frame:after-on-frame`, `frame:draw-ui` | Tolerates rare faults, logs the first of each streak only; an alternating fail/succeed pattern never trips; must undo visible side effects |

## Common design rules

- Wrap **every** per-frame hook and every load-time installer; a disabled mod returns at the top of each hook.
- Log the exception with context; rate-limit repeated ones ([Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md)).
- Do the clean-up on the fault path, not in `StarMapUnload`, because StarMap removes all Harmony patches before
  `StarMapUnload` ([Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md)).
- Exceptions inside Harmony patches are not on the hook call stack and need their own guards:
  `isolate-patch-failures-and-fault-proof-patch-bodies`.

## See also

- [Choose and use the right StarMap lifecycle hook](../choose-a-starmap-startup-hook/overview.md), [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md), [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md),
  [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md)
- [StarMap entry class with per-hook fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md), [Per-mod log file, build stamp and fault limiter](../../../../mods/ksacars/features/mod-log-file.md),
  [One step per frame across three hooks, on the simulation clock](../../../../mods/ksastructures/features/frame-step-plumbing.md), [Error budget and self-disable](../../../../mods/ksa-public-telemetry/features/error-budget-self-disable.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) - related topic

