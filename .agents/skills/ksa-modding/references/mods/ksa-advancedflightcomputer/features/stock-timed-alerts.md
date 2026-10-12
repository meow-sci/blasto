---
type: Mod Feature
title: Player messages through the game's timed alerts
description: Show transient centred messages with TimedAlert.Create and CreateWarning from main-thread hooks, reuse the game's localized alert strings with the controlled-vehicle variant, and keep worker-thread code from touching the unsynchronized alert list
mod: ksa-advancedflightcomputer
category: ui
techniques: [stock-ui-reuse]
hooks:
  - harmony-postfix KSA.Universe.ApplyVehicleSolvers
  - harmony-postfix KSA.FlightComputer.RaisePendingAlerts
  - harmony-prefix KSA.TransferPlanner.DrawPlanWindow
lifecycle: [frame:apply-solvers, frame:prepare, frame:draw-ui]
game_types: [KSA.TimedAlert, KSA.Alert, KSA.LStrings, KSA.Program]
tags: [alerts, notifications, hud, localization, thread-safety]
---

AFC talks to the player almost entirely through the game's own alert line: the centred text that stock
uses for "Auto burn stopped: out of propellant". About two dozen call sites across twelve files raise
staging countdowns ("Decouple in 1.0s"), refusals ("Transfer calculating; wait for it to finish."),
multi-pass failures and planner hints, so the messages look and stack exactly like stock ones and need no
window of their own.

## How it works

| Game piece | Behavior AFC relies on |
|---|---|
| `TimedAlert.Create(string, byte4 color, double displayPlayerTime = 5.0)` | Registers an alert that expires at `Program.GetPlayerTime() + max(displayPlayerTime, 2.0)` and fades out over its last 2 s; the list is kept sorted by expiry ([current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L45-L80))[^ksa-timedalert] |
| `TimedAlert.CreateWarning(string, double = 5.0)` | Same with the warning tone (`ConsoleStyle.PendingU32`) |
| Colour argument | Only checked against cyan: cyan selects the subordinate tone (`TextLabelU32`), any other colour the primary tone (`TextPrimaryU32`) ([current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L80-L98)) |
| `Alert.DrawAll` | Draws `LoadAlert`, `FixedAlert` then `TimedAlert` lists on the foreground draw list from y = 150 ([current/decomp/KSA/Alert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Alert.cs#L17-L25)); called from `Program.OnDrawUiFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3133)) |
| `Program.DrawUI = false` | `TimedAlert.ClearAll()`: hiding the HUD drops every pending alert ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L540)) |

Patterns in AFC:

- **Countdowns.** Staging delays show "Decouple in X.Xs" and "Ignition in X.Xs" with the delay itself as the
  display time ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L473-L482)).
- **Reuse stock wording.** When AFC declines to hand Auto back to a burn that would stop at once, it raises the
  same `LStrings.AlertAutoBurnStopped` / `AlertAutoBurnStoppedVehicle` pair stock raises in
  `FlightComputer.RaisePendingAlerts`, picking the vehicle-named variant when the craft is not
  `Program.ControlledVehicle` ([AdvancedFlightComputer/Core/StockBurnMode.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnMode.cs#L32-L38),
  [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L555-L564),
  [current/decomp/KSA/LStrings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LStrings.cs#L71-L73)).
- **Once per subject.** Repeating conditions are de-duplicated with a flag or a `HashSet` key (once per
  staging row held for the control module; once per hyperbolic target).
- **Refusal feedback.** Context-menu shortcuts and the Hohmann create interceptor explain why nothing
  happened instead of failing silently ([AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/ManeuverTools/BurnMenuLauncher.cs#L95-L105),
  [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs)).

## Lifecycle and timing

Alerts are created from whatever main-thread hook detects the condition: the post-solver tick
(`frame:apply-solvers`, staging and RCS), the `RaisePendingAlerts` postfix (`frame:prepare`, multi-pass
completion), and planner draw patches (`frame:draw-ui`). They are drawn later in the same frame by
`OnDrawUiFrame`. `TimedAlert` keeps a plain static `List<TimedAlert>` that `Create` appends to and sorts,
and the draw walks and prunes it, so creating an alert from another thread races the draw. AFC's
hyperbolic-target patches run partly on the planner's `ThreadPool` tasks, so the alert is raised only from
the patch that runs on the draw thread, with a comment saying why
([AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L184-L202)).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs) | Staging countdowns and hold notice |
| [AdvancedFlightComputer/Core/StockBurnMode.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnMode.cs) | Stock localized warning, vehicle-aware |
| [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs) | Alert deliberately kept off worker threads |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassController.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassController.cs) | Multi-pass failure messages |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs) | RCS burn refusals |

## Snippets

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L473-L482
        if (pending != null)
        {
            state.Pending = pending;
            state.ActivationEngines = null;
            state.State = StagingState.Phase.AwaitingIgnition;

            if (pending.DecouplersPending && pending.DecouplerDelay > 0.0)
                TimedAlert.Create($"Decouple in {pending.DecouplerDelay:F1}s", Color.Yellow, pending.DecouplerDelay);
            if (pending.EnginesPending && pending.EngineDelay > 0.0)
                TimedAlert.Create($"Ignition in {pending.EngineDelay:F1}s", Color.Yellow, pending.EngineDelay);
```

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L184-L202
    /// <summary>Says once per target that the ideal departure, one transfer time
    /// before the target's periapsis, is already behind the sim time, which is
    /// why the porkchop window looks degenerate. <see cref="Patch_AlignmentTime"/>
    /// evaluates the same condition but also runs on the porkchop worker, and
    /// <c>TimedAlert.Create</c> mutates an unsynchronized list the draw thread walks.</summary>
    private static void MaybeAlertDepartureWindowPast(IOrbiter target, UniverseTime hohmannToF)
    {
        UniverseTime ideal = target.Orbit.TimeAtPeriapsis - hohmannToF;
        if (!(ideal < Universe.GetElapsedTime())) return;

        string targetId = target.Id;
        if (!_alertedTargets.Add(targetId)) return;

        // An unbound target has one periapsis passage, and TransferTask.Run sweeps from the current sim time whatever AlignmentTime returned, so this does not promise a later window.
        TimedAlert.Create(
            $"{targetId}: the ideal departure, one transfer time before its periapsis, " +
            "has already passed. The porkchop window starts from the current time.",
            Color.Yellow, 6.0);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `TimedAlert.Create`, `CreateWarning`, `ClearAll`, `DrawList` ([current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs)) | The alert line |
| `Alert.DrawAll`, `DrawCenteredAlertText` ([current/decomp/KSA/Alert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Alert.cs)) | Where and how alerts are drawn |
| `Program.GetPlayerTime`, `DrawUI`, `ControlledVehicle` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5263)) | Expiry clock, HUD toggle, vehicle-aware text |
| `LStrings.AlertAutoBurnStopped`, `AlertAutoBurnStoppedVehicle` ([current/decomp/KSA/LStrings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LStrings.cs#L71-L73)) | Stock localized text reused verbatim |

## Pitfalls and version notes

- The colour is not a colour: AFC passes `Color.Red`, `Color.Yellow` and `Color.Green`, which all render in
  the same primary tone in this build. Use `CreateWarning` for the amber warning tone, cyan for the dimmer
  subordinate tone.
- Expiry uses player time while AFC's staging delays run on simulation time
  (`Universe.GetElapsedSeconds`), so under time warp a "Decouple in 2.0s" alert outlives the delay it
  counts down; display times below 2 s are raised to 2 s.
- Alerts are not ImGui windows: creating one is legal outside the ImGui frame, but hiding the HUD clears
  them, so do not use them for anything the player must see later.
- Never call `TimedAlert.Create` from a physics worker, a `ThreadPool` task or a solver thread; post a flag
  and raise it from a main-thread hook.

## See also

- [Staging delays](staging-delays.md)
- [Transfer Planner targets on hyperbolic orbits](hyperbolic-transfer-targets.md)
- [Vehicle control ownership](vehicle-control-ownership.md)
- [Stock-styled console UI](stock-styled-console-ui.md)
- [Alerts: TimedAlert.Create and CreateWarning from main-thread hooks](../../../howto/ui/windows/show-alerts-and-popups-in-game/show-alerts-and-popups-in-game-1.md) - how-to article demonstrating this feature

[^ksa-timedalert]: KSA.TimedAlert (decompiled, build 2026.10.10.5554)
