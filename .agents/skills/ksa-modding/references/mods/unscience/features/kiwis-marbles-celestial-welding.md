---
type: Mod Feature
title: Celestial welding by rewriting a body's orbit before every sim step
description: Kiwi's Marbles pins a planet or moon at a CCI-frame offset from another celestial or a vessel by building a fresh orbit from the target's state vectors in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers, before the orbit workers snapshot it
mod: unscience
submod: kiwis-marbles
category: celestials
techniques: [harmony-prefix, save-sidecar]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers (priority First)
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: create, edit, unweld)"
  - "harmony-postfix KSA.GameSave.Populate (shared NativeSaveHooks: capture)"
  - "harmony-prefix KSA.Universe.DeserializeSave (shared NativeSaveHooks: restore original orbits)"
  - "harmony-postfix KSA.Universe.DeserializeSave (shared NativeSaveHooks: replay welds)"
lifecycle: [frame:dispatch-solvers, frame:draw-ui, event:save, event:load]
game_types:
  - KSA.Universe
  - KSA.Program
  - KSA.Celestial
  - KSA.Orbit
  - KSA.IOrbiter
  - KSA.IParentBody
  - KSA.CelestialUpdateTask
  - KSA.CelestialSystem
  - KSA.UniverseTime
  - KSA.StateVectors
  - KSA.Vehicle
tags: [celestials, orbits, welding, solver, timing, harmony, topological-sort, persistence]
---

Kiwi's Marbles lets a player drag a planet or moon around the solar system: pick a source body, pick
any orbiter as the target (another celestial or a vessel), type an offset, and the source follows the
target at that offset until it is unwelded, at which point it goes back to the orbit it had when the
weld was created. Several welds can be active at once and can chain (Moon follows Earth, Earth follows
a vessel). The panel is the F9 window in the standalone development host and a section of the
unscience toolbox in the shipped supermod.[^km-readme]

Mechanism in one line: once per sim step, from a `Priority.First` Harmony prefix on
`Universe.ExecuteNextVehicleSolvers`, every weld reads the target's freshly applied CCI state vectors,
builds a new `Orbit` with `Orbit.CreateFromStateCci` around the target's parent at target position plus
offset with the target's velocity, and swaps it onto the source body, so that the next
`CelestialUpdateTask` propagates the welded orbit instead of the original one.[^km-engine]

## How it works

- **Weld record** ([kiwis-marbles.lib/CelestialWeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEntry.cs#L7-L24)).
  `CelestialWeldEntry` holds `Source` (a `Celestial`), `Target` (any `IOrbiter`), `Offset` (`double3`,
  metres, applied directly in the CCI frame of the target's parent and not rotated by any body frame)
  and `OriginalOrbit` (the source's `Orbit` reference captured at weld creation).[^km-entry]
- **Pickers** ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L109-L269)).
  Sources come from `CelestialProvider.GetAllCelestials()` (`Universe.CurrentSystem.All` filtered to
  `Celestial`, so stars and barycentres are excluded) and targets from `GetAllOrbiters()` (every
  `IOrbiter`, celestials and vessels).[^celestial-provider] Both combos are filterable.
- **Create** ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L471-L487)).
  `InitiateWeld` rejects a body that is already a weld source, appends the entry with
  `OriginalOrbit = source.Orbit`, and re-sorts the list. The UI only mutates the managed list; no
  celestial is touched from the render loop.
- **Unit scale and live editing** ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L32-L33)).
  The offset is edited as a `float3` proxy in one of m, km, Mm or Gm and converted with a `double`
  factor, so Gm-scale offsets keep metre precision in the stored `double3`. Each active weld has its own
  unit combo and `DragFloat3`; a change writes `weld.Offset` immediately
  ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L344-L359))
  and the next solver prefix applies it. When the target is a celestial, a "Surface Mode" alternative
  converts longitude, latitude and altitude above the sum of both mean radii into the same CCI offset
  ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L366-L425)),
  and +X/+Y/+Z buttons place a new weld at surface-contact distance.
- **Per-step update** ([kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L27-L52)).
  `UpdateWeld` returns `false` (weld removed) if the target has no orbit or no parent. Otherwise it
  reads `GetPositionCci()` and `GetVelocityCci()` from the target, creates the orbit around
  `entry.Target.Parent` at `SimTimeProvider.GetElapsedTime()` with the source's own `OrbitColor`, and
  applies it through `ApplyOrbit`, which also fixes the parent's child lists when the parent changes
  and refreshes the cached frames of the whole subtree (see
  [cross-parent re-parenting](./kiwis-marbles-cross-parent-reparenting.md)).
- **Chains: Kahn's topological sort** ([kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs#L102-L150)).
  An edge x -> y exists when x's source is y's target, so the body is moved before anything that follows
  it reads its position. A cycle leaves the list order unchanged and logs. The sort runs on every
  create, and on load; the per-step loop simply walks the sorted list.
- **Unweld and restore** ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L489-L522)).
  The Unweld button removes the entry and queues it in `_pendingRestores`; the restore
  (`CelestialWeldEngine.RestoreOrbit`, which re-applies `OriginalOrbit` through the same `ApplyOrbit`)
  runs at the start of the next solver prefix. A weld whose `UpdateWeld` returns `false` is removed the
  same way. The original orbit object is reused as is, so the body resumes from the state vectors that
  orbit held when it was captured; the orbit workers then propagate it forward from there.
- **Unload** ([kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L456-L469)).
  `Dispose` restores every weld and pending restore immediately, because after unpatching there is no
  later safe window.
- **Scene saves** ([kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs)).
  Participant `kiwis-marbles`, restore order 40. The record per weld is source id, target id,
  `TargetIsVehicle`, the offset, and the original orbit as parent id plus `StateVectors.StateTime`,
  `PositionCci` and `VelocityCci`, because native KSA saves never serialize celestial orbits. Validation
  rejects more than 10,000 welds, duplicate sources, non-finite numbers and saved weld cycles. Reset
  (before KSA rebuilds the world) restores the old orbits in reverse list order. Replay rebuilds
  `OriginalOrbit` with `Orbit.CreateFromStateCci`, rejects any weld whose target sits below its own
  source (including cycles through vessel parents that a target-only sort misses), sorts, and applies
  each weld once while the solvers are idle; unresolved bodies are warnings, never substitutions.[^km-persistence][^km-lib-readme]

## Lifecycle and timing

Order inside `Program.PrepareFrame` in build 5554:[^ksa-program]

| Line | Call | What it means for a weld |
|---|---|---|
| L2155 | `JobSystems.OrbitSolvers.Wait()` | The previous `CelestialUpdateTask`s have finished |
| L2164 | `Universe.ApplyOrbitSolvers()` | Each body's `UpdateFromTaskResults` calls `Orbit.UpdatePosition` with the propagated state |
| L2167 | `Universe.ApplyVehicleSolvers()` | Vessel results committed; ends with `CelestialSystem.UpdatePerFrameData()` (L1950 in Universe.cs) |
| L2196-L2201 | nearest-orbit hover job queued | Runs on a worker alongside the prefix; it only reads `Orbit` references |
| L2216-L2219 | `GetJobSimStep`, cloth dispatch, `PartTree.FlushDirty*` | |
| L2220 | **`Universe.ExecuteNextVehicleSolvers`** | **Kiwi's Marbles prefix runs here**, after Eternal Flame's prefix |
| L2221 | `Universe.ExecuteNextOrbitSolvers` | One `CelestialUpdateTask` per body is prepared and queued |

Why the prefix and not the UI hook:

1. The orbit job does not copy the orbit when it is queued. `CelestialUpdateTask.DoWorkAndStageResults`
   reads `_readOnlyCelestial.Orbit` when the worker runs and stages `GetStateVectorsAt(simStep.NextTime)`;
   the next frame, `Celestial.UpdateFromTaskResults` writes that result into whatever `Orbit` the body has
   at that moment.[^ksa-celestial-update-task][^ksa-celestial] An orbit swapped from
   `[StarMapBeforeGui]` (after L2221) races the worker, and the welded orbit then receives the state
   vectors propagated from the old one. The authors record this as the original symptom: welds had no
   visible effect.[^km-readme][^scope-celestial-lights]
2. In the prefix every target position is current (both Apply calls have run and the celestial frames
   were refreshed at the end of `ApplyVehicleSolvers`), no orbit or vehicle worker is running, and the
   welded orbit is what the worker propagates next.[^ksa-universe]
3. The epoch matches the state: `Universe.GetElapsedTime()` returns `_lastSimStep.NextTime`, which
   `ApplyVehicleSolvers` has just set to the end of the step whose results were applied, the same time
   the target's state vectors describe.[^ksa-universe][^sim-time-provider]
4. The vessel step that the original method prepares and dispatches right after the prefix starts with
   the welded orbits already in place and the moved subtree's cached frames refreshed.

`ISubmod.Update(dt)` (StarMap `BeforeGui`) is deliberately empty, and every mutation from the UI (create,
unweld) is either a list edit or a queued restore.[^km-submod] Because the prefix is not a UI hook it also
runs with the HUD hidden (F2), and because `ExecuteNextVehicleSolvers` is called every frame, welds and
live offset edits keep applying while the game is paused (the orbit worker then skips propagation,
since `DeltaTime <= 0`).[^ksa-celestial-update-task]

Both hosts install the same patch class: the supermod at
[unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L77)
(removed at L131) right after Eternal Flame's, and the development host from
[kiwis-marbles/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/Patcher.cs#L15-L30).
Save replay runs inside the scene coordinator's `DeserializeSave` postfix, which is also before the next
solver dispatch.

## Key code

| Path | Role |
|---|---|
| [kiwis-marbles.lib/KiwisMarblesPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesPatches.cs#L20-L54) | `Apply`/`Remove` of the `Priority.First` prefix; prefix calls `KiwisMarblesSubmod.Instance.UpdateBeforeVehicleSolvers()` |
| [kiwis-marbles.lib/KiwisMarblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.cs#L52-L73) | Per-step loop: pending restores, then every weld |
| [kiwis-marbles.lib/CelestialWeldEngine.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEngine.cs) | `UpdateWeld`, `RestoreOrbit`, `ApplyOrbit`, `TopologicalSort` |
| [kiwis-marbles.lib/CelestialWeldEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/CelestialWeldEntry.cs) | Weld record |
| [kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs) | Scene-save capture, reset, validate and replay |
| [ksa-abstractions.lib/CelestialProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/CelestialProvider.cs) | Source and target lists |
| [kiwis-marbles/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kiwis-marbles/README.md) | User guide and the authors' timing write-up |
| [scope/celestial-and-lights.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/celestial-and-lights.md) | Integration table and per-build verification notes |

## Snippets

The weld itself: state vectors in, new orbit out.

```csharp
// kiwis-marbles.lib/CelestialWeldEngine.cs L27-L52
    public static bool UpdateWeld(CelestialWeldEntry entry)
    {
        if (entry.Source == null || entry.Target == null)
            return false;

        if (entry.Target.Orbit == null || entry.Target.Parent == null)
            return false;

        double3 tgtPosCci = entry.Target.GetPositionCci();
        double3 tgtVelCci = entry.Target.GetVelocityCci();
        IParentBody parent = entry.Target.Parent;

        double3 newSrcPosCci = tgtPosCci + entry.Offset;
        double3 newSrcVelCci = tgtVelCci;

        Orbit newOrbit = Orbit.CreateFromStateCci(
            parent,
            SimTimeProvider.GetElapsedTime(),
            newSrcPosCci,
            newSrcVelCci,
            entry.Source.OrbitColor
        );

        ApplyOrbit(entry.Source, newOrbit);
        return true;
    }
```

What the prefix runs, deferred restores first:

```csharp
// kiwis-marbles.lib/KiwisMarblesSubmod.cs L52-L73
    public void UpdateBeforeVehicleSolvers()
    {
        if (_pendingRestores.Count > 0)
        {
            foreach (var entry in _pendingRestores)
                TryRestoreOrbit(entry);
            _pendingRestores.Clear();
        }

        if (_welds.Count == 0) return;

        List<CelestialWeldEntry>? toRemove = null;
        foreach (var weld in _welds)
        {
            if (!CelestialWeldEngine.UpdateWeld(weld))
                (toRemove ??= new List<CelestialWeldEntry>()).Add(weld);
        }

        if (toRemove == null) return;
        foreach (var weld in toRemove)
            RemoveWeld(weld);
    }
```

Kahn's sort over "x moves the body y follows":

```csharp
// kiwis-marbles.lib/CelestialWeldEngine.cs L102-L150
    public static List<CelestialWeldEntry> TopologicalSort(List<CelestialWeldEntry> welds)
    {
        var inDegree = new Dictionary<CelestialWeldEntry, int>();
        var adj = new Dictionary<CelestialWeldEntry, List<CelestialWeldEntry>>();
        // ...
        foreach (var x in welds)
        {
            foreach (var y in welds)
            {
                if ((IOrbiter)x.Source == y.Target)
                {
                    adj[x].Add(y);
                    inDegree[y]++;
                }
            }
        }

        var queue = new Queue<CelestialWeldEntry>();
        foreach (var w in welds)
            if (inDegree[w] == 0)
                queue.Enqueue(w);

        var sorted = new List<CelestialWeldEntry>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);
            foreach (var neighbor in adj[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sorted.Count == welds.Count)
            return sorted;
        // ...
        return new List<CelestialWeldEntry>(welds);
    }
```

The game side: the orbit worker reads `Celestial.Orbit` when it runs, not when it is queued.

```csharp
// current/decomp/KSA/CelestialUpdateTask.cs L52-L64
	private void DoWorkAndStageResults()
	{
		if (!(_simStep.DeltaTime <= 0.0))
		{
			UniverseTime nextTime = _simStep.NextTime;
			Orbit orbit = _readOnlyCelestial.Orbit;
			NewStateVectors = orbit.GetStateVectorsAt(nextTime);
			if (orbit.IsMissingPoints())
			{
				_newOrbitPoints = UpdateTaskUtils.GenerateSpacedPoints(orbit);
			}
		}
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139) | Prefix target (single overload) |
| `Universe.ExecuteNextOrbitSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2098-L2112) | Queues the `CelestialUpdateTask` that must see the welded orbit |
| `Universe.ApplyOrbitSolvers` / `ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1905-L1952) | Results applied before the prefix; frame caches refreshed |
| `Universe.GetElapsedTime()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2399-L2402) | Epoch of the new orbit (`_lastSimStep.NextTime`) |
| `CelestialUpdateTask.DoWorkAndStageResults` | [current/decomp/KSA/CelestialUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialUpdateTask.cs#L52-L64) | Worker propagation that reads `Orbit` late |
| `Celestial.UpdateFromTaskResults` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L1529-L1535) | Writes the staged state into the current orbit |
| `Celestial.SetOrbit(Orbit)`, `Celestial.Orbit`, `Celestial.OrbitColor` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L149-L152) | The swap (a bare property assignment) and orbit-line colour |
| `Orbit.CreateFromStateCci(IParentBody, UniverseTime, double3, double3, byte4)` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1611) | Orbit from state vectors, using the parent's `Mu` |
| `Orbit.StateVectors`, `Orbit.Parent` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1185-L1213) | Saved original orbit (time, position, velocity, parent) |
| `IOrbiter.GetPositionCci()` / `GetVelocityCci()` / `Parent` | [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L16-L64) | Target state in its parent's CCI frame |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2140-L2222) | Placement of the prefix in the frame |

## Pitfalls and version notes

- The offset is inertial. It is expressed in the target parent's CCI axes and is never rotated, so a
  "surface mode" longitude and latitude are inertial angles: the welded body does not turn with the
  target's rotation.
- The source takes the target's velocity, so between two prefixes it coasts on a Kepler arc around
  the new parent. Each frame renders the re-welded position (the prefix runs before rendering), but
  vessel physics in that step integrate against the propagated arc.
- KSA defines a celestial's CCI-to-CCE rotation from its orbit (`Celestial.UpdatePerFrameData` uses
  `Orbit.GetParentCce2Orb()`), so replacing the orbit also changes the welded body's axial orientation
  relative to its parent (from the decomp; visual effect not checked in game).[^ksa-celestial][^ksa-orbit]
- The live UI path only rejects duplicate sources and source == target. It does not run the
  ancestor checks the save replay does, so welding a body to something that orbits it (for example a
  moon to a vessel in orbit around that moon) makes the body its own parent; the replay code warns that
  the native subtree refresh is recursive for exactly this reason.[^km-persistence]
- `OriginalOrbit` is captured once per weld. Unweld after a long weld resumes from the original
  orbit's last stored state vectors, not from a freshly propagated position.
- Two prefixes share `ExecuteNextVehicleSolvers` at `Priority.First`; Harmony runs equal priorities in
  registration order, and the supermod registers Eternal Flame first (L76, then L77).[^unscience-patcher]
- The prefix is located with `AccessTools.Method` by name; a second overload would make the lookup
  ambiguous. The authors' 5482 notes record that `PartTree.FlushDirty*` now run just before this call
  and that vessel bubble eviction after a parent change moved into the vehicle worker, so vessels near a
  re-parented body switch bubbles one step later.[^scope-celestial-lights]
- Standalone hot reload is not a save or load; only the supermod's scene saves restore welds.[^km-readme]

## See also

- [Cross-parent welds and the parent/child tree](./kiwis-marbles-cross-parent-reparenting.md)
- [Infinite fuel and battery refill before the vehicle solver snapshot](./eternal-flame-resource-refill.md) (the other prefix on the same method)
- [Physics-safe vessel mutation at the PrepareFrame solver handoff](./lib-physics-frame-hook.md)
- [Part-anchored vehicle welding by per-frame teleport at the physics handoff](./garrys-torch-vehicle-welding.md) (the vessel equivalent)
- [Runtime planetary rings](./bloomin-onion-runtime-planetary-rings.md) (weld a ringed moon to a vessel)
- [Scene save coordinator](./lib-scene-save-coordinator.md) and [scene save sidecar](./unscience-scene-save-sidecar.md)
- [Kiwi's Marbles sub-mod](../submods/kiwis-marbles.md) and [mod overview](../overview.md)
- [SetOrbit plus manual Children surgery and UpdatePerFrameDataTree, with cycle guards on replay](../../../howto/celestials/systems-and-stars/move-or-reparent-a-celestial-body/move-or-reparent-a-celestial-body-1.md) - how-to article demonstrating this feature
- [Priority.First prefix on ExecuteNextVehicleSolvers builds a fresh orbit from the target's state vectors](../../../howto/celestials/systems-and-stars/move-or-reparent-a-celestial-body/move-or-reparent-a-celestial-body-2.md) - how-to article demonstrating this feature
- [Drain a command queue in a Priority.First prefix on Universe.ExecuteNextVehicleSolvers](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-2.md) - how-to article demonstrating this feature

[^km-readme]: kiwis-marbles/README.md
[^km-engine]: kiwis-marbles.lib/CelestialWeldEngine.cs
[^km-entry]: kiwis-marbles.lib/CelestialWeldEntry.cs
[^celestial-provider]: ksa-abstractions.lib/CelestialProvider.cs
[^km-persistence]: kiwis-marbles.lib/KiwisMarblesSubmod.Persistence.cs
[^km-lib-readme]: kiwis-marbles.lib/README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestial-update-task]: KSA.CelestialUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^scope-celestial-lights]: scope/celestial-and-lights.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^sim-time-provider]: ksa-abstractions.lib/SimTimeProvider.cs
[^km-submod]: kiwis-marbles.lib/KiwisMarblesSubmod.cs
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^unscience-patcher]: unscience/Patcher.cs
