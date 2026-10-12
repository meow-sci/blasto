---
type: How-To
title: Set the speed with Universe.SetSimulationSpeed and suppress the on-screen alert
description: Write the simulation speed directly from a drained command, why alert false matters, and what stock auto-warp and the menu pause do to the value
category: flight-and-orbits
group: flight-state
topic: set-simulation-speed-from-a-mod
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/debug-cheats-teleport-refuel-warp]
techniques: [command-queue]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.Universe]
tags: [time-warp, simulation-speed, alert, debug]
---

Outcome: a one-line speed change from mod code. gatOS exposes it as `debug.warp`.[Debug cheats for teleport, refuel, warp and impulse](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md)
Prerequisite: StarMap (any main-thread hook).

## Background: how the game does it

`Universe.SetSimulationSpeed(double simulationSpeed, bool alert = true)` assigns `_simulationSpeed`; when the value changes by more
than 0.001, is non-zero and `alert` is true it also creates a yellow `TimedAlert` with the new speed. That alert would appear in
footage, so gatOS passes `alert: false`.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2288-L2296) The overload taking a `SimSpeed` assigns the
value and nothing else.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703) There is no auto-warp guard in either: the refusal "cannot set simulation speed
while auto warp is active" exists only in the console command `SetSimulationSpeedDirect`. While auto-warp runs,
`ProcessAutoWarp` writes the speed again every `PrepareFrame` before the step is sized, so your write is overridden before a
step uses it.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L706-L740)

## Lifecycle and timing

gatOS drains its frame-phase commands in the main-viewport `GameViewport.OnFrame` prefix (`frame:viewports`), which is after
`PrepareFrame`; the speed it writes is therefore first read by the next frame's `GetJobSimStep`. The frame-phase commands only touch
main-thread globals, so the write is safe there. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Validate the value.** gatOS rejects NaN, infinity and negative factors; zero means paused.
2. **Call the game, silently:**

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs L16-L24
[KsaAnchor("Universe.SetSimulationSpeed(double, alert:false)", SourceFile = "KSA/Universe.cs",
    Verified = "2026-06-12", Risk = ChurnRisk.Medium, Notes = "Public; no reflection needed.")]
internal static CommandResult SetWarp(double factor)
{
    if (!double.IsFinite(factor) || factor < 0)
        return new CommandResult(CommandOutcome.Invalid, "warp must be a finite factor ≥ 0");
    Universe.SetSimulationSpeed(factor, alert: false);
    return CommandResult.Ok;
}
```

3. **Route it from where your commands arrive.** gatOS maps the action to the actuator in its catalog executor:

```csharp
// gatOS.GameMod/Game/Ksa/KsaCatalog.cs L40-L42
// Vessel-agnostic debug actions (no target vehicle to resolve).
if (command.Action == SimActions.DebugWarp)
    return Finish(accessor, DebugActuator.SetWarp(command.Value));
```

4. **Expect overrides.** The menu pause (`UpdateMenuPause`) and an active auto-warp rewrite the speed in `PrepareFrame`; read the speed
   back next frame if you need to know what stuck.

## Complete example

[gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/DebugActuator.cs) and [gatOS.GameMod/Game/Ksa/KsaCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaCatalog.cs#L31-L43).

## Variations and gotchas

- **A speed under 1e-4 is shown as "paused"** by `SimSpeed` naming, but `IsPaused()` tests exactly zero; if you want a slow but
  moving world, floor your value (KSArmory floors at 0.001).
- **Do not fight auto-warp.** Query `Universe.IsAutoWarpActive` first, or use the buffer ([approach 2](set-simulation-speed-from-a-mod-2.md)).
- The `SimSpeed` max is `99999999999999`; larger requests are meaningless.
- A write from a worker thread is a data race with `GetJobSimStep`; use the main thread.

## See also

- [Overview](overview.md); [AutoWarpBuffer](set-simulation-speed-from-a-mod-2.md); [feedback controller](set-simulation-speed-from-a-mod-3.md)
- [Debug cheats for teleport, refuel, warp and impulse](../../../../mods/gatos/features/debug-cheats-teleport-refuel-warp.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
