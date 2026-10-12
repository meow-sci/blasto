---
type: Mod Feature
title: Vehicle staged delta-v readout
description: Reads the vessel's total staged delta-v from a private instance of the game's SequencePerformanceList, gated by a cheap change signature and a reflected PartTree._derivedDirty check so the UI draw never races the vehicle solver
mod: ksa-deltavmap
category: vehicles-and-parts
techniques: [reflection, stock-model-reuse, change-gating, imgui-drawlist]
hooks: [starmap StarMapAfterGui, reflection KSA.PartTree._derivedDirty]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.PartTree
  - KSA.DerivedData
  - KSA.SequenceList
  - KSA.MoleState
  - KSA.Program
  - KSA.Vehicle
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.PhysicsBubble
  - KSA.Universe
  - KSA.ResourceGroups
tags:
  - delta-v
  - staging
  - sequence-performance
  - part-tree
  - threading
  - reflection
  - vehicle-solver
  - imgui-drawlist
  - editor
---

The route panel ends with a bar that reads "needed / available": the route's estimated delta-v
against the vessel's total staged delta-v, green, yellow or red. The available figure is not
re-derived by the mod. It runs the game's own staged model, `SequencePerformanceList`, which
simulates per-reactant drain with cutoff groups, cross-stage fuel attribution and solid residue and
integrates the rocket equation piecewise; a hand-rolled stage walk could not match it or keep
matching it across game updates.[^dvm-stageddv]

The interesting part is doing that from the UI draw, while the next vehicle physics step is already
running on worker threads. The mod uses a private list instance, recomputes only when a cheap
signature says the answer could have moved (at most four times a second), and in flight waits
whenever the part tree has pending derived data, which it detects through a reflected private field.
The same per-sequence masses feed the interstellar rocket-equation fuel estimate.

## How it works

### Which tree, which list

`StagedDv.ResolveTree` takes `Program.ControlledVehicle.Parts` in flight, else
`Program.Editor?.EditingSpace.Parts` in the vehicle editor, and remembers which. On a new tree it
creates `new SequencePerformanceList(tree)` and keeps it; the vessel's own
`PartTree.PerformanceSequences` instance is never touched, for two reasons the code documents and the
decomp confirms:[^dvm-stageddv]

| Reason | Decompiled evidence |
|---|---|
| In flight stock refreshes the shared list on a vehicle-solver worker, and only while the `Sequence` or the `EngineControl` gauge canvas is enabled. Both canvases start enabled, so with the default UI the list is being rewritten every step while the UI reads it; with both closed it is never refreshed and reading it would freeze the readout | `PhysicsBubble.RunVehiclePostWorkInner` calls `ReadOnlyVehicle.Parts.PerformanceSequences.RecomputeForFlight(...)` only when `vehicleState.IsControlled` and either `ResourceGroups.IsOpen` or `Program.EngineControlGaugeOpen` is true; `ResourceGroups.IsOpen` is `Program.SequenceCanvas?.Enabled`, and `GaugeCanvas._enabled` starts `true` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L143))[^ksa-physicsbubble][^ksa-resourcegroups][^ksa-gaugecanvas] |
| The published array is double-buffered, so entries are rewritten in place two recomputes later | `Recompute` ends with `_performanceScratch = Interlocked.Exchange(ref _performanceSequences, performanceScratch)`[^ksa-sequenceperformancelist] |

All of `SequencePerformanceList`'s scratch (`_performanceScratch`, `_simMoleMass`, `_drainRate`,
`_sequencePartsScratch`, `_activeCores` and the rest) are instance fields, so a private instance
shares no simulation scratch with stock. The mod also copies `DeltaV`, `WetMass` and
`BurnedFuelMass` of each `SequencePerformance` into its own `StageMass[]` right after the recompute.[^ksa-sequenceperformance][^dvm-rocketpropellant]

### Flight versus editor recompute

- Flight: `analyzer.RecomputeForFlight(0f)`. In flight mode a sequence numbered below
  `SequenceList.ActiveSequence` registers no engines, and the ambient pressure only pins the display
  thrust of the active sequence; each sequence still drains at its own `Environment` (101,325 Pa
  for atmospheric, 0 for vacuum), which is what stock's flight readout shows.[^ksa-sequenceperformancelist]
- Editor: a fresh `new SequencePerformanceList(tree)` per recompute (it starts with `_dirty = true`)
  and `RecomputeIfDirty()`. Calling `SetDirty()` instead would also set
  `PartTree.HasUnsavedChanges = true` and mark the craft unsaved, and flight mode in the editor would
  drop stages because inserting a sequence raises `ActiveSequence` (`SetActiveSequence(ActiveSequence + 1)`).[^ksa-sequenceperformancelist][^ksa-sequencelist]

### Change gating

A stock recompute costs single-digit milliseconds, so it never runs per frame. Every frame
`TryTotalDv` builds a signature: total propellant mass (a sum over `tree.Moles.States[i].Mass`),
`tree.Count` and `tree.SequenceList.Sequences.Length`. It recomputes only when there is no cached
value, or when at least 0.25 s of `Program.GetPlayerTime()` has passed and the signature changed
(part count, sequence count, or propellant by at least 0.05 kg). The baseline moves only when a
recompute actually runs, so a slow drain under the epsilon still accumulates; a clock that went
backwards (new game, save load) counts as elapsed. A coasting vessel or an untouched editor craft
therefore never recomputes.[^dvm-stageddv]

`Revision` is bumped on each recompute and when a reading is dropped, so the interstellar section
can key its own cache on the staged state without probing the tree again.[^dvm-interstellarsection]

### Not racing the vehicle solver

Before a flight recompute, `HasPendingDerivedWork` reads the private `PartTree._derivedDirty`
through `AccessTools.FieldRefAccess<PartTree, DerivedData>` (bound once, only if the field exists,
is an instance field and is of type `DerivedData`) and skips the recompute while any of
`SubstanceStores | SolidMotorStacks | Sequences` is pending, returning the last value (or `null` for
one frame on a fresh tree). If the field is gone after an update it logs a warning once and stops
waiting rather than failing.[^dvm-stageddv]

Why that is enough, checked against the decomp:

1. `SequencePerformanceList.Recompute` calls `_partTree.EnsureDerived(SolidMotorStacks | Sequences)`
   and reads `_partTree.SubstanceStores`, whose getter calls `EnsureDerived(SubstanceStores)`.
   `EnsureDerived` computes `Closure(needed) & _derivedDirty`, and only when that is non-zero clears
   the bits and rebuilds shared tree state (`RebuildSubstanceStores`, `RecomputeSolidMotorStacks`,
   `SequenceList.ResetCaches`). On a clean tree it is a read.[^ksa-parttree]
2. `Program.PrepareFrame` joins `JobSystems.VehicleSolver` (after the nearest-orbit worker and the
   orbit solvers), applies results, then near its end runs `Universe.ExecuteNextClothSolvers`,
   `PartTree.FlushDirtyDerived()` (which `EnsureDerived(All)`s every enrolled tree),
   `PartTree.FlushDirtyResourceManagers()`, `Universe.ExecuteNextVehicleSolvers` and
   `ExecuteNextOrbitSolvers` (decomp `Program.cs` L2141-L2236).[^ksa-program]
3. `ExecuteNextVehicleSolvers` queues `_vehicleUpdateTask` on `JobSystems.VehicleSolver`; that task
   calls `PhysicsBubble.RunPostWorkAndCluster`, which reaches `RunVehiclePostWorkInner` and the
   shared list's `RecomputeForFlight`, and therefore `EnsureDerived` on the same tree, on a worker
   thread while the UI draws.[^ksa-universe][^ksa-vehicleupdatetask][^ksa-physicsbubble]
4. Every `MarkDerivedDirty` call goes through `PartTree.RecomputeAllDerivedData`. The callers traced
   are main-thread and, in flight, all run before the flush: `InputEvents.ApplyInputEvents` (in
   `PrepareFrame`, including `Decoupler.Decouple`), the docking and part-failure render events
   (`DockingEvent.Apply`, `PartFailureEvent.Apply`, run by
   `VehicleUpdateTask.ApplyResultsToMainThread` in `Universe.ApplyVehicleSolvers`), and the editor.
   The staging UI does not edit the tree while it draws: `SequenceList` and `ResourceGroupList`
   enqueue their add, remove, reorder and place operations into `InputEvents.SequenceChangeBuffer`
   and `StageChangeBuffer`, which `ApplyInputEvents` applies at the next `PrepareFrame`
   ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1131-L1132)).[^ksa-inputevents][^ksa-dockingevent][^ksa-partfailureevent][^ksa-sequencelist]

So a tree that is clean at the draw stays clean while the batch runs, both `EnsureDerived` calls
only read, and the mod's recompute does not rebuild anything the solver is reading. The worker
writes module states into the `VehicleUpdateData` state updaters (`Moles`, `RocketNozzles`), whose
`GetNewStates` copies the live list into the updater's own buffer on first write; the live
`PartTree.Moles` list is overwritten only by `PartTree.UpdateFromTaskResults` ->
`UpdateBulkStates` during `Universe.ApplyVehicleSolvers`, which the main thread joins. So the masses
the recompute reads are stable during the draw, and the worker's own `RecomputeForFlight` reads the
same live list.[^ksa-vehicleupdatedata][^ksa-modulestateful][^ksa-parttree]

The editor path does not wait: stock queues its editor `SequencePerformanceJob` (which runs
`RecomputeIfDirty` on the editor list) on `NearestOrbitAndPerformanceWorker` after the UI draw and
before `ImGui.Render`, and `PrepareFrame` joins that worker first, so no job touches the editor tree
during the draw.[^ksa-program][^ksa-sequenceperformancejob]

### The comparison bar

`MapWindow.TryGetAvailableDv` passes `StagedDv.TryTotalDv()` through, dropping non-finite values;
`RoutePanelRenderer.DrawVehicleBar` shows "Vehicle dV: n/a" for `null`, otherwise fills a
`GetWindowDrawList()` rectangle with `needed / available` of the width and colors it green when
`needed <= 1` or `available >= 1.1 * needed`, yellow at `>= 0.9 * needed`, red below. The label
`"~needed / available m/s"` is re-formatted only when the rounded values change and drawn twice for
a drop shadow; `ImGui.Dummy` reserves the row.[^dvm-mapwindow][^dvm-routepanel] `needed` already
includes the piloting margin, the plane change and the return trip when those are on.

## Lifecycle and timing

- Where: main thread, `[StarMapAfterGui]` (StarMap's postfix on `Program.OnDrawUiViewports`,
  `frame:draw-ui`), from `MapWindow.DrawPanel` each frame the map window is shown and the HUD is
  visible. Never while the window is closed.
- Frame position: after `frame:apply-solvers` and after `frame:dispatch-solvers`. The vehicle step
  for the next frame is running on workers, so main-thread vessel state is the result applied at the
  top of this frame and must only be read, and shared derived tree data must not be rebuilt. The
  staging window drawn later in `OnDrawUiThreadSafe` only queues `InputEvents` operations, so a
  staging edit dirties the tree in the next `PrepareFrame`, before that frame's
  `FlushDirtyDerived`, and the mod reads a clean tree again.
- Cost: a sum over a few dozen floats per frame; a stock recompute at most every 0.25 s while the
  signature changes (throughout a burn), otherwise never.
- Unload: `[StarMapUnload]` calls `StagedDv.Reset()`, dropping the cached tree and analyzer so a
  reload cannot keep a stale vessel alive.[^dvm-mod]

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Dv/StagedDv.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/StagedDv.cs#L34-L230) | Tree resolution, private analyzer, signature gate, `_derivedDirty` wait, flight vs editor recompute |
| [DeltaVMap/Dv/RocketPropellant.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/RocketPropellant.cs#L7-L19) | `StageMass`, the owned per-sequence copy |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1533-L1540) | `TryGetAvailableDv`, called from `DrawPanel` (L462-L486) |
| [DeltaVMap/Render/RoutePanelRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/RoutePanelRenderer.cs#L295-L348) | The comparison bar |
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L74-L90) | Reset on unload |
| [DeltaVMap.HarnessTests/StagedDvTest.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap.HarnessTests/StagedDvTest.cs#L83-L91) | Headless test that a flight read waits for the derived flush, matches stock's accumulator and is reproducible[^dvm-stageddvtest] |

## Snippets

```csharp
// DeltaVMap/Dv/StagedDv.cs L85-L121
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
```

```csharp
// DeltaVMap/Dv/StagedDv.cs L46-L49, L165-L191
    private const DerivedData RecomputeInputs =
        DerivedData.SubstanceStores | DerivedData.SolidMotorStacks | DerivedData.Sequences;

    private static readonly AccessTools.FieldRef<PartTree, DerivedData>? DerivedDirty = BindDerivedDirty();
    // ...
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

    private static PartTree? ResolveTree(out bool inFlight)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        inFlight = vehicle != null;
        if (vehicle != null)
            return vehicle.Parts;
        return Program.Editor?.EditingSpace.Parts;
    }
```

```csharp
// DeltaVMap/Dv/StagedDv.cs L204-L229
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
```

```csharp
// DeltaVMap/Render/RoutePanelRenderer.cs L306-L325
        ImGui.Text("Vehicle dV"u8);

        byte4 fill = (needed <= 1.0 || available >= 1.1 * needed) ? Green
            : available >= 0.9 * needed ? Yellow
            : Red;

        float2 pos = ImGui.GetCursorScreenPos();
        float width = Math.Max(40f, ImGui.GetContentRegionAvail().X);

        // The bar reads "needed out of available": it fills with the fraction of the
        // vehicle's dV this route spends, so a cheap route barely fills it and a route that
        // exceeds the vehicle's dV fills it completely (and reads red).
        double ratio = available > 1.0 ? needed / available : (needed > 0.0 ? 1.0 : 0.0);
        float fillWidth = (float)(width * Math.Clamp(ratio, 0.0, 1.0));

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 barMax = pos + new float2(width, BarHeight);
        float2 fillMax = pos + new float2(fillWidth, BarHeight);
        dl.AddRectFilled(in pos, in barMax, BarBg, 3f);
        dl.AddRectFilled(in pos, in fillMax, fill, 3f);
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `new SequencePerformanceList(PartTree)`, `RecomputeForFlight(float)`, `RecomputeIfDirty()`, `PerformanceSequences` | [current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs) | The staged model itself, on a private instance[^ksa-sequenceperformancelist] |
| `SequencePerformance.DeltaV`, `WetMass`, `BurnedFuelMass` | [current/decomp/KSA/SequencePerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformance.cs) | Total and per-stage masses[^ksa-sequenceperformance] |
| `PartTree.Count`, `Moles.States`, `SequenceList.Sequences`; private `_derivedDirty` (reflection) | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs) | Change signature; pending-derived-work test[^ksa-parttree] |
| `DerivedData` flags | [current/decomp/KSA/DerivedData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DerivedData.cs) | `SubstanceStores`, `SolidMotorStacks`, `Sequences` mask[^ksa-deriveddata] |
| `Program.ControlledVehicle`, `Program.Editor`, `Program.GetPlayerTime()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Tree source and the 0.25 s clock (`_lastPlayerTime`)[^ksa-program] |
| `VehicleEditingSpace.Parts` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs) | Editor craft[^ksa-vehicleeditingspace] |
| `SequenceList.ActiveSequence`, `SetActiveSequence` (read for reasoning only) | [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs) | Why the editor uses the plain recompute[^ksa-sequencelist] |
| `PhysicsBubble.RunVehiclePostWorkInner` (not called) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs) | The concurrent stock recompute the mod avoids racing[^ksa-physicsbubble] |

## Pitfalls and version notes

- Verified in build 2026.10.10.5554: the `PrepareFrame` order (join solvers, apply,
  `FlushDirtyDerived`, dispatch), the solver-side `RecomputeForFlight` gate, the
  `Interlocked.Exchange` double buffer, that every `SequencePerformanceList` scratch field is an
  instance field, that `_derivedDirty` is a private instance `DerivedData` field (initialized to
  `All`), and that the editor job is queued after the UI draw and joined at the next `PrepareFrame`.
- Not exhaustively verified: that no `RecomputeAllDerivedData` caller can ever run on a worker. The
  traced callers (input events, docking, editor, staging UI, tree creation and split/merge through
  those) are main-thread; a future engine path that dirties a tree from a job would reopen the race.
  The code comment asks to re-check this, the flush order and the private field on every update.
- The `_derivedDirty` wait therefore almost never triggers in flight in this build; it covers a
  tree dirtied on the main thread after the flush (for example by a mod or a future stock path), in
  which case the readout keeps its last value until the next flush.
- `RecomputeForFlight(0f)` uses vacuum for the active sequence's display thrust only; the total is
  stock's per-sequence environment model, not a live-pressure figure.
- If `Program.ControlledVehicle` is set while the editor is open, the flight tree wins.
- The readout lags real propellant by up to 0.25 s plus one frame when the tree is dirty; the bar
  reads "n/a" for one frame on a freshly created tree.

## See also

- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Headless harness tests](./headless-harness-tests.md)
- [Map window and menu entry](./map-window-and-menu-entry.md)
- [KSA-DeltaVMap overview](../overview.md)
- [Keep a private SequencePerformanceList with a change-gated recompute](../../../howto/vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/read-staged-delta-v-from-the-game-2.md) - how-to article demonstrating this feature

[^dvm-stageddv]: DeltaVMap/Dv/StagedDv.cs
[^dvm-rocketpropellant]: DeltaVMap/Dv/RocketPropellant.cs
[^dvm-routepanel]: DeltaVMap/Render/RoutePanelRenderer.cs
[^dvm-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvm-interstellarsection]: DeltaVMap/Render/InterstellarSection.cs
[^dvm-mod]: DeltaVMap/Mod.cs
[^dvm-stageddvtest]: DeltaVMap.HarnessTests/StagedDvTest.cs
[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformance]: KSA.SequencePerformance (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-deriveddata]: KSA.DerivedData (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-resourcegroups]: KSA.ResourceGroups (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: KSA.SequenceList (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformancejob]: KSA.SequencePerformanceJob (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-dockingevent]: KSA.DockingEvent (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatedata]: KSA.VehicleUpdateData (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditingspace]: KSA.VehicleEditingSpace (decompiled, build 2026.10.10.5554)
[^ksa-partfailureevent]: KSA.PartFailureEvent (decompiled, build 2026.10.10.5554)
[^ksa-modulestateful]: KSA.ModuleStateful (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
