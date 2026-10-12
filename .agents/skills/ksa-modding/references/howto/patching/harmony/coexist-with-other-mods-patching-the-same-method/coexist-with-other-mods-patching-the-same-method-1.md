---
type: How-To
title: Priority.First prefixes and Priority.Last finalizers around shared seams
description: Use Harmony priorities so a prefix on Universe.ExecuteNextVehicleSolvers runs before every other mod's prefix and a clearing finalizer on Vehicle.PrepareWorker runs after every other mod's finalizer
category: patching
group: harmony
topic: coexist-with-other-mods-patching-the-same-method
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/gatos/features/solver-phase-command-drain
  - mods/ksastructures/features/terrain-height-roof-override
techniques: [harmony-prefix, harmony-finalizer, harmony-priority]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-finalizer KSA.Vehicle.PrepareWorker
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Universe, KSA.Vehicle, KSA.FlightComputer]
tags: [harmony, priority, prefix, finalizer, coexistence, solver]
---

Several mods patch the same hot game methods. When your patch has to happen *before* anyone else looks at the
state, or *after* everyone else is done, say so with Harmony priorities rather than relying on load order. gatOS
drains its command queue in a `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers` so its writes land
before other mods' prefixes snapshot anything; KSAStructures marks "who is asking" in a `Priority.First` prefix on
`Vehicle.PrepareWorker` and clears it in a `Priority.Last` finalizer; unscience's Eternal Flame uses the same First
prefix on the same solver method.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by gatOS in
[Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md) and KSAStructures in [Terrain height answered as roof, ramp or floor](../../../../mods/ksastructures/features/terrain-height-roof-override.md).

## Background: how the game does it

`Program.PrepareFrame` applies the finished solver results, polls input and then dispatches the next step. The
dispatch is `Universe.ExecuteNextVehicleSolvers`, whose first act is `PrepareVehicleWorkers`: each vehicle's
`PrepareWorker` copies its state, including the whole `FlightComputer`, into the object the worker will use
([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139)[^ksa-universe], [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221)[^ksa-program]). Anything a mod writes to the
main-thread vehicle *after* that snapshot is overwritten when the results are applied; anything written
*before* is carried into the step. A prefix on `ExecuteNextVehicleSolvers` runs in exactly that window.

## Lifecycle and timing

Both seams run on the main thread in `frame:dispatch-solvers`: after `Universe.Apply*Solvers`, `Glfw.PollEvents`,
`InputEvents.ApplyInputEvents`, `Universe.GetJobSimStep` and `ExecuteNextClothSolvers`, and before the vehicle
snapshot. The previous step has been waited for and applied, the next has not started, so the main thread owns
bubble lists and flight-computer state. A command enqueued during frame N is drained in frame N+1's `PrepareFrame`
and is in the step dispatched there. See [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Know what Harmony does with priorities.** In Lib.Harmony 2.4.2 (the version StarMap references) prefixes
   run from `Priority.First` to `Priority.Last`, and finalizers run in the same order. A test with three patches
   registered in scrambled order printed `prefix First`, `prefix default`, `prefix Last`, `original`,
   `finalizer First`, `finalizer default`, `finalizer Last`. A prefix that returns `false` also stops later prefixes that
   themselves return `bool`, while later `void` prefixes still run (a local test against Lib.Harmony 2.4.2; see [Skipping Harmony prefixes](../skip-the-original-method-with-a-prefix/overview.md)),
   so a high-priority prefix that only observes or queues work should return `void` or `true`.

2. **Install the command drain at `Priority.First`.** gatOS gives the patch its own Harmony id, looks the target
   up by name and degrades with a warning if it is gone:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L858-L880
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
partial void InstallSolverHook()
{
    try
    {
        _solverHarmony = new Harmony("gatos.solver");
        var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
        if (original is null)
        {
            ModLog.Log.Warn("Solver hook: Universe.ExecuteNextVehicleSolvers not found; "
                            + "solver-phase commands (refills) are disabled.");
            return;
        }

        var prefix = new HarmonyMethod(typeof(Mod), nameof(SolverDrainPrefix)) { priority = Priority.First };
        _solverHarmony.Patch(original, prefix: prefix);
        ModLog.Log.Info("gatOS solver-phase command hook installed.");
    }
    catch (Exception ex)
    {
        ModLog.Log.Error($"gatOS solver hook failed to install (solver commands disabled): {ex.Message}");
    }
}
```

Source: [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L858-L880)[^gatos-mod-game]

3. **Keep the work in the prefix cheap and non-throwing.** The prefix calls `DrainSolverCommands`, which
   drains only the `Solver` phase queue through the same executor as the frame-phase drain. Which commands belong
   there is a fixed set (flight-computer modes, refills, teleport and impulse), because they depend on the snapshot
   round trip:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L849-L856
internal void DrainSolverCommands()
{
    if (_commandsDead || _commandQueue is not { } queue)
        return;
    EnsureControlObjects();
    using (_drainStats.Measure()) // shares the stat with the Frame-phase drain (both game-thread)
        queue.Drain(CommandPhase.Solver, _catalog!, Config.MaxCommandsPerFrame);
}
```

Source: [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L856)

4. **Mark and clear around a method other mods also patch.** KSAStructures needs a thread-local mark on during
   `Vehicle.PrepareWorker`, whatever other mods do in that method. It registers the prefix at `Priority.First` and the
   finalizer at `Priority.Last`, so the mark brackets every other patch on the method:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L245-L249
MethodInfo Own(string name) => typeof(Office).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
var harmony = new Harmony(HarmonyId);
harmony.Patch(worker, prefix: new HarmonyMethod(Own(nameof(BeforeWorker))) { priority = Priority.First },
              finalizer: new HarmonyMethod(Own(nameof(AfterWorker))) { priority = Priority.Last });
harmony.Patch(height, postfix: new HarmonyMethod(Own(nameof(AfterTerrainHeight))));
```

Source: [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L245-L249)[^ksastructures-officeterrain]

   The mark itself and the reasoning for using a finalizer are in
   [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](../run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md).

5. **Same idea elsewhere in unscience.** Its Eternal Flame feature registers the same First prefix on
   `ExecuteNextVehicleSolvers` so its refill runs before other prefixes on that method:

```csharp
// unscience/Patcher.cs L186-L197
public static void Apply(Harmony harmony)
{
    var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
    var prefixMethod = AccessTools.Method(typeof(EternalFlamePatches), nameof(BeforeVehicleSolvers));

    if (original == null)
        throw new MissingMethodException(typeof(Universe).FullName, nameof(Universe.ExecuteNextVehicleSolvers));
    if (prefixMethod == null)
        throw new MissingMethodException(typeof(EternalFlamePatches).FullName, nameof(BeforeVehicleSolvers));

    harmony.Patch(original, prefix: new HarmonyMethod(prefixMethod) { priority = Priority.First });
}
```

Source: [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L186-L197)[^unscience-patcher]

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L849-L880) | Drain and install |
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L233-L264) | First prefix and Last finalizer on `PrepareWorker` |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L184-L205) | Eternal Flame prefix and symmetrical `Remove` |

## Variations and gotchas

- **Priority is not a lock.** Another mod can also use `Priority.First`. Among equal priorities, a local test
  ran the prefix registered first first; setting `before = new[] { "z.mod" }` on the later `HarmonyMethod`
  (the owner id of the earlier mod) reversed them. `before` and `after` order patches by Harmony id when you
  must run relative to a specific mod.
- **Do not skip other mods' work.** A `First` prefix that returns `false` skips everything after it, including
  other mods' prefixes and the original.
- **Do not use a late seam for writes.** A write from `StarMapBeforeGui` lands after the snapshot and is erased
  at the next apply; gatOS's page documents the navball flashing to prograde and snapping back.
- **Shut down by id.** Remove with `UnpatchAll(<your id>)`, never the id-less overload; StarMap's own id-less
  call at exit has already removed every patch by the time `[StarMapUnload]` runs (see
  [Removing Harmony patches safely at runtime and at exit](../../safety/remove-your-patches-safely-at-runtime-and-at-exit/overview.md)).
- **Targets are shared with other mods.** `Program.DrawProgramMenusHook` and `Universe.ExecuteNextVehicleSolvers`
  carry patches from several mods; the guidance above is about not breaking each other.

## See also

- [Anchors that one mod can consume (and what the next transpiler then sees)](coexist-with-other-mods-patching-the-same-method-2.md), [Idempotent shared installs with a registration API](coexist-with-other-mods-patching-the-same-method-3.md), [One shared tick with per-feature enable flags set from TryApply results](coexist-with-other-mods-patching-the-same-method-4.md), [One worker-side postfix merging every writer](coexist-with-other-mods-patching-the-same-method-5.md)
- [Coexisting with other mods that patch the same method](overview.md)
- [Solver-phase drain for flight computer setpoints](../../../../mods/gatos/features/solver-phase-command-drain.md), [Terrain height answered as roof, ramp or floor](../../../../mods/ksastructures/features/terrain-height-roof-override.md)
- Related task: mutate-vehicles-at-a-physics-safe-point

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^gatos-mod-game]: gatOS/gatOS.GameMod/Game/Mod.Game.cs
[^ksastructures-officeterrain]: KSAStructures/src/KSAStructures/Ksa/OfficeTerrain.cs
[^unscience-patcher]: unscience/Patcher.cs
