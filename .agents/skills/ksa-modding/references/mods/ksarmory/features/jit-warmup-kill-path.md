---
type: Mod Feature
title: JIT-compile the mod and the engine's destroy path on a background thread at load
description: At StarMapAllModsLoaded a Task.Run walks every method of the mod assembly and six engine types and calls RuntimeHelpers.PrepareMethod, so the first kill of a session no longer pays tens of milliseconds of JIT in one frame
mod: ksarmory
category: getting-started
techniques: [starmap-lifecycle, background-thread, jit-prewarm, reflection]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types:
  - KSA.Universe
  - KSA.PartFailure
  - KSA.ExplosionSystem
  - KSA.ExplosionVolumeSystem
  - KSA.ExplosionFlashSystem
  - KSA.Vehicle
tags: [performance, jit, startup, background-thread, frame-budget]
---

The first time a KSArmory warhead killed something in a session, that frame took 44-45 ms against
8-14 ms for every later kill: the mod's detonation code and KSA's own destroy path were being
JIT-compiled on first call.[^jit-warmup][^blocked-doc] `JitWarmup` compiles both ahead of time, off
the main thread, right after the mod finishes loading. It is a dozen lines any mod with an
expensive, rarely-taken path (a first explosion, a first flown lay, a first save) can copy.

## How it works

1. `JitWarmup.Start()` queues one `Task.Run` and returns immediately.[^jit-warmup]
2. The task walks `typeof(JitWarmup).Assembly.GetTypes()` (the whole mod) and a fixed list of
   engine types: `Universe`, `PartFailure`, `ExplosionSystem`, `ExplosionVolumeSystem`,
   `ExplosionFlashSystem`, `Vehicle`, the ones `Universe.DestroyVehicleFromEvent` runs through
   (spawn the destruction explosion, shed up to twelve debris vehicles).[^jit-warmup][^ksa-universe][^ksa-partfailure]
3. For each non-generic type, every declared method and constructor (instance, static, public and
   non-public) that has a body and no open generic parameters is passed to
   `RuntimeHelpers.PrepareMethod(method.MethodHandle)`; failures are swallowed, since such a method
   is simply compiled on first call as before.[^jit-warmup]
4. One log line records how many methods were compiled and how long it took.

Measured effect at 2026.9.22.5482 (the author's figures): the mod's detonation 12.8 ms to 2.6 ms and
the engine's destroy 28 ms to 23 ms; the rest of the destroy is first-time debris construction that
compilation does not reach (compiling all of `KSA.dll` left it at 21.9 ms). The kill frame went
from 44-45 ms to 33-34 ms.[^blocked-doc] The first flown gun lay pays JIT too (186 ms cold against
41 ms warm in a headless measurement), which the whole-assembly walk also covers.[^lay-cost-doc]

## Lifecycle and timing

- **Started at `startup:all-mods-loaded`**: `OnFullyLoaded` calls `JitWarmup.Start()` right after
  registering weapon packs (KSArmoryMod.cs L160), before any scene exists, so the compilation
  overlaps the rest of KSA's loading and the main menu.[^entry]
- **Runs on `thread:background`** (a thread-pool task). `PrepareMethod` only compiles code; it
  executes nothing and touches no game state, so there is nothing to synchronise with the main
  thread, and if the task is still running when the first shot fires, the remaining methods are
  simply compiled on demand as usual.[^jit-warmup]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/JitWarmup.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/JitWarmup.cs) | The whole feature |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L149-L160) | Started from `[StarMapAllModsLoaded]` |
| [docs/BLOCKED-ON-KSA.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/BLOCKED-ON-KSA.md#L107-L124) | Measurements and the check for when to drop the engine list |

## Snippets

```csharp
// src/KSArmory/Ksa/JitWarmup.cs L26-L64
    public static void Start()
    {
        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int compiled = 0;

            foreach (Type type in typeof(JitWarmup).Assembly.GetTypes()) compiled += Prepare(type);
            foreach (Type type in EngineTypes) compiled += Prepare(type);

            Log.Info($"compiled {compiled} method(s) ahead of the first shot in {clock.Elapsed.TotalMilliseconds:F0} ms");
        });
    }

    private static int Prepare(Type type)
    {
        if (type.ContainsGenericParameters) return 0;

        int compiled = 0;
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;

            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                compiled++;
            }
            catch
            {
                // A method the runtime will not compile ahead is compiled on first call, as before.
            }
        }

        return compiled;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Universe.DestroyVehicleFromEvent` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971)) | The path being warmed; its types are compiled, not called |
| `PartFailure.ShedDebris` ([current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L88)) | The debris construction that compilation cannot speed up |

## Pitfalls and version notes

- **Compilation is not initialisation**: first-time allocations and caches inside the engine still
  cost on first use, as the remaining ~22 ms of the first destroy shows.[^blocked-doc]
- **Generic methods are skipped** (`ContainsGenericParameters`); their instantiations still JIT on
  first use.[^jit-warmup]
- **Keep the engine list short and documented.** The author records a scenario-based check for
  when KSA's first destroy runs warm and the engine half can be deleted.[^blocked-doc]
- Warming the whole mod assembly includes code that may never run in a session; the cost is paid
  on a pool thread during loading, not in a frame.[^jit-warmup]

## See also

- [Safe vehicle destruction and part failure](./safe-vehicle-destruction-and-part-failure.md)
- [KSA explosions from a point](./ksa-explosions-from-a-point.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Pre-compile hot code at load with a Task.Run PrepareMethod sweep](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-6.md) - how-to article demonstrating this feature

[^jit-warmup]: src/KSArmory/Ksa/JitWarmup.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^blocked-doc]: docs/BLOCKED-ON-KSA.md
[^lay-cost-doc]: docs/LAY-COST.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-partfailure]: KSA.PartFailure (decompiled, build 2026.10.10.5554)
