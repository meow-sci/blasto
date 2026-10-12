---
type: How-To
title: Patches on planner tasks that run on pooled threads
description: Four prefixes replace parts of the Transfer Planner's pooled searches for unbound targets; they run on thread-pool threads, share only a locked once-only logger, and leave the player alert to a finalizer on the draw thread
category: patching
group: harmony
topic: patch-methods-that-run-on-background-threads
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets]
techniques: [harmony-prefix, harmony-finalizer, background-thread]
hooks:
  - harmony-prefix KSA.OrbitalTransfers.HohmannFlight
  - harmony-prefix KSA.OrbitalTransfers.AlignmentTime
  - harmony-prefix KSA.RefineBurnTask.TryFindIntercept
  - harmony-prefix KSA.PatchedConic.FindClosestApproaches
lifecycle: [thread:background, frame:draw-ui, physics:vehicle-solver]
game_types: [KSA.TransferPlanner, KSA.OrbitalTransfers, KSA.RefineBurnTask, KSA.TransferTask, KSA.PatchedConic]
tags: [harmony, prefix, thread-pool, transfer-planner, thread-safety]
---

A patch body runs on whatever thread the game called the method from. The Transfer Planner searches on thread-pool
threads: `TransferTask.Run` computes the alignment time and fans out the porkchop sweep, and `RefineBurnTask.Run`
refines a chosen burn. KSA-AdvancedFlightComputer patches four methods that those threads reach so the planner can
target interstellar comets. This article is about the thread rules that follow: what the pooled patches may touch,
what they must not, and how a result produced on a pool thread reaches the UI.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in
[Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md).

## Background: how the game does it

| Target | Who calls it (build 2026.10.10.5554) | Thread |
|---|---|---|
| `OrbitalTransfers.AlignmentTime` | `TransferTask.Run` ([current/decomp/KSA/TransferTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferTask.cs#L167-L175)[^ksa-transfertask]), and the plan window's alignment block ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L478)[^ksa-transferplanner]) | pool, and main (draw) |
| `OrbitalTransfers.HohmannFlight` | `TransferPlanner.SetTransferInfo` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1587)); AFC's own `AlignmentTime` prefix calls it too | main (draw), and pool through AFC's prefix |
| `RefineBurnTask.TryFindIntercept` | `RefineBurnTask.Run`, queued with `ThreadPool.QueueUserWorkItem` ([current/decomp/KSA/RefineBurnTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RefineBurnTask.cs#L20-L39)[^ksa-refineburntask]) | pool |
| `PatchedConic.FindClosestApproaches` (private) | reached through `FlightPlan.CalculateTargetNodes`, called by the vehicle worker's update state ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L612)[^ksa-vehicleupdatestate], [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L641)) and by main-thread target changes ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2200)[^ksa-vehicle]) | vehicle workers, main |

The pooled work is started from the draw call: `TransferTask` queues `Run` with `ThreadPool.QueueUserWorkItem`
([current/decomp/KSA/TransferTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferTask.cs#L160-L165)) and `Run` then queues the porkchop worker callbacks ([current/decomp/KSA/TransferTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferTask.cs#L195-L200)).

## Lifecycle and timing

The plan window, `SetTransferInfo` and the finalizer run in `frame:draw-ui`, on the main thread. The prefixes on
`AlignmentTime`, `HohmannFlight` (via AFC's alignment prefix) and `TryFindIntercept` run on pool threads
(`thread:background`), possibly many at once; the `FindClosestApproaches` prefix can also run on vehicle workers in
`physics:vehicle-solver`. Results travel back through the objects the game already passes around (the
`TransferInfo`, the `PorkChopEntry`), which the game reads when `IsResultsReady` or the task's completion flag is
set. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).

## Steps

1. **List which thread reaches each target before writing the patch.** Use the table above: grep the decompile
   for every caller, and for every `ThreadPool`, `Task.Run` and `Parallel.` in the call chain. Note callers you
   create yourself: AFC's `AlignmentTime` prefix calls `HohmannFlight`, so the `HohmannFlight` prefix is reached from
   a pool thread even though stock calls it only from the draw thread.

2. **Allow only thread-safe shared state.** The `HohmannFlight` prefix returns the Hohmann estimate for an unbound
   end, builds the `UniverseTime` itself, and reports degenerate geometry through a locked once-only logger, because
   a `UniverseTime` refuses NaN. Its comment states the rule: "the dedup set is the only shared state touched here":

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L68-L98
[HarmonyPatch(typeof(OrbitalTransfers), nameof(OrbitalTransfers.HohmannFlight),
    new Type[] { typeof(Orbit), typeof(Orbit) })]
internal static class Patch_HohmannFlight
{
    static bool Prefix(Orbit origin, Orbit destination, ref UniverseTime __result)
    {
        if (origin.IsBound() && destination.IsBound())
            return true;

        double r1 = origin.IsBound() ? origin.SemiMajorAxis : origin.Periapsis;
        double r2 = destination.IsBound() ? destination.SemiMajorAxis : destination.Periapsis;
        double transferSma = (r1 + r2) * 0.5;
        double mu = origin.Parent.Mu;
        double tof = Math.PI * Math.Sqrt(transferSma * transferSma * transferSma / mu);

        // UniverseTime throws on NaN, and running the original instead would build one from the NaN apoapsis.
        // Zero is what every consumer of the estimate already tests for.
        // This runs on the porkchop worker as well as the draw thread, so the dedup set is the only shared state touched here.
        if (!double.IsFinite(tof))
        {
            LogHelper.WarnOnce(
                $"hohmann-tof-degenerate-{origin.Parent?.Id ?? "?"}",
                $"[AFC] HohmannFlight: degenerate geometry (r1={r1:E3}m r2={r2:E3}m " +
                $"mu={mu:E3}); reporting no transfer estimate.");
            __result = UniverseTime.Zero;
            return false;
        }
        __result = new UniverseTime(tof);
        return false;
    }
}
```

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L68-L98)[^ksa-advancedflightcomputer-transferplannerpatches]

3. **Make the shared helper itself thread-safe.** `LogHelper` is locked "because the porkchop worker reaches
   WarnOnce through `OrbitalTransfers.AlignmentTime` while the plan-window draw reaches it on the main thread", and
   the log call stays outside the lock:

```csharp
// AdvancedFlightComputer/Core/LogHelper.cs L7-L24
/// Log deduplication. A key fires once per mod load through <see cref="WarnOnce"/> or
/// <see cref="DebugOnce"/>, each level with its own key set, and <see cref="ThrottleAllows"/>
/// re-arms its key after the given spacing. Locked because the porkchop worker reaches WarnOnce
/// through <c>OrbitalTransfers.AlignmentTime</c> while the plan-window draw reaches it on the
/// main thread. The log call itself stays outside the lock.
/// </summary>
internal static class LogHelper
{
    private static readonly HashSet<string> _warned = new();
    private static readonly HashSet<string> _debugged = new();
    private static readonly Dictionary<string, long> _throttleLastTimestamp = new();
    private static readonly object _gate = new();

    public static void WarnOnce(string key, string message)
    {
        if (FirstTime(_warned, key))
            DefaultCategory.Log.Warning(message);
    }
```

Source: [AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs#L7-L24)[^ksa-advancedflightcomputer-loghelper]

4. **Never throw out of a pooled patch.** `TransferTask.Run` rethrows on the pool thread. The `AlignmentTime` prefix
   catches, logs each fault type once (the plan window calls it every frame) and falls through to the original:

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L205-L232
/// <summary>
/// Stock's <c>AlignmentTime</c> works from the synodic period, which is infinite
/// against an unbound target. The cheapest intercept of a comet is near its
/// periapsis, so the departure is placed one Hohmann time before that.
///
/// Runs on two threads. <c>TransferTask.Run</c> calls it from the ThreadPool
/// whenever the transfer source is not the vehicle, and the plan window's
/// "Show Parent/Target Alignment" block calls it every frame on the draw thread.
/// Nothing here may touch state that assumes one thread, which is why the player
/// alert lives in <see cref="Patch_SetTransferInfo"/>.
/// </summary>
[HarmonyPatch(typeof(OrbitalTransfers), nameof(OrbitalTransfers.AlignmentTime),
    new Type[] { typeof(OrbitalTransfers.TransferInfo), typeof(UniverseTime), typeof(double) })]
internal static class Patch_AlignmentTime
{
    static bool Prefix(OrbitalTransfers.TransferInfo transferInfo,
                       UniverseTime startTime,
                       double offsetDegrees,
                       ref UniverseTime __result)
    {
        try
        {
            // Celestial targets only.
            // Stock lists vehicles as targets with no eccentricity filter, and a vehicle on an escape trajectory in the same SOI has nothing to do with a heliocentric periapsis model.
            if (transferInfo.Target is not Celestial
                || transferInfo.Target.Orbit == null
                || transferInfo.Target.Orbit.IsBound())
                return true;
```

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L205-L232)

5. **Keep UI-thread-only effects out of pooled code.** `TimedAlert.Create` mutates an unsynchronized list that the
   draw thread walks, so the player alert ("ideal departure is already past") is raised only from the
   `SetTransferInfo` finalizer on the draw thread. The `AlignmentTime` prefix evaluates the same condition but only
   logs:

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

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs#L184-L202)

6. **Replace the work on the pool thread, return the result through the original's arguments.** The
   `TryFindIntercept` prefix builds the flight plan from the Lambert delta-v, measures the closest approach by
   sampling and writes into `ref PorkChopEntry`, then sets `__result` and returns `false`; bound targets return `true`
   and run the stock sweep:

```csharp
// AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs L24-L60
[HarmonyPatch(typeof(RefineBurnTask), nameof(RefineBurnTask.TryFindIntercept),
    new Type[] { typeof(OrbitalTransfers.TransferInfo), typeof(OrbitalTransfers.PorkChopEntry) },
    new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref })]
internal static class Patch_TryFindIntercept
{
    static bool Prefix(
        OrbitalTransfers.TransferInfo transferInfo,
        ref OrbitalTransfers.PorkChopEntry selectedEntry,
        ref bool __result)
    {
        try
        {
            if (transferInfo.Target?.Orbit == null
                || transferInfo.Target.Orbit.IsBound())
                return true;

            double3 dv = selectedEntry.TransferData.TransferDvVlf;
            FlightPlan flightPlan = FlightPlan.CreateUninitialized(transferInfo.Vehicle.Hash);

            if (OrbitalTransfers.BuildFlightPlan(ref flightPlan, transferInfo,
                    selectedEntry.TransferData.Start, dv,
                    out var closestPoint, out var _))
            {
                selectedEntry.FlightPlan = flightPlan;
                selectedEntry.TransferData.Point = closestPoint;

                double patchedConicDist = FindPatchedConicClosestApproach(
                    flightPlan, transferInfo.Target,
                    selectedEntry.TransferData.Transit.Seconds());
                selectedEntry.TransferData.ClosestApproachDistance = patchedConicDist;
                __result = true;

                if (DebugConfig.HyperbolicTargets)
                    DefaultCategory.Log.Debug(
                        $"[AFC] RefineBurnTask: hyperbolic target, " +
                        $"Lambert dV={dv.Length():F1} m/s, " +
                        $"patched conic miss={patchedConicDist / 1000:F0} km");
```

Source: [AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs#L24-L60)[^ksa-advancedflightcomputer-encounterpatches]

7. **Verify.** Select an interstellar comet and start a transfer search: the porkchop fills, the refine step
   completes, and nothing logs a thread-safety exception. A bound target takes the stock code path in every prefix.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs) | `HohmannFlight`, `AlignmentTime` prefixes and the window finalizer |
| [AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs) | `TryFindIntercept` and `FindClosestApproaches` prefixes |
| [AdvancedFlightComputer/Core/LogHelper.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LogHelper.cs) | Locked once-only logging |

## Variations and gotchas

- **Do not touch ImGui, Vulkan or main-thread collections** from a pooled patch. The test is not whether the *stock*
  method is thread-safe but whether *your* code is, on every thread that reaches it.
- **Mutable game objects are shared.** The prefixes write only to the arguments the game passes to that call.
- **A swallowed exception on a pool thread can vanish.** Log each fault type once; do not let a per-frame call
  flood the log.
- **Name the callers in a comment.** AFC's patch classes state which threads reach them, which is what a reviewer
  needs after a game update changes who calls what.
- **Finalizers run on the thread of the original call.** That is why the alert lives in the finalizer of the
  draw-thread method and not in the pooled ones.

## See also

- [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](patch-methods-that-run-on-background-threads-2.md), [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md), [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md), [Look the state up through __instance.BurnPlan in a ComputeControl prefix](patch-methods-that-run-on-background-threads-5.md)
- [Patching game methods that run on worker and pool threads](overview.md)
- [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md)
- [Swallow an exception and finish the method's remaining work](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md) for the finalizer on the draw thread
- Related tasks: write-worker-thread-safe-solver-patches, run-an-expensive-solve-without-stalling-the-frame

[^ksa-transfertask]: KSA.TransferTask (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-refineburntask]: KSA.RefineBurnTask (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-transferplannerpatches]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs
[^ksa-advancedflightcomputer-loghelper]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/LogHelper.cs
[^ksa-advancedflightcomputer-encounterpatches]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/HyperbolicTargets/EncounterPatches.cs
