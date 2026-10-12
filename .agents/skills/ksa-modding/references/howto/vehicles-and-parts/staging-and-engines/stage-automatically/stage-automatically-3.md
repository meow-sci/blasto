---
type: How-To
title: Hold decouplers and engines back after the staging trigger with sim-time deadlines
description: Reimplement SequenceList.ActivateNextSequence so engines and decouplers fire on separate deadlines, read per-part and per-sequence delays from TOML, and flush held modules on unload
category: vehicles-and-parts
group: staging-and-engines
topic: stage-automatically
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/staging-delays]
techniques: [sequence-reimplementation, reflection, toml-config, harmony-postfix]
hooks:
  - harmony-postfix KSA.Part.DrawPartInfo
  - reflection KSA.SequenceList.ActiveSequence
  - reflection KSA.SequenceList._updatingSequence
lifecycle: [frame:apply-solvers, frame:draw-ui]
game_types:
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.Part
  - KSA.ISequenced
  - KSA.EngineController
  - KSA.Decoupler
  - KSA.Universe
  - KSA.TimedAlert
tags: [staging, delays, reflection, sequence-list, toml]
---
Outcome: after a staging trigger, decouplers fire after one delay and engines after another, with delays configurable per
part variant and overridable per vehicle and sequence, so a booster can separate cleanly before the next stage ignites.
Prerequisites: StarMap, Harmony. Demonstrated by [ksa-advancedflightcomputer/staging-delays](../../../../mods/ksa-advancedflightcomputer/features/staging-delays.md).

## Background: how the game does it

`ActivateNextSequence` is all-or-nothing: it marks the row activated and calls `ActivateSubtreeInStage` for each part, so
every engine and decoupler in the row fires at once, and a skipped module never fires again because stock skips activated rows
forever [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171-L206)[^ksa-sequencelist]. To delay half of a row a mod must run the walk itself. See the [overview](overview.md).

## Lifecycle and timing

The trigger and the deadline check both run in `frame:apply-solvers` (article 1's `Evaluate`), and deadlines are absolute
sim-time values from `Universe.GetElapsedSeconds()`, not a countdown fed by the frame delta, because the detector also runs on
frames the game spends paused. The part window postfix runs in `frame:draw-ui`. The activations only queue into the input
buffers, which the game drains in `frame:input` the same frame. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Reimplement the activation.** Find the first unactivated row with parts, set `ActiveSequence` and the private
   `_updatingSequence` guard through reflection, mark the row activated, walk its parts in reverse like stock, and activate
   immediately except engines and decouplers with a positive delay, which are collected instead:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs L13-99
    public static PendingStaging? ActivateNextSequenceSplit(Vehicle vehicle)
    {
        StagingHelpers.InvalidateSequenceCache();
        SequenceList.RequestScrollToBottom();

        SequenceList seqList = vehicle.Parts.SequenceList;
        ReadOnlySpan<Sequence> sequences = seqList.Sequences;

        Sequence? target = null;
        for (int i = 0; i < sequences.Length; i++)
        {
            Sequence seq = sequences[i];
            if (seq.Activated)
                continue;
            if (seq.Parts.IsEmpty)
            {
                seq.Activated = true;
                continue;
            }
            target = seq;
            break;
        }

        if (target == null)
            return null;

        int seqNumber = target.Number;
        double engineDelay = StagingConfig.GetSequenceEngineDelay(vehicle, seqNumber);
        double decouplerDelay = StagingConfig.GetSequenceDecouplerDelay(vehicle, seqNumber);

        // The public SetActiveSequence is no substitute: it rewrites Activated list-wide and resets caches.
        GameReflection.SequenceList_ActiveSequence!.SetValue(seqList, seqNumber);
        TimedAlert.Create($"Sequence {seqNumber} activated", Color.Yellow, 3.0);

        // Stock's own guard: ResetCaches early-returns while this is set, so a re-entrant reset cannot rebuild the parts cache under the span below.
        GameReflection.SequenceList_updatingSequence!.SetValue(seqList, true);
        target.Activated = true;

        ReadOnlySpan<Part> parts = target.Parts;
        List<ISequenced>? pendingEngines = null;
        List<ISequenced>? pendingDecouplers = null;
        int firedNow = 0;

        // The same walk stock performs through Part.ActivateSubtreeInStage.
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            foreach (ISequenced module in parts[i].InSequence(seqNumber))
            {
                if (!module.IsActive && module is EngineController && engineDelay > 0.0)
                {
                    pendingEngines ??= new List<ISequenced>();
                    pendingEngines.Add(module);
                }
                else if (!module.IsActive && module is Decoupler && decouplerDelay > 0.0)
                {
                    pendingDecouplers ??= new List<ISequenced>();
                    pendingDecouplers.Add(module);
                }
                else
                {
                    module.Activate(vehicle);
                    firedNow++;
                }
            }
        }

        GameReflection.SequenceList_updatingSequence!.SetValue(seqList, false);
        seqList.ResetCaches();
        seqList.RemoveSpentSequences();
        StagingHelpers.InvalidateSequenceCache();

        // Nothing is drained or rebuilt here.
        // Activation only appends to IActivateInputBuffer. Program.PrepareFrame drains it, and PartTree.FlushDirtyDerived rebuilds the changed trees before the solvers run.
        // Doing either from the solver apply would mutate a list PhysicsBubble is iterating.

        if (DebugConfig.AutoStage)
            DefaultCategory.Log.Debug(
                $"[AFC] AutoStage activated sequence {seqNumber}: {parts.Length} part(s), {firedNow} module(s) fired now, " +
                $"{pendingEngines?.Count ?? 0} engine(s) held {engineDelay:F1}s, {pendingDecouplers?.Count ?? 0} decoupler(s) held {decouplerDelay:F1}s.");

        bool anyPending = pendingEngines is { Count: > 0 } || pendingDecouplers is { Count: > 0 };
        if (!anyPending)
            return null;

        return new PendingStaging(pendingDecouplers, decouplerDelay, pendingEngines, engineDelay,
            Universe.GetElapsedSeconds());
    }
```

2. **Hold with deadlines.** Record the pending modules and their deadlines in sim time:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs L121-150
// Deadlines in sim time, not a countdown fed by DeltaTime: the detector also runs on frames the game spends paused, where the last non-zero DeltaTime would keep draining a countdown.
internal sealed class PendingStaging
{
    public List<ISequenced>? DecouplerModules { get; private set; }
    public List<ISequenced>? EngineModules { get; private set; }

    public double DecouplerDelay { get; }
    public double EngineDelay { get; }
    public double DecouplerDeadline { get; }
    public double EngineDeadline { get; }

    public bool DecouplersPending => DecouplerModules is { Count: > 0 };
    public bool EnginesPending => EngineModules is { Count: > 0 };
    public bool AnyPending => DecouplersPending || EnginesPending;

    public PendingStaging(
        List<ISequenced>? decouplerModules, double decouplerDelay,
        List<ISequenced>? engineModules, double engineDelay, double now)
    {
        DecouplerModules = decouplerModules;
        EngineModules = engineModules;
        DecouplerDelay = decouplerDelay;
        EngineDelay = engineDelay;
        DecouplerDeadline = now + decouplerDelay;
        EngineDeadline = now + engineDelay;
    }

    public void ClearDecouplers() => DecouplerModules = null;
    public void ClearEngines() => EngineModules = null;
}
```

3. **Fire due modules every frame, first.** Even for an unarmed vehicle, because a row already marked activated is never
   offered again. Skip modules whose part left the vehicle in an earlier decouple:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L398-423
    // Fires whichever pending parts have reached their deadline.
    private static void TickPendingStaging(Vehicle vehicle, StagingState state, double now)
    {
        PendingStaging? p = state.Pending;
        if (p == null)
            return;

        if (p.DecouplersPending && now >= p.DecouplerDeadline)
        {
            StagingExecution.ActivatePendingModules(vehicle, p.DecouplerModules!, "decoupler");
            p.ClearDecouplers();
        }

        if (p.EnginesPending && now >= p.EngineDeadline)
        {
            StagingExecution.ActivatePendingModules(vehicle, p.EngineModules!, "engine");
            p.ClearEngines();
        }

        if (p.AnyPending)
            return;

        state.Pending = null;
        if (state.State == StagingState.Phase.AwaitingIgnition)
            BeginPropagation(state, Universe.GetElapsedSeconds());
    }
```
```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs L101-118
    // An earlier decouple may have carried a module's part away.
    public static void ActivatePendingModules(Vehicle vehicle, List<ISequenced> modules, string label)
    {
        foreach (ISequenced module in modules)
        {
            Part part = module.Parent.FullPart;
            if (part.Tree != vehicle.Parts)
            {
                DefaultCategory.Log.Warning(
                    $"[AFC] AutoStage: {label} {SequencedModules.Describe(module)} on '{part.DisplayName}' no longer belongs to the vehicle, skipping.");
                continue;
            }
            module.Activate(vehicle);
        }

        vehicle.Parts.SequenceList.ResetCaches();
        StagingHelpers.InvalidateSequenceCache();
    }
```

4. **Resolve the delay.** A per-sequence override wins over the longest per-part-variant delay in the row, per module kind:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs L265-276
    public static double GetDecouplerDelay(string partTemplateId)
        => DecouplerDelays.TryGetValue(partTemplateId, out double d) ? d : 0.0;

    // The per-sequence override wins over the longest variant delay in the row.
    public static double GetSequenceEngineDelay(Vehicle vehicle, int sequenceNumber)
        => TryGetOverride(_vehicleEngineOverrides, vehicle, sequenceNumber, out double delay)
            ? delay : ComputeSequenceEngineDelay(vehicle, sequenceNumber);

    public static double GetSequenceDecouplerDelay(Vehicle vehicle, int sequenceNumber)
        => TryGetOverride(_vehicleDecouplerOverrides, vehicle, sequenceNumber, out double delay)
            ? delay : ComputeSequenceDecouplerDelay(vehicle, sequenceNumber);

```

5. **Configuration layers.** `autostage.toml` holds the per-variant delays and the behaviour switches; one
   `autostage-vehicles/<id>.toml` per vehicle holds per-sequence overrides; nothing lives in the save:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs L8-20
// Two layers: autostage.toml holds the per-part-variant delays and the behaviour switches, autostage-vehicles/{id}.toml the per-sequence overrides, which win.
// Nothing lives in a save.
internal static class StagingConfig
{
    public const bool DropSpentStagesDefault = true;

    private static string _modDir = string.Empty;
    private static string _vehiclesDir = string.Empty;
    private static string _configPath = string.Empty;

    public static bool DropSpentStages { get; set; } = DropSpentStagesDefault;

    // Part template id to seconds.
```

6. **Edit it in the part window.** A Harmony postfix on `Part.DrawPartInfo` adds one delay block per (module kind, sequence)
   the part fires in, for the controlled vehicle only:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs L8-29
namespace AdvancedFlightComputer.Features.AutoStage;

// Delay settings in the pinned part window, one block per (module kind, sequence) the part fires in.
// A tower with a motor and two mounts on three rows gets three blocks.
[HarmonyPatch(typeof(Part), nameof(Part.DrawPartInfo), new Type[0])]
internal static class StagingDelayPartWindow
{
    // Shared scratch, safe because Part.DrawPartInfo never nests.
    private static readonly List<int> _engineSequences = new();
    private static readonly List<int> _decouplerSequences = new();

    static void Postfix(Part __instance)
    {
        try
        {
            DrawDelays(__instance);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("autostage-part-window", $"[AFC] AutoStage part window draw failed for '{__instance.Id}': {ex}");
        }
    }
```

7. **Flush on unload.** Modules still held when the mod unloads would be dead for the flight, so fire them first:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L542-559
    // Modules still held at unload are dead for the flight, because their row is already activated.
    internal static void FlushPendingForUnload()
    {
        _walk.Clear();
        _walk.AddRange(_states);
        foreach (KeyValuePair<Vehicle, StagingState> entry in _walk)
        {
            PendingStaging? p = entry.Value.Pending;
            if (p == null || entry.Key.IsDisposed)
                continue;

            DefaultCategory.Log.Info(
                $"[AFC] Unloading with a staging still pending on '{entry.Key.Id}': firing {p.DecouplerModules?.Count ?? 0} decoupler(s) and " +
                $"{p.EngineModules?.Count ?? 0} engine(s) now, because their sequence is already marked activated.");

            TickPendingStaging(entry.Key, entry.Value, double.PositiveInfinity);
        }
    }
```

## Complete example

- [AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs)[^ksa-advancedflightcomputer-stagingexecution]
- [AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs)[^ksa-advancedflightcomputer-stagingconfig]
- [AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs)[^ksa-advancedflightcomputer-stagingdelaypartwindow]

## Variations and gotchas

- **A second trigger while a staging is held** fires the pending modules early (`FlushPendingStaging`) because dropping them
  would leave the row unfired for good.
- **Reflection on private members.** `SequenceList.ActiveSequence` and `_updatingSequence` are set through `GameReflection`; the
  public `SetActiveSequence` is no substitute because it rewrites `Activated` list-wide and resets caches. Expect to re-verify
  after game updates.
- **Re-entrancy.** `ResetCaches` early-returns while `_updatingSequence` is set, which is why the guard is set during the walk.
- **Do not drain buffers or rebuild trees** from the apply postfix; the activations are appended to the input buffer and the
  game drains and rebuilds later, as the mod's comment notes.
- **Disposed vehicles abandon pending staging** rather than fire it, since their parts are going away.

## See also

- [Overview](overview.md), [Article 1](stage-automatically-1.md), [Article 2](stage-automatically-2.md)
- [ksa-advancedflightcomputer/staging-delays](../../../../mods/ksa-advancedflightcomputer/features/staging-delays.md)
- [Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md)

[^ksa-advancedflightcomputer-stagingdelaypartwindow]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingDelayPartWindow.cs
[^ksa-advancedflightcomputer-stagingconfig]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs
[^ksa-sequencelist]: current/decomp/KSA/SequenceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-stagingexecution]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingExecution.cs
