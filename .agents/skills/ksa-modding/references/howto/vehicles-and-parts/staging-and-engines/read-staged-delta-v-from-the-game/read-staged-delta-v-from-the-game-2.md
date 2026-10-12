---
type: How-To
title: Keep a private SequencePerformanceList with a change-gated recompute
description: Run the game's stage model on your own list instance, recompute only when the answer can have moved, and wait while the part tree has pending derived work so the UI never races the vehicle solver
category: vehicles-and-parts
group: staging-and-engines
topic: read-staged-delta-v-from-the-game
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-deltavmap/features/vehicle-staged-dv-readout]
techniques: [change-gating, reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.PartTree._derivedDirty]
lifecycle: [frame:draw-ui, frame:dispatch-solvers, frame:apply-solvers]
game_types:
  - KSA.PartTree
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.SequenceList
  - KSA.DerivedData
  - KSA.Vehicle
tags: [delta-v, change-gating, private-list, freshness, solver-race]
---
Outcome: a total (and per-stage) delta-v readout that is correct in flight whether or not the player has the staging window
open, from the UI draw, without racing the vehicle worker. KSA-DeltaVMap needs it for the "needed / available" bar of its
route panel. The mod does not re-derive anything: it runs the game's `SequencePerformanceList` on a **private instance**,
recomputes only when a cheap signature says the answer may have moved (at most four times a second), and in flight waits
whenever the part tree has pending derived data.

Prerequisites: StarMap, Harmony's `AccessTools`. Demonstrated by [ksa-deltavmap/vehicle-staged-dv-readout](../../../../mods/ksa-deltavmap/features/vehicle-staged-dv-readout.md).

## Background: how the game does it

See the [overview](overview.md). Why a private list:

- In flight stock refreshes the shared list on a vehicle worker, and only while the `Sequence` or the `EngineControl` gauge
  canvas is enabled; with both closed it is never refreshed, so reading it would freeze the readout
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L757-L798)[^ksa-physicsbubble].
- The published array is double-buffered, so per-sequence entries are rewritten in place two recomputes later
  [current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L289)[^ksa-sequenceperformancelist]. A private instance shares no simulation scratch with stock (all scratch fields are
  instance fields).

Why waiting on derived work is enough, checked against `Program.PrepareFrame`: it joins `VehicleSolver`, applies results, drains
`ApplyInputEvents`, runs `PartTree.FlushDirtyDerived()` / `FlushDirtyResourceManagers()` and then
`Universe.ExecuteNextVehicleSolvers` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2220)[^ksa-program]. The staging UI only enqueues `InputEvents` operations
(`SequenceChangeBuffer`, `StageChangeBuffer`), so a tree that is clean at the draw stays clean while the batch runs, and
`EnsureDerived` only reads. `SequencePerformanceList.Recompute` itself calls `EnsureDerived(SolidMotorStacks | Sequences)`
[current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L105-L120).

## Lifecycle and timing

Main thread, `StarMapAfterGui` (`frame:draw-ui`), each frame the map window is shown and the HUD is visible. The frame
position is after `frame:apply-solvers` and after `frame:dispatch-solvers`: the next step is running on workers, so main-thread
state is the result applied at the top of this frame and must only be read, and shared derived tree data must not be
rebuilt. Cost: a sum over a few dozen floats per frame, plus a stock recompute (single-digit milliseconds) at most every
0.25 s while the signature changes. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Resolve the tree**: the controlled vehicle's `Parts` in flight, else the editor's `EditingSpace.Parts`; create your own
   `new SequencePerformanceList(tree)` when the tree changes:

```csharp
// DeltaVMap/Dv/StagedDv.cs L184-191
    private static PartTree? ResolveTree(out bool inFlight)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        inFlight = vehicle != null;
        if (vehicle != null)
            return vehicle.Parts;
        return Program.Editor?.EditingSpace.Parts;
    }
```

2. **Gate the recompute with a signature**: total propellant mass (sum of `tree.Moles.States[i].Mass`), `tree.Count` and
   `tree.SequenceList.Sequences.Length`. Recompute only when there is no cached value, or 0.25 s have passed and the signature
   changed (propellant by at least 0.05 kg). Move the baseline only when a recompute actually runs, so a slow drain under the
   epsilon still accumulates; treat a clock that went backwards as elapsed:

```csharp
// DeltaVMap/Dv/StagedDv.cs L85-122
            if (!ReferenceEquals(tree, _tree))
            {
                Forget();
                _tree = tree;
                _analyzer = new SequencePerformanceList(tree);
            }

            double now = Program.GetPlayerTime();
            // The backwards test catches a clock reset (new game, save load) so the readout cannot
            // latch until the old timestamp is reached again.
            bool intervalElapsed = now - _lastComputeTime >= RecomputeIntervalSeconds || now < _lastComputeTime;

            double propellant = SumPropellantMass(tree);
            int partCount = tree.Count;
            int sequenceCount = tree.SequenceList.Sequences.Length;
            bool changed = partCount != _lastPartCount
                || sequenceCount != _lastSequenceCount
                || double.IsNaN(_lastPropellantMass)
                || Math.Abs(propellant - _lastPropellantMass) >= PropellantEpsilonKg;

            if (_totalDv == null || (intervalElapsed && changed))
            {
                // Pending work clears at the next PrepareFrame, so the readout keeps its last
                // value, or shows n/a for a frame on a fresh tree.
                if (inFlight && HasPendingDerivedWork(tree))
                    return _totalDv;

                // The baseline is the state the cached total was computed from, so it moves only
                // here. Advancing it on a frame that skipped the recompute would let a slow drain
                // stay under the epsilon forever and freeze the readout.
                _lastComputeTime = now;
                _lastPropellantMass = propellant;
                _lastPartCount = partCount;
                _lastSequenceCount = sequenceCount;
                _totalDv = Compute(_analyzer!, tree, inFlight);
            }
            return _totalDv;
        }
```

3. **In flight, skip while derived work is pending** by reading the private `PartTree._derivedDirty` through a bound field
   reference (only if the field exists, is an instance field and is of type `DerivedData`); if it is gone after an update,
   warn once and stop waiting:

```csharp
// DeltaVMap/Dv/StagedDv.cs L165-182
    private static bool HasPendingDerivedWork(PartTree tree)
    {
        if (DerivedDirty == null)
        {
            LogHelper.WarnOnce("staged-dv-derived-dirty",
                "[DvMap] PartTree._derivedDirty not found, staged dV no longer waits for the derived-data flush.");
            return false;
        }
        return (DerivedDirty(tree) & RecomputeInputs) != DerivedData.None;
    }

    private static AccessTools.FieldRef<PartTree, DerivedData>? BindDerivedDirty()
    {
        FieldInfo? field = AccessTools.DeclaredField(typeof(PartTree), "_derivedDirty");
        return field != null && field.FieldType == typeof(DerivedData) && !field.IsStatic
            ? AccessTools.FieldRefAccess<PartTree, DerivedData>(field)
            : null;
    }
```

4. **Recompute per context.** Flight: `RecomputeForFlight(0f)`. Editor: a fresh list per recompute (it starts dirty) and
   `RecomputeIfDirty()`; `SetDirty()` would mark the craft unsaved, and flight mode in the editor would drop stages because
   inserting a sequence raises `ActiveSequence`:

```csharp
// DeltaVMap/Dv/StagedDv.cs L193-229
    private static double Compute(SequencePerformanceList analyzer, PartTree tree, bool inFlight)
    {
        // Each sequence drains at its own Environment toggle in both modes. Flight mode matches
        // stock's flight readout, where the ambient pressure pins the display thrust of the active
        // sequence and a sequence numbered below SequenceList.ActiveSequence registers no engines.
        // Stock's editor readout runs the plain recompute, and inserting a sequence in the editor
        // raises ActiveSequence, so flight mode there would drop whole stages. The editor gets a
        // fresh list instead, which starts dirty, because SetDirty would also mark the craft as
        // unsaved. The editor recompute does not wait for the flush, because no job touches the
        // editor tree during the draw (stock queues its editor recompute after the UI draw), so any
        // derived rebuild it triggers is the one the next flush would run anyway.
        if (inFlight)
        {
            analyzer.RecomputeForFlight(0f);
        }
        else
        {
            analyzer = new SequencePerformanceList(tree);
            analyzer.RecomputeIfDirty();
        }

        ReadOnlySpan<Sequence> sequences = tree.SequenceList.Sequences;
        ReadOnlySpan<SequencePerformance> perf = analyzer.PerformanceSequences;
        int count = Math.Min(sequences.Length, perf.Length);

        if (_stages.Length < count)
            _stages = new StageMass[count];
        double total = 0.0;
        for (int i = 0; i < count; i++)
        {
            total += perf[i].DeltaV;
            _stages[i] = new StageMass(perf[i].DeltaV, perf[i].WetMass, perf[i].BurnedFuelMass);
        }
        _stageCount = count;
        _revision++;
        return total;
    }
```

5. **Copy out what you need** (`DeltaV`, `WetMass`, `BurnedFuelMass` per sequence) into your own array immediately; bump a
   `Revision` on each recompute so consumers can cache on it.
6. **Reset on unload** (`StarMapUnload`) so a reload cannot keep a stale vessel alive.

## Complete example

- [DeltaVMap/Dv/StagedDv.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/StagedDv.cs)[^ksa-deltavmap-stageddv]

## Variations and gotchas

- **Editor does not wait.** Stock queues its editor recompute after the UI draw and joins that worker first in `PrepareFrame`,
  so no job touches the editor tree during the draw.
- **One-frame `n/a`.** On a fresh tree the readout can show `n/a` for a frame while pending work clears.
- **Re-verify on each game update** the flush order, the main-thread-only dirtying, the private `_derivedDirty` field and that
  no `SequencePerformanceList` scratch field has moved to a static (the mod's own checklist).
- **Compare with approach 1:** a shared list is simpler but is only fresh when the player's gauges keep it so.

## See also

- [Overview](overview.md), [Article 1](read-staged-delta-v-from-the-game-1.md), [Article 3](read-staged-delta-v-from-the-game-3.md)
- [ksa-deltavmap/vehicle-staged-dv-readout](../../../../mods/ksa-deltavmap/features/vehicle-staged-dv-readout.md)
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)

[^ksa-deltavmap-stageddv]: KSA-DeltaVMap: DeltaVMap/Dv/StagedDv.cs
[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformancelist]: current/decomp/KSA/SequencePerformanceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
