---
type: Mod Feature
title: Physics-safe vessel mutation at the PrepareFrame solver handoff
description: A transpiler on Program.PrepareFrame swaps the Universe.GetJobSimStep call for a dispatcher that runs queued vessel edits after solver results are applied and before the next physics step is snapshotted
mod: unscience
category: patching
techniques: [harmony-transpiler, reflection]
hooks:
  - harmony-transpiler KSA.Program.PrepareFrame
  - reflection KSA.Program.PrepareFrame (private, looked up by name)
lifecycle: [frame:prepare]
game_types: [KSA.Program, KSA.Universe, KSA.SimStep, KSA.JobSystems, KSA.NearestOrbitPointJob, KSA.Vehicle]
tags: [physics, timing, harmony, transpiler, solver, teleport, threading]
---

KSA steps vessel physics on worker threads that run between frames. Anything that moves,
teleports, rescales or re-wires a vessel from ordinary mod code (an ImGui button, a StarMap GUI
hook, an `ISubmod.Update`) runs while the next physics step is already in flight, so the worker
later commits its own result over the edit, or reads half-mutated state. `PhysicsFrameHook` is
the shared library seam that gives every unscience feature one main-thread instant where vessel
state is settled and nothing has been snapshotted yet.[^physics-frame-hook]

Mechanism in one line: a Harmony transpiler on the private `Program.PrepareFrame` replaces the
single call to `Universe.GetJobSimStep` with `PhysicsFrameHook.GetStepAndDispatch`, which computes
the same `SimStep` and, before returning it, runs a pending world replacement, the queued
mutations and the `BeforePhysics` callbacks.

## How it works

Public surface of [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs):

| Member | Purpose |
|---|---|
| `Apply(Harmony)` / `Remove(Harmony)` | Install or remove the transpiler; `Apply` is idempotent (`IsApplied`) |
| `Enqueue(Action)` | One-shot main-thread mutation run at the next handoff (Godzilla scaling, Iron Man activation) |
| `BeforePhysics` event `(double dtPlayer, UniverseTime stateTime)` | Per-frame callback at the handoff (Garry's Torch welds, Dent Wizard launches, Godzilla session checks); `stateTime` is `SimStep.PreviousTime` |
| `EnqueueWorldChange(Action)` / `IsReplayingWorldChange` | Defers a whole save load or system switch to the handoff (used by the scene-save hooks) |
| `JoinOrbitReaders()` | Waits once per frame for the nearest-orbit job before a callback disposes flight-plan points |
| `ClearPending()` / `ClearPendingWorldChange()` | Drop work queued against a world that is being replaced |

The transpiler does not trust the IL blindly. It looks up seven `Universe` methods
(`ApplyOrbitSolvers`, `ApplyVehicleSolvers`, `ApplyClothSolvers`, `GetJobSimStep`,
`ExecuteNextClothSolvers`, `ExecuteNextVehicleSolvers`, `ExecuteNextOrbitSolvers`), requires each to
be called exactly once and in that order, and throws otherwise. A throw aborts `Apply`, the host's
`TryApply` logs it, and dependants (Dent Wizard's launch button checks `IsApplied`) disable
themselves instead of falling back to unsafe UI-time teleports. Only then does it rewrite the
operand of the `GetJobSimStep` call, which keeps the caller's stack shape, labels and exception
blocks intact.

`GetStepAndDispatch(dtPlayer)` then runs, in order:

1. Reset the per-frame "orbit readers joined" flag.
2. If a world change is pending (a native load captured by the save hooks), join the orbit job and
   run it with `IsReplayingWorldChange = true` so the save hooks let the real load through.
3. Call the real `Universe.GetJobSimStep(dtPlayer)`; if there is no current system, drop the queue.
4. Drain only the actions present at entry (actions queued by callbacks wait one frame), joining the
   orbit job first if there is any work, each in its own `try/catch`.
5. Invoke every `BeforePhysics` subscriber with `step.PreviousTime`, each isolated in `try/catch`.
6. Return the `SimStep` to `PrepareFrame`, which passes it to the three `ExecuteNext*Solvers` calls.

## Lifecycle and timing

The rewritten call sits at `current/decomp/KSA/Program.cs` L2216 in build 5554, inside
`PrepareFrame` (the first thing `Program.OnFrame` does).[^ksa-program] The surrounding order is:

| Step in `PrepareFrame` | Line | State for a mod at the handoff |
|---|---|---|
| `JobSystems.NearestOrbitAndPerformanceWorker.Wait()`, then `OrbitSolvers`/`VehicleSolver`/`ClothSolvers.Wait()` | L2151-L2161 | Every physics worker from the previous frame has finished |
| `Universe.ApplyOrbitSolvers` / `ApplyVehicleSolvers` / `ApplyClothSolvers` | L2164-L2170 | Worker results are committed to the main-thread vessels; `_lastSimStep` is advanced |
| `Glfw.PollEvents`, `InputEvents.ApplyInputEvents`, `RefreshVehiclesInFrame` | L2179-L2189 | Input for this frame is processed (F2 may have flipped `DrawUI`) |
| `NearestOrbitAndPerformanceWorker.Queue(_nearestOrbitPointJob)` + `ExecuteJobs` | L2196-L2200 | The nearest-orbit job now runs **concurrently** and reads flight plans / orbit points |
| `Universe.ProcessAutoWarp` | L2215 | Warp state for this step decided |
| **`Universe.GetJobSimStep` -> `GetStepAndDispatch`** | L2216 | **Handoff: mutations run here** |
| `ExecuteNextClothSolvers`, `PartTree.FlushDirtyDerived`, `FlushDirtyResourceManagers`, `ExecuteNextVehicleSolvers`, `ExecuteNextOrbitSolvers` | L2217-L2221 | Next step is snapshotted (`Vehicle.PrepareWorker` inside `PrepareVehicleWorkers`) and kicked onto workers |

Consequences for code running in `BeforePhysics` or an enqueued action:

- Vessel kinematics, part trees and module state are the committed result of the last step, and no
  worker holds them, so writes are not lost and not torn.
- Part-tree edits made here are picked up by `PartTree.FlushDirtyDerived` /
  `FlushDirtyResourceManagers`, which run two lines later, before the vehicle snapshot.[^ksa-program]
- `SimStep.PreviousTime` is `_lastSimStep.NextTime` (see `Universe.GetJobSimStep`), the instant the
  committed state represents and the start time of the step about to run, so teleports stamped with
  it line up with the solver's integration start.[^ksa-universe][^ksa-simstep]
- The one thing still racing is `NearestOrbitPointJob`, queued at L2198; it calls
  `CelestialSystem.SetNearestOrbitPoint` and reads cached orbit points that `Vehicle.Teleport`
  disposes.[^ksa-nearest-orbit-job] `JoinOrbitReaders()` calls
  `JobSystems.NearestOrbitAndPerformanceWorker.Wait()` once per frame before such edits; the authors
  identified this race as a candidate cause of the Garry's Torch weld error spam.[^scope-vehicle-physics]
- ImGui is not inside a frame yet (`PrepareImGui` runs after `PrepareFrame`), and the previous frame's
  `ImGui.Render` has completed, which is why the save hooks defer world replacement here instead of
  disposing textures from inside a console command.

Why the caller and not a prefix: the comment in the transpiler notes that a prefix on a small
`Universe` method can miss a call the JIT already inlined into `PrepareFrame`; replacing the call
instruction in the caller cannot be bypassed that way. A prefix on `ExecuteNextVehicleSolvers` (as
Eternal Flame and Kiwi's Marbles use) runs later, after the cloth step was kicked and after the two
`PartTree` flushes, so it only suits edits that do not dirty derived part-tree data (tank and
battery contents, celestial orbits).

The hook fires every frame regardless of HUD visibility (it is not part of the GUI phase), so welds
keep working with F2.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs) | The transpiler, the dispatcher, the queue, events and orbit-reader join |
| [garrys-torch.lib/GarrysTorchPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchPatches.cs) | Subscribes weld updates to `BeforePhysics` |
| [dent-wizard.lib/DentWizardSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.cs#L25-L51) | Fires a pending launch from `BeforePhysics` after `JoinOrbitReaders` |
| [godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs#L35-L60) | Enqueues scale edits requested from the UI |
| [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs#L49-L60) | Enqueues kitten activation |
| [ksa-abstractions.lib/Persistence/NativeSaveHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/NativeSaveHooks.cs) | Defers native loads with `EnqueueWorldChange` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L67-L78) | Applies the hook directly and through the save and weld patch classes |

## Snippets

The transpiler validates the solver call sequence before swapping one call:

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L73-L104
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.ToList();
        // Patch the caller: a prefix on a small solver method can miss a call already inlined
        // into PrepareFrame. Replacing this one call preserves its stack, labels and blocks.
        // Reject missing, duplicate or reordered seams instead of falling back to UI teleports.
        string[] seams =
        {
            nameof(Universe.ApplyOrbitSolvers),
            nameof(Universe.ApplyVehicleSolvers),
            nameof(Universe.ApplyClothSolvers),
            nameof(Universe.GetJobSimStep),
            nameof(Universe.ExecuteNextClothSolvers),
            nameof(Universe.ExecuteNextVehicleSolvers),
            nameof(Universe.ExecuteNextOrbitSolvers)
        };
        int previousIndex = -1;
        int stepIndex = -1;
        foreach (string seam in seams)
        {
            var method = AccessTools.Method(typeof(Universe), seam)
                ?? throw new MissingMethodException(typeof(Universe).FullName, seam);
            int index = codes.FindIndex(code => code.Calls(method));
            if (index <= previousIndex || codes.Count(code => code.Calls(method)) != 1)
                throw new InvalidOperationException($"unscience physics: unexpected PrepareFrame solver sequence at {seam}");
            previousIndex = index;
            if (seam == nameof(Universe.GetJobSimStep)) stepIndex = index;
        }

        codes[stepIndex].operand = AccessTools.Method(typeof(PhysicsFrameHook), nameof(GetStepAndDispatch));
        return codes;
    }
```

The replacement call runs world changes, queued edits and callbacks, then returns the real step:

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L106-L140
    private static SimStep GetStepAndDispatch(double dtPlayer)
    {
        // The previous ImGui frame has finished and worker results have committed. A
        // console load requested after ImGui.Image must never dispose its textures inline.
        // Replay the whole transaction here, before computing any time from the old world.
        _orbitReadersJoined = false;
        Action? worldChange = _pendingWorldChange;
        _pendingWorldChange = null;
        if (worldChange != null)
        {
            JoinOrbitReaders();
            IsReplayingWorldChange = true;
            try { worldChange(); }
            catch (Exception ex) { Console.WriteLine($"unscience saves: deferred world change failed: {ex}"); }
            finally { IsReplayingWorldChange = false; }
        }
        SimStep step = Universe.GetJobSimStep(dtPlayer);
        if (Universe.CurrentSystem == null) { Pending.Clear(); return step; }

        // Drain only the actions present at the handoff; actions queued by callbacks wait a frame.
        int count = Pending.Count;
        if (count > 0) JoinOrbitReaders();
        for (int i = 0; i < count; i++)
        {
            try { Pending.Dequeue()(); }
            catch (Exception ex) { Console.WriteLine($"unscience physics: queued mutation failed: {ex}"); }
        }
        if (BeforePhysics != null)
            foreach (Action<double, UniverseTime> callback in BeforePhysics.GetInvocationList())
            {
                try { callback(dtPlayer, step.PreviousTime); }
                catch (Exception ex) { Console.WriteLine($"unscience physics: callback failed: {ex}"); }
            }
        return step;
    }
```

The game code being rewritten (build 5554):

```csharp
// current/decomp/KSA/Program.cs L2215-L2221
		Universe.ProcessAutoWarp(dtPlayer);
		SimStep jobSimStep = Universe.GetJobSimStep(dtPlayer);
		Universe.ExecuteNextClothSolvers(dtPlayer, jobSimStep);
		PartTree.FlushDirtyDerived();
		PartTree.FlushDirtyResourceManagers();
		Universe.ExecuteNextVehicleSolvers(dtPlayer, jobSimStep);
		Universe.ExecuteNextOrbitSolvers(dtPlayer, jobSimStep);
```

A consumer: Dent Wizard holds a launch request from the UI and fires it at the handoff:

```csharp
// dent-wizard.lib/DentWizardSubmod.cs L42-L51
    private void FirePending(double dt, UniverseTime stateTime)
    {
        var request = _pending;
        _pending = null;
        if (request == null) return;
        // The launch teleports the source, which disposes orbit points the hover job may read.
        PhysicsFrameHook.JoinOrbitReaders();
        try { SetStatus(request.Execute(stateTime), false); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.PrepareFrame(double, double)` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) | Transpiler target, found with `AccessTools.Method(typeof(Program), "PrepareFrame", ...)` |
| `Universe.GetJobSimStep(double)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615) | The swapped call; builds `SimStep { PreviousTime = _lastSimStep.NextTime, ... }` |
| `Universe.Apply*Solvers`, `Universe.ExecuteNext*Solvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1905) | Order anchors the transpiler validates |
| `SimStep.PreviousTime` | [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | Timestamp handed to `BeforePhysics` subscribers |
| `JobSystems.NearestOrbitAndPerformanceWorker` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs) | `Wait()` in `JoinOrbitReaders` |
| `NearestOrbitPointJob` | [current/decomp/KSA/NearestOrbitPointJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NearestOrbitPointJob.cs) | The concurrent reader the join protects |
| `Universe.CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | No system: queued work is discarded |

## Pitfalls and version notes

- The validation is strict on purpose: a game update that adds a second `GetJobSimStep` call or
  reorders the solver calls makes `Apply` throw. Every feature that needs the handoff must then
  report itself unavailable rather than mutate from the UI. All seven anchors are present once and
  in order in build 5554 (`Program.cs` L2164-L2221).
- `JoinOrbitReaders` was added for KSA 5482, where the nearest-orbit job is queued inside
  `PrepareFrame` before the handoff; in 5554 the job is queued at L2198, so the join is still needed.
  It only covers the job queued at that point: the same worker can later run editor
  sequence-performance jobs (queued after `OnDrawUiConsole` in `OnFrame`), which this does not wait for.
- There is no reference counting: `Remove` unpatches and clears the queue for every user. In the
  supermod all removals happen at unload, but a standalone host that removes one feature's patches
  (for example `GarrysTorchPatches.Remove`) also removes the handoff for the others.
- Actions enqueued by a `BeforePhysics` callback or another action run one frame later, by design.
- Work done here costs main-thread frame time before the solvers start; keep it short.
- Exceptions are caught per action and per subscriber and logged to the console, so one failing
  feature cannot stop the frame.

## See also

- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md) for the other half of the per-frame contract
- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md)
- [Dent Wizard vessel launcher](./dent-wizard-vessel-launcher.md)
- [Godzilla vessel scaling](./godzilla-vessel-scaling.md)
- [Native save hooks](./lib-native-save-hooks.md) and [scene save coordinator](./lib-scene-save-coordinator.md) (deferred world change)
- [Eternal Flame resource refill](./eternal-flame-resource-refill.md) (the alternative `ExecuteNextVehicleSolvers` prefix)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Mod overview](../overview.md)
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) - how-to article demonstrating this feature
- [Idempotent shared installs with a registration API](../../../howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-3.md) - how-to article demonstrating this feature
- [Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler](../../../howto/physics/solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-3.md) - how-to article demonstrating this feature

[^physics-frame-hook]: ksa-abstractions.lib/PhysicsFrameHook.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-simstep]: KSA.SimStep (decompiled, build 2026.10.10.5554)
[^ksa-nearest-orbit-job]: KSA.NearestOrbitPointJob (decompiled, build 2026.10.10.5554)
[^scope-vehicle-physics]: scope/vehicle-physics.md (authors' timing analysis)
