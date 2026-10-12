---
type: How-To
title: Pre-compile hot code at load with a Task.Run PrepareMethod sweep
description: Compile the mod's own methods and the engine types the first use will touch on a background thread so the first kill, shot or open does not pay the JIT in one frame
category: testing-and-tooling
group: in-game-testing
topic: measure-and-budget-mod-performance
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/jit-warmup-kill-path]
techniques: [background-thread, reflection]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: [KSA.Universe, KSA.Vehicle, KSA.PartFailure]
tags: [performance, jit, warm-up, prepare-method, background-thread, first-use-hitch]
---

Outcome: a one-time background sweep that makes the first use of a code path as fast as the second. KSArmory measured the first kill of a session at 45 ms in one frame against 8-14 ms for every kill after; the sweep took the mod's detonation from 12.8 ms to 2.6 ms and the engine's destroy from 28 to 23 ms
(the remainder is the engine building debris vehicles for the first time, which no compiling reaches: warming all of KSA.dll left it at 21.9) ([src/KSArmory/Ksa/JitWarmup.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/JitWarmup.cs#L7-L14)). Demonstrated by KSArmory ([JIT warm-up of the kill path](../../../../mods/ksarmory/features/jit-warmup-kill-path.md)). Prerequisites: StarMap.

## Background: how the game does it

The cost is .NET's, not the game's: a method is JIT-compiled on first call, and the first call of a path that spans the mod and several engine types (here `Universe`, `PartFailure`, `ExplosionSystem`, `ExplosionVolumeSystem`, `ExplosionFlashSystem` and `Vehicle` on the engine's destroy path) all compile inside whichever frame first uses it.
`RuntimeHelpers.PrepareMethod(handle)` compiles a method without running it, and is safe off the main thread because it touches no game state.

## Lifecycle and timing

| What | Where |
|---|---|
| `JitWarmup.Start()` | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), in the mod's `OnFullyLoaded`, after registrations and just before the catalogue is frozen ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L158-L164)) |
| The sweep | A `Task.Run` (`thread:background`); it reads types and method handles and compiles, nothing else |
| Join point | None needed: the main thread never waits for it; a method it has not reached yet compiles on first call as before |

No game state is touched off thread. See the [startup order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Choose a StarMap startup hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md).

## Steps

1. **List the engine types on the path you are warming** (the mod's own assembly is swept wholesale). Comment when to delete the list, because the game may one day run its first destroy warm:

   ```csharp
   // src/KSArmory/Ksa/JitWarmup.cs L15-L24
   internal static class JitWarmup
   {
       // The engine's side of a kill: KsaWorld.Destroy is Universe.DestroyVehicleFromEvent, which spawns
       // the destruction explosion and sheds up to twelve pieces as vehicles of their own. Delete this list
       // the day KSA's first destroy runs warm; docs/BLOCKED-ON-KSA.md has the check.
       private static readonly Type[] EngineTypes =
       [
           typeof(Universe), typeof(PartFailure), typeof(ExplosionSystem), typeof(ExplosionVolumeSystem),
           typeof(ExplosionFlashSystem), typeof(Vehicle),
       ];
   ```

2. **Sweep on a background task and log what it did**, so the effect is in the log:

   ```csharp
   // src/KSArmory/Ksa/JitWarmup.cs L26-L38
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
   ```

3. **Prepare every concrete method and constructor of a type.** Skip generic definitions, abstract methods and methods without bodies, and tolerate a method the runtime will not compile ahead:

   ```csharp
   // src/KSArmory/Ksa/JitWarmup.cs L40-L64
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

4. **Start it once, at load:**

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L158-L164
           InstalledPacks.RegisterAll();

           JitWarmup.Start();

           Catalogue.Freeze();
           ReportRegistrations();

   ```

5. **Verify.** The log prints `compiled N method(s) ahead of the first shot in M ms`. Measure the first use against the second with the [budget spans](measure-and-budget-mod-performance-3.md) or a [PerfTracker scope](measure-and-budget-mod-performance-1.md); expect the first-use spike to shrink to the engine's own first-time work.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/JitWarmup.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/JitWarmup.cs) | The sweep |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | The call site |

## Variations and gotchas

- **Warm what you will actually use.** Compiling all of KSA.dll helped nothing beyond the listed types; unrelated compilation just uses CPU and memory at load.
- **Generics are skipped.** Open generic methods cannot be prepared without instantiation; closed ones compile on first call.
- **It cannot warm non-JIT first-time costs** such as the engine building its first debris vehicles.
- **Delete the list when the engine stops needing it.** The mod keeps a check in its `docs/BLOCKED-ON-KSA.md`.
- **Keep the sweep side-effect free.** `PrepareMethod` runs no code, but type initialisers of swept types can run when types are loaded; avoid sweeping types whose static constructors touch game state.

## See also

- [Overview](overview.md), [article 3](measure-and-budget-mod-performance-3.md)
- [Choose a StarMap startup hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- [KSArmory JIT warm-up of the kill path](../../../../mods/ksarmory/features/jit-warmup-kill-path.md)
