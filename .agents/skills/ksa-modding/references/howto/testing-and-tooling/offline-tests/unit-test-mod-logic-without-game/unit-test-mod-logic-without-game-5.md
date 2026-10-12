---
type: How-To
title: Compile-gate the game-facing half with partial methods and gate slow tests behind an opt-in switch
description: Drop the whole Game folder out of compilation when the KSA assemblies are absent, join it to the game-free half with partial methods, and let integration tests skip themselves unless an environment variable opts in
category: testing-and-tooling
group: offline-tests
topic: unit-test-mod-logic-without-game
approach: 5
difficulty: advanced
prerequisites: [dotnet-sdk, nunit]
demonstrated_by: [mods/gatos/features/compile-gated-game-seam, mods/gatos/features/build-deploy-and-ci]
techniques: [msbuild, partial-methods, compile-gating, opt-in-integration-tests]
hooks: []
lifecycle: [startup:all-mods-loaded, event:headless-test-run]
game_types: []
tags: [testing, msbuild, partial-methods, ci, integration-tests, boundary]
---

Outcome: the mod entry project builds and its tests run on a CI machine with no KSA assemblies at all, because the game-coupled files drop out of compilation and the calls into them disappear; real-VM or other heavy tests skip themselves locally and are forced on in CI.
Demonstrated by gatOS ([gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L19-L28)). Prerequisites: a layered solution where only one project may reference game assemblies.

## Background: how the game does it

KSA's assemblies are not redistributable, so a public CI cannot reference them. gatOS makes that a property of the build. The dependency rule from its repo notes: only `gatOS.GameMod` may reference KSA, Brutal or StarMap assemblies, and all other projects speak game-free types (`SimSnapshot` for reads, `SimCommand` for writes). Even inside `GameMod`, the game-coupled code lives under `Game/`, which is compiled only when `KSA.dll` is present. A StarMap entry class that is always compiled calls game-coupled work through partial methods, so the hook attributes exist either way; see [Choose a StarMap startup hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md).

## Lifecycle and timing

The seam is compile-time. At runtime with the game present, the partial implementations run in the StarMap hooks (`startup:all-mods-loaded` for installing logging, per-frame hooks for sampling and command drains); without the game they do nothing. The test side runs as ordinary `dotnet test`. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve `KSAFolder` once, first match wins:** the `KSA_DLL_DIR` environment variable (what CI uses), a `ksa-game-assemblies` checkout next to the repo, then per-OS defaults. The comment block in `Directory.Build.props` explains that references are condition-guarded so every game-free project and its tests build even when the DLLs are absent ([Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props#L28-L64)).

2. **Set a property when the assemblies exist and remove `Game/` otherwise:**

   ```xml
   <!-- gatOS.GameMod/gatOS.GameMod.csproj L19-L28 -->
           <!-- The Game/ sources (ImGui status window, ModMenu entry, Brutal-backed logger) compile
                only when the KSA reference assemblies are present. CI without the private game DLLs
                still builds this project: the partial-method seams in Mod.cs simply drop out
                (see .github/workflows/build.yml). -->
           <KsaAssembliesPresent Condition="Exists('$(KSAFolder)/KSA.dll')">true</KsaAssembliesPresent>
       </PropertyGroup>

       <ItemGroup Condition="'$(KsaAssembliesPresent)' != 'true'">
           <Compile Remove="Game\**\*.cs"/>
       </ItemGroup>
   ```

   Each KSA reference carries `Condition="Exists(...)"` and `Private=false` so nothing is copied into the mod folder.

3. **Declare the game-coupled seams as partial methods in the always-compiled file.** A partial method with no implementation is removed by the compiler together with every call to it, so without the game assemblies the hooks still exist and do nothing:

   ```csharp
   // gatOS.GameMod/Mod.cs L1038-L1046

       // Game-coupled seams, implemented in the Game/ partial files (compiled only with the KSA
       // assemblies; the calls drop out otherwise).
       partial void InstallGameLogging();
       partial void DrawGameUi();
       partial void SampleTelemetry(double dt);
       partial void DrainCommands();
       partial void TickSchedules(double dt);
       partial void DriveAudio();
   ```

4. **Implement them in `Game/Mod.Game.cs` with `NoInlining`.** If a game assembly is missing at runtime, the `TypeLoadException` must be raised at the call site (where `Mod.cs` catches it), not when the JIT compiles the calling hook method:

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L215-L219
       // NoInlining on the partial impls: their bodies reference game assemblies, and a missing
       // assembly must fail at the call site (where Mod.cs catches it), not at the JIT of the
       // calling method — inlining would hoist the type resolution into the caller.
       [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
       partial void InstallGameLogging() => ModLog.SetLogger(new BrutalModLogger());
   ```

5. **Gate slow or platform-specific tests behind an opt-in switch.** Plain `dotnet test` must never require QEMU; tests that need it self-skip with `Assert.Ignore` unless `GATOS_IT=1`, and with that variable set, missing prerequisites are hard failures so CI cannot silently skip:

   ```csharp
   // gatOS.Vm.Tests/TestEnv.cs L1-L36
   namespace gatOS.Vm.Tests;

   /// <summary>
   ///     Shared gating helpers for tests that need external tools or a real VM. Plain
   ///     <c>dotnet test</c> must never require QEMU (OS_PLAN.md M3 header); tests that do need it
   ///     self-skip via <see cref="Assert.Ignore(string)"/> unless <c>GATOS_IT=1</c> is set, in
   ///     which case missing prerequisites are hard failures (CI must not silently skip).
   /// </summary>
   internal static class TestEnv
   {
       /// <summary>True when the env opts into integration tests (<c>GATOS_IT=1</c>).</summary>
       public static bool IntegrationEnabled
           => Environment.GetEnvironmentVariable("GATOS_IT") == "1";

       /// <summary>Skips the test unless <c>GATOS_IT=1</c>.</summary>
       public static void RequireIntegration()
       {
           if (!IntegrationEnabled)
               Assert.Ignore("Integration test: set GATOS_IT=1 (needs QEMU + fetched guest artifacts).");
       }

       /// <summary>
       ///     Resolves QEMU, skipping the test when it is absent and integration mode is off.
       /// </summary>
       public static QemuBinaries RequireQemu()
       {
           try
           {
               return QemuLocator.Find();
           }
           catch (QemuNotFoundException) when (!IntegrationEnabled)
           {
               Assert.Ignore("QEMU not found on this host — install it to run disk/VM tests "
                             + "(CI installs it and sets GATOS_IT=1).");
               throw; // unreachable; Assert.Ignore throws
           }
   ```

6. **Turn the switch on in CI only:**

   ```yaml
   # .github/workflows/build.yml L97-L101
         - name: Test
           id: test
           env:
             GATOS_IT: "1"
           run: dotnet test gatos.slnx --nologo -v quiet --logger trx --results-directory .tmp/test-results
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | `KsaAssembliesPresent` and `Compile Remove` |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs) | Always-compiled entry with partial method declarations |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs) | Game-coupled implementations |
| [Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props) | `KSAFolder` resolution and conditional references |
| [gatOS.Vm.Tests/TestEnv.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm.Tests/TestEnv.cs) | Opt-in gating helpers |
| [.github/workflows/build.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.github/workflows/build.yml) | CI build and test gate |

## Variations and gotchas

- **Partial methods must return void and have no out parameters** to be removable; anything that returns a value needs a different seam (an interface with a null object).
- **Inlining defeats the guard.** Without `NoInlining` the JIT can hoist type resolution into the caller and the failure moves into the hook method itself.
- **Game-free code still cannot read game state.** gatOS's hook latch is shaped by this: the game-free half cannot read `Program.FrameNumber`.
- **Skip messages cost output.** Prefer silent skips when the suite is large (approach 4); here the message names the missing prerequisite on purpose.
- **Game updates.** Only the `Game/` folder needs review after a build; the game-free projects are untouched.

## See also

- [Overview](overview.md); [approach 4](unit-test-mod-logic-without-game-4.md), [approach 1](unit-test-mod-logic-without-game-1.md)
- [Build a mod without game references](../../../getting-started/build/build-a-mod-without-game-references/overview.md), [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md)
- Mod features: [gatOS compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md), [gatOS build, deploy and CI](../../../../mods/gatos/features/build-deploy-and-ci.md)
