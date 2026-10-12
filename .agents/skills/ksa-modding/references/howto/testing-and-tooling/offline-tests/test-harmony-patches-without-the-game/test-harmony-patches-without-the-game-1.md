---
type: How-To
title: Apply the real patches to stand-in KSA types in a console test
description: Link the production patch files into a framework-free console project, declare minimal managed stand-ins that carry the game's namespace and member names, and let Harmony patch them exactly as it patches the real game
category: testing-and-tooling
group: offline-tests
topic: test-harmony-patches-without-the-game
approach: 1
difficulty: advanced
prerequisites: [harmony, dotnet-sdk]
demonstrated_by: [mods/unscience/features/unscience-managed-test-harnesses]
techniques: [harmony-prefix, harmony-transpiler, headless-tests, stand-in-types]
hooks: [harmony-transpiler KSA.Program.PrepareFrame]
lifecycle: [frame:prepare, event:save, event:load]
game_types: [KSA.Program, KSA.Universe, KSA.SimStep, KSA.Vehicle, KSA.Part, KSA.UncompressedSave]
tags: [testing, harmony, stand-ins, transpiler, console, patches, no-game]
---

Outcome: a test project that runs on a laptop (including macOS ARM, where the shipped Windows x64 game cannot run) and exercises the shipped patch methods, their `AccessTools` lookups, their prefix/postfix/finalizer contract and, for a transpiler, the real rewrite of a stand-in copy of the game method.
Demonstrated by unscience, which has 18 such console projects ([the-tick.tests/the-tick.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/the-tick.tests.csproj#L1-L19)). Prerequisites: Harmony (see [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md)).

## Background: how the game does it

A unit-test runner cannot start the KSA executable: it needs its native renderer, physics and content. Harmony only needs a method to patch, so a managed class with the same namespace, type name and member names as the game's is enough for a patch that looks its target up by name. What the stand-in must reproduce is the part the patch depends on: the order of calls in the method a transpiler anchors on, or the signature a prefix binds.
For `Program.PrepareFrame` that order is: apply orbit, vehicle and cloth solvers, get the job step, then execute the next cloth, vehicle and orbit solvers ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2160-L2170), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221)).

## Lifecycle and timing

Nothing runs inside KSA. The harnesses reproduce three game moments in miniature: the `Program.PrepareFrame` solver handoff (`frame:prepare`, an event list recorded by the fixture), a native save whose `Write()` can return false or throw (`event:save`), and a native load that fails reading or reconstructs (`event:load`). See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). What a fixture cannot show is the real frame: the real `PrepareFrame` returns `OnFrameResult` and contains more calls (dirty-flag flushes, input polling) than the stand-in, and `SimStep` is a `readonly struct` in the game ([current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs)).

## Steps

1. **Create an `Exe` project with no test framework** that compiles production files by link rather than referencing the feature library, so it takes a minimal slice that drags in no ImGui, Vulkan or `KSA.dll`. The repo-wide `Directory.Build.props` fails a build early when `KSA.dll` is missing unless a project sets `RequiresKSAAssemblies=false`:

   ```xml
   <!-- the-tick.tests/the-tick.tests.csproj L1-L19 -->
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <OutputType>Exe</OutputType>
       <RequiresKSAAssemblies>false</RequiresKSAAssemblies>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="Lib.Harmony" Version="2.4.2" />
       <Compile Include="../the-tick.lib/TickProtection.cs" Link="TickProtection.cs" />
       <Compile Include="../the-tick.lib/TheTickPatches.cs" Link="TheTickPatches.cs" />
       <Compile Include="../the-tick.lib/TheTickSubmod.Saves.cs" Link="TheTickSubmod.Saves.cs" />
       <Compile Include="../ksa-abstractions.lib/VehicleProvider.cs" Link="VehicleProvider.cs" />
       <Compile Include="../ksa-abstractions.lib/Persistence/ISaveParticipant.cs" Link="ISaveParticipant.cs" />
       <Compile Include="../ksa-abstractions.lib/Persistence/SaveJson.cs" Link="SaveJson.cs" />
       <Compile Include="../ksa-abstractions.lib/Persistence/FiniteNumberConverters.cs" Link="FiniteNumberConverters.cs" />
       <Compile Include="../ksa-abstractions.lib/Persistence/SaveDocument.cs" Link="SaveDocument.cs" />
       <Compile Include="../ksa-abstractions.lib/Persistence/SaveStorage.cs" Link="SaveStorage.cs" />
       <Compile Include="../ksa-abstractions.lib/Persistence/SceneSaveCoordinator.cs" Link="SceneSaveCoordinator.cs" />
     </ItemGroup>
   </Project>
   ```

2. **Declare stand-in game types in a `namespace KSA`** with the game's member names and shapes. Keep bodies to the decision under test, and mark every method that will be patched `[MethodImpl(MethodImplOptions.NoInlining)]` so the JIT cannot inline it away from Harmony:

   ```csharp
   // the-tick.tests/GameFixture.cs L7-L22
   // Native-free stand-ins for the KSA surfaces the production patches touch. Member names and
   // shapes mirror KSA 5482 so the linked production code and Harmony lookups bind unchanged.

   public sealed class Vehicle
   {
       public Vehicle(string id, int partCount = 1)
       {
           Id = id;
           Parts = new PartTree(this, partCount);
       }

       public string Id { get; }
       public bool IsDisposed { get; set; }
       public bool IsDebris { get; set; }
       public PartTree Parts { get; }
   }
   ```

3. **For a transpiler, copy the game method's call sequence into the stand-in and record every call.** The fixture below is the same sequence of solver calls as the game's `PrepareFrame`, with a `Universe` that appends to an event list:

   ```csharp
   // garrys-torch.tests/GameLoopFixture.cs L12-L29
       public sealed class Program
       {
           [MethodImpl(MethodImplOptions.NoInlining)]
           public SimStep RunFrame(double dt) => PrepareFrame(0, dt);

           [MethodImpl(MethodImplOptions.NoInlining)]
           private SimStep PrepareFrame(double currentPlayerTime, double dtPlayer)
           {
               Universe.ApplyOrbitSolvers();
               Universe.ApplyVehicleSolvers();
               Universe.ApplyClothSolvers();
               SimStep step = Universe.GetJobSimStep(dtPlayer);
               Universe.ExecuteNextClothSolvers(dtPlayer, step);
               Universe.ExecuteNextVehicleSolvers(dtPlayer, step);
               Universe.ExecuteNextOrbitSolvers(dtPlayer, step);
               return step;
           }
       }
   ```

   ```csharp
   // garrys-torch.tests/GameLoopFixture.cs L63-L84
           public static void ApplyOrbitSolvers() => Events.Add("apply orbit");
           public static void ApplyVehicleSolvers()
           {
               Events.Add("apply vehicle");
               if (InBubble) TimeCurrent = PendingTimeCurrent;
               Time = PendingTime;
           }
           public static void ApplyClothSolvers() => Events.Add("apply cloth");
           public static SimStep GetJobSimStep(double dtPlayer)
           {
               Events.Add("get step");
               return new(new(Time), new(Time + dtPlayer * Speed), dtPlayer * Speed);
           }
           public static void ExecuteNextClothSolvers(double dtPlayer, SimStep step) => Events.Add("queue cloth");
           public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep step)
           {
               Events.Add("queue vehicle");
               InBubble = true;
               PendingTimeCurrent = Math.Min(1, TimeCurrent + step.DeltaTime);
               PendingTime = step.NextTime.Seconds;
           }
           public static void ExecuteNextOrbitSolvers(double dtPlayer, SimStep step) => Events.Add("queue orbit");
   ```

4. **Reproduce the bug first, then apply the real patch.** The test runs frames without the patch to prove the fixture shows the failure, then calls the production `Apply` on a Harmony instance and asserts the event order after each frame: the callback must land after every apply call and before the next step is queued:

   ```csharp
   // garrys-torch.tests/Program.cs L20-L49
           var game = new KSA.Program();

           // Reproduce the bug: removing the source after workers stage results prevents commit.
           Universe.Reset();
           for (int i = 0; i < 4; i++)
           {
               game.RunFrame(0.25);
               Universe.InBubble = false;
           }
           Require(Universe.TimeCurrent == 0.25, "old UI teleport must reproduce stalled actuation");

           var harmony = new Harmony("garrys-torch.tests");
           GarrysTorchPatches.Apply(harmony);
           try
           {
               Universe.Reset();
               var submod = new GarrysTorchSubmod();
               GarrysTorchSubmod.Instance = submod;
               for (int i = 0; i < 4; i++)
               {
                   Universe.Events.Clear();
                   SimStep step = game.RunFrame(0.25);
                   Require(Universe.TimeCurrent == (i + 1) * 0.25, "actuator must accumulate committed progress");
                   Require(submod.Updates == i + 1, "exactly one weld update per frame");
                   Require(submod.StateTime == step.PreviousTime, "teleport must use committed state time");
                   Require(step.NextTime.Seconds == step.PreviousTime.Seconds + 0.25, "preserve game step");
                   Require(string.Join(",", Universe.Events) ==
                       "apply orbit,apply vehicle,apply cloth,get step,weld,queue cloth,queue vehicle,queue orbit",
                       "weld after all results, before any physics snapshot");
               }
   ```

5. **Use a `Require` helper that throws on the first failed check** and print one PASS line at the end; the process exit code is the verdict:

   ```csharp
   // garrys-torch.tests/Program.cs L174-L177
       private static void Require(bool condition, string message)
       {
           if (!condition) throw new Exception(message);
       }
   ```

6. **Reference the game's own numerics where layout matters.** Tests of collider or scale maths reference `BepuPhysics`, `BepuUtilities` and `Brutal.Core.Numerics` through `KSAFolder` HintPaths (types only, nothing native initialised), so struct layouts and maths are the shipped ones.

7. **Check rejection paths too.** The same fixture asserts that missing, duplicated or reordered seams make the transpiler refuse to patch, and that timestamps use `SimStep.PreviousTime`.

8. **Re-read the decompiled game on every upgrade.** A test that passed against an out-of-date fixture proves nothing about a new build; the repo keeps a regression project (`ksa-upgrade.tests`) that grows with each game-build upgrade.

## Complete example

| File | Role |
|---|---|
| [the-tick.tests/the-tick.tests.csproj](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/the-tick.tests.csproj) | Minimal linked-source test project |
| [the-tick.tests/GameFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/the-tick.tests/GameFixture.cs) | Stand-in `KSA` types mirroring the build the patch targets |
| [garrys-torch.tests/GameLoopFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/GameLoopFixture.cs) | `PrepareFrame` call-order fixture |
| [garrys-torch.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/Program.cs) | Applies the real patch, asserts event order, `Require` |
| [Directory.Build.props](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/Directory.Build.props#L70-L80) | `RequiresKSAAssemblies` switch and early missing-DLL error |

## Variations and gotchas

- **A stand-in is a model.** If the game method gains a call the transpiler cares about, the fixture will not notice. Transpilers that match by call seams survive such differences; for exact IL, use approach 3.
- **`NoInlining` is not optional.** Without it the JIT may inline the stand-in into the caller and the patch never runs.
- **Say what is not covered.** The repo's rules require stating separately what still needs an in-game check.
- **Game updates.** Compare the fixture's member names and call order with the new decompile and update both together.

## See also

- [Overview](overview.md); [approach 2](test-harmony-patches-without-the-game-2.md), [approach 3](test-harmony-patches-without-the-game-3.md)
- [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md), [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- Mod feature: [Unscience managed test harnesses](../../../../mods/unscience/features/unscience-managed-test-harnesses.md)
