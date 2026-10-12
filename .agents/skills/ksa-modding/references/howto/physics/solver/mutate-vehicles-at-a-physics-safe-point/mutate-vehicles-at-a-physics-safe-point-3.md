---
type: How-To
title: Run a callback at the PrepareFrame handoff with a GetJobSimStep transpiler
description: Rewrite the single Universe.GetJobSimStep call in Program.PrepareFrame so one-shot edits and a per-frame BeforePhysics event run after the apply calls and before cloth, part-tree flushes and the vehicle snapshot
category: physics
group: solver
topic: mutate-vehicles-at-a-physics-safe-point
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/lib-physics-frame-hook
  - mods/unscience/features/garrys-torch-vehicle-welding
  - mods/unscience/features/dent-wizard-vessel-launcher
  - mods/unscience/features/godzilla-vessel-scaling
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.Program.PrepareFrame]
lifecycle: [frame:prepare, frame:draw-ui]
game_types: [KSA.Program, KSA.Universe, KSA.SimStep, KSA.JobSystems, KSA.Vehicle, KSA.PartTree]
tags: [physics, harmony, transpiler, prepareframe, handoff, teleport, scaling, call-site]
---

Outcome: a shared hook with `Enqueue(Action)` for one-shot main-thread edits and a `BeforePhysics` event for
per-frame work, both run at the instant `Program.PrepareFrame` computes the next `SimStep`. Edits made here may
dirty part-tree data, teleport vessels or rescale parts, and still be flushed and snapshotted by the very
next lines of the game. Prerequisites: Harmony (StarMap only to load the mod). unscience's
`PhysicsFrameHook` is the demonstrated implementation; Garry's Torch, Dent Wizard and Godzilla consume it.

## Background: how the game does it

`Program.PrepareFrame` calls the solver methods in a fixed order:

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

`GetJobSimStep` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) is a pure computation of the next `SimStep` (`PreviousTime`
is the last applied step's `NextTime`) [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs), called exactly once in that method. It is the last
call before the game starts dispatching, so code spliced into it runs with the vehicle, orbit and cloth workers
joined and every result applied (the nearest-orbit job, queued a few lines earlier, is the one job still running) (see the [per-frame sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).[^ksa-program]

## Lifecycle and timing

| Moment | What a callback sees |
|---|---|
| After `Wait`, `Apply*Solvers`, `PollEvents`, `ApplyInputEvents` | Vessel kinematics, part trees and module state are the committed result of the last step; no vehicle, orbit or cloth worker is running |
| Before `ExecuteNextClothSolvers` | Part-tree edits are picked up by the `PartTree.FlushDirtyDerived` and `FlushDirtyResourceManagers` calls two lines later |
| `NearestOrbitPointJob` | Queued at L2198, so still running; `Vehicle.Teleport` disposes cached orbit points it reads |
| `stateTime` passed to callbacks | `SimStep.PreviousTime`, equal to `Universe.GetElapsedTime()`: the instant the committed state represents and the start of the step about to run |
| ImGui | Not in a frame yet; `PrepareImGui` runs after `PrepareFrame` |
| HUD hidden (F2) | Unaffected; this is not in the GUI phase |

Compared with the [`ExecuteNextVehicleSolvers` prefix](mutate-vehicles-at-a-physics-safe-point-2.md), this
window is earlier: before cloth dispatch and before the two `PartTree` flushes. A prefix on
`ExecuteNextVehicleSolvers` therefore only suits edits that do not dirty derived part-tree data (tank
contents, orbits). Compared with the [`PrepareWorker` prefix](mutate-vehicles-at-a-physics-safe-point-1.md),
this is once per frame for all vessels, so it suits teleports, rescales and welds rather than per-step
velocity writes.

## Steps

1. Patch the caller, not the callee. A prefix on a small `Universe` method can miss a call the JIT already
   inlined into `PrepareFrame`; replacing the call instruction in the caller cannot be bypassed. Look up the
   private method by name and parameter types:

```csharp
// ksa-abstractions.lib/PhysicsFrameHook.cs L50-L65
    public static void Apply(Harmony harmony)
    {
        if (IsApplied) return;
        harmony.Patch(PrepareFrameMethod(), transpiler: new HarmonyMethod(TranspilerMethod()));
        IsApplied = true;
        Console.WriteLine("unscience physics: PrepareFrame handoff hook applied");
    }

    public static void Remove(Harmony harmony)
    {
        harmony.Unpatch(PrepareFrameMethod(), TranspilerMethod());
        Pending.Clear();
        ClearPendingWorldChange();
        IsApplied = false;
    }
```

2. Validate the IL before changing it. Require each of the seven solver calls to appear exactly once and in
   order; throw otherwise. A throw aborts `Apply`, the host logs it, and consumers check `IsApplied` and
   disable their features instead of falling back to unsafe UI-time edits. Then rewrite only the operand of
   the `GetJobSimStep` call, which keeps the caller's stack shape, labels and exception blocks:

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

3. The replacement method must return the same `SimStep`. It computes the real step, drains the one-shot
   queue (only what was queued at entry, so actions queued by callbacks wait one frame), joins the orbit job
   first if there is work, then fires `BeforePhysics` with `step.PreviousTime`. Every action and subscriber
   is isolated in `try/catch`, because an exception here would abort `PrepareFrame`:

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

4. Use `Enqueue` from UI code: the button only schedules, the handoff performs. Godzilla's rescale:

```csharp
// godzilla.lib/GodzillaSubmod.cs L53-L60
    public void RequestApply(Vehicle vehicle, bool smart, float3 factor)
    {
        if (!WeldScale.IsValid(factor)) { _status = "Each scale must be positive and finite."; return; }
        if (smart) factor = new float3(factor.X);
        _status = $"Applying to {vehicle.Id}…";
        bool physics = ScalePhysics, colliders = ScaleColliders;
        PhysicsFrameHook.Enqueue(() => ApplyNow(vehicle, smart, factor, physics, colliders));
    }
```

5. Use `BeforePhysics` for work that must happen every frame (welds). Garry's Torch subscribes in its patch
   class and stamps the new orbit with the `stateTime` the hook hands it, joining the orbit job first because
   `Vehicle.Teleport` disposes orbit points the hover job reads:

```csharp
// garrys-torch.lib/WeldEngine.cs L112-L123
        Orbit newOrbit = Orbit.CreateFromStateCci(
            entry.Source.Parent,
            stateTime,
            newSrcPosCci,
            newSrcVelCci,
            entry.Source.Orbit.OrbitLineColor
        );

        PhysicsFrameHook.JoinOrbitReaders(); // Teleport disposes orbit points the hover job may read.
        entry.Source.Teleport(newOrbit, newSrcBody2Cce, newBodyRates);
        entry.Source.UpdatePerFrameData();
        return true;
```

6. Gate features on `PhysicsFrameHook.IsApplied`. Dent Wizard disables its Launch button and shows "the
   physics handoff hook did not load" when the transpiler was rejected. Check the log for the line
   `unscience physics: PrepareFrame handoff hook applied` at startup, or the exception text naming the seam
   that no longer matches.

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/PhysicsFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PhysicsFrameHook.cs) | The transpiler, queue, event, orbit-reader join |
| [garrys-torch.lib/GarrysTorchPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchPatches.cs#L8-L30) | Applies the hook and subscribes the weld update |
| [dent-wizard.lib/DentWizardSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/dent-wizard.lib/DentWizardSubmod.cs#L25-L51) | Fires a pending launch from `BeforePhysics` |
| [godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs#L34-L152) | Enqueues scale edits requested from the UI |

## Variations and gotchas

- The validation is strict on purpose: a game update that adds a second `GetJobSimStep` call or reorders the
  solver calls makes `Apply` throw and the hook turns itself off. Keep a visible "unavailable" state.
- `JoinOrbitReaders()` covers only the nearest-orbit job queued before the handoff; the same worker can later
  run editor performance jobs queued after `OnDrawUiConsole`, which this does not wait for.
- No reference counting: `Remove` unpatches for every user, so a host that removes one feature's patches
  also removes the handoff for the rest.
- Work done here costs main-thread frame time before the solvers start; keep it short.
- Actions queued by a callback run one frame later by design; the shape of the queue prevents infinite loops.
- Godzilla restores a half-applied edit in the same handoff when a mutation throws, because the next worker
  would otherwise snapshot a half-edited vessel; do the same for any multi-step edit.
- Never enqueue closures that capture a `Vehicle` without re-checking `IsDisposed`; a save load replaces
  every vessel before the handoff drains.
- A save load or system switch is deferred to the same seam with `EnqueueWorldChange`, so textures used by
  the previous ImGui frame are never disposed inline.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: PrepareWorker prefix](mutate-vehicles-at-a-physics-safe-point-1.md), [approach 2: ExecuteNextVehicleSolvers prefix](mutate-vehicles-at-a-physics-safe-point-2.md)
- Mod features: [PhysicsFrameHook library](../../../../mods/unscience/features/lib-physics-frame-hook.md), [Garry's Torch vehicle welding](../../../../mods/unscience/features/garrys-torch-vehicle-welding.md), [Dent Wizard vessel launcher](../../../../mods/unscience/features/dent-wizard-vessel-launcher.md), [Godzilla vessel scaling](../../../../mods/unscience/features/godzilla-vessel-scaling.md)
- [Weld or anchor one vessel to another](../../kinematics/weld-one-vessel-to-another/overview.md), [teleport a vessel by building a new orbit](../../kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md)
- [Harmony patches on Program and Universe](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) for the call-site rewriting technique in general

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
