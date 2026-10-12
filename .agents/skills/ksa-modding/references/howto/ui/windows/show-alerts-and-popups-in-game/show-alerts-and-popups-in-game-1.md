---
type: How-To
title: "Alerts: TimedAlert.Create and CreateWarning from main-thread hooks"
description: Raise centred, fading messages that look and stack exactly like the game's own alerts by calling TimedAlert from a main-thread hook, while keeping the call off worker threads
category: ui
group: windows
topic: show-alerts-and-popups-in-game
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/stock-timed-alerts]
techniques: [harmony-postfix, starmap-lifecycle]
hooks: [harmony-postfix KSA.FlightComputer.RaisePendingAlerts]
lifecycle: [frame:apply-solvers, frame:prepare, frame:draw-ui]
game_types: [KSA.TimedAlert, KSA.Alert, KSA.LStrings, KSA.Program, KSA.FlightComputer]
tags: [alert, toast, timed-alert, warning, notification, advanced-flight-computer]
---
The game's own centred text line ("Auto burn stopped: out of propellant.") is a public, static API: `TimedAlert.Create` and `TimedAlert.CreateWarning`. The Advanced Flight Computer talks to the player almost entirely through it, from about two dozen call sites: staging countdowns ("Decouple in 1.0s"), refusals ("Transfer calculating; wait for it to finish.") and planner hints. The messages look and stack exactly like stock ones and need no window of their own. Demonstrated by KSA-AdvancedFlightComputer. Prerequisites: StarMap, and Harmony if you raise alerts from patches.

## Background: how the game does it

- **API.** `TimedAlert.Create(string, byte4 color, double displayPlayerTime = 5.0)` registers an alert that expires at `Program.GetPlayerTime() + max(displayPlayerTime, 2.0)` and fades out over its last 2 seconds; `CreateWarning(string, double = 5.0)` is the same with the warning tone. The colour argument is only checked against cyan: cyan selects the subordinate tone, any other colour `Tone.Neutral`.[current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L62-L83)[^ksa-timedalert]
- **Storage.** A plain static `List<TimedAlert>` that `Create` appends to and sorts, and the draw walks and prunes.[current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L26-L55)[^ksa-timedalert] It is not synchronised.
- **Drawing.** `Alert.DrawAll` draws the load, fixed and timed lists on the foreground draw list from y = 150, centred in the main viewport; `Program.OnDrawUiFrame` calls it first (before `Popup.DrawAll`).[current/decomp/KSA/Alert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Alert.cs#L16-L24)[^ksa-alert][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3134)[^ksa-program]
- **HUD hidden.** Setting `Program.DrawUI = false` calls `TimedAlert.ClearAll()`, so hiding the HUD drops every pending alert.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L528-L539)[^ksa-program]

## Lifecycle and timing

Alerts are created from any main-thread hook that detects the condition: the post-solver tick (`frame:apply-solvers`, staging and RCS), the `FlightComputer.RaisePendingAlerts` postfix (`frame:prepare`, multi-pass completion) and planner draw patches (`frame:draw-ui`). They are drawn later in the same frame by `OnDrawUiFrame`. Because the list is unsynchronised, creating an alert from a physics worker or a `ThreadPool` task races the draw; AFC's hyperbolic-target patches run partly on the planner's worker tasks, so the alert is raised only from the patch that runs on the draw thread. Expiry uses player time while game logic runs on simulation time. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Call `TimedAlert.Create` with the countdown as the display time**, so the message lasts as long as the delay it announces:

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

2. **Use `CreateWarning` for refusals**, and reuse the game's own localized strings so the wording matches stock; pick the vehicle-named variant when the craft is not the controlled one:

```csharp
// AdvancedFlightComputer/Core/StockBurnMode.cs L26-L41
internal static bool GiveBackAuto(Vehicle vehicle, BurnTarget? heldTarget, bool takesStopBack = false)
{
    FlightComputer fc = vehicle.FlightComputer;
    if (fc.BurnMode != FlightComputerBurnMode.Manual || heldTarget == null || !ReferenceEquals(fc.Burn, heldTarget)
        || !vehicle.IsControllable)
        return false;
    if (!takesStopBack && WouldStopAtOnce(vehicle, heldTarget))
    {
        TimedAlert.CreateWarning(ReferenceEquals(vehicle, Program.ControlledVehicle)
            ? LStrings.AlertAutoBurnStopped.Format()
            : LStrings.AlertAutoBurnStoppedVehicle.Format<string>(vehicle.Id));
        return false;
    }
    Write(vehicle, FlightComputerBurnMode.Auto);
    return true;
}
```

3. **Say each thing once.** Repeating conditions are de-duplicated with a flag or a `HashSet` key, so the line is not re-raised every frame:

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

4. **Keep it on the main thread.** If the condition is detected on a worker, record a flag and raise the alert from a main-thread hook, as the comment in step 3 explains.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L468-L485) | Staging countdown alerts |
| [AdvancedFlightComputer/Core/StockBurnMode.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/StockBurnMode.cs#L26-L41) | Stock wording, vehicle-aware |
| [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L184-L202) | Once-per-target hint kept off worker threads |

## Variations and gotchas

- **The colour is not a colour.** `Color.Red`, `Color.Yellow` and `Color.Green` all render in the same neutral tone (`Tone.Neutral`) in this build (only cyan differs); use `CreateWarning` for the amber warning tone.
- **Display time is player time**, so under time warp a "Decouple in 2.0s" alert outlives the simulation delay it counts down; times below 2 s are raised to 2 s.
- **Alerts are not ImGui windows.** Creating one is legal outside the ImGui frame, but hiding the HUD clears them, so do not use them for anything the player must see later.
- **Never call from a physics worker or `ThreadPool` task.**
- **Version.** Checked against 2026.10.10.5554; `TimedAlert.Create` and `CreateWarning` are public API.

## See also

- [Overview of show-alerts-and-popups-in-game](overview.md)
- [Approach 2: Popup subclass drawn by Popup.DrawAll](show-alerts-and-popups-in-game-2.md)
- [Keep UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Show a dialog during loading](../show-a-dialog-during-loading/overview.md)
- Mod feature: [Stock timed alerts](../../../../mods/ksa-advancedflightcomputer/features/stock-timed-alerts.md)

[^ksa-timedalert]: KSA.TimedAlert (decompiled, build 2026.10.10.5554)
[^ksa-alert]: KSA.Alert (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
