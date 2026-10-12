---
type: How-To
title: Split a mod into a game-free half and a compile-gated Game half
description: Keep typed game access in a Game/ folder that is compiled only when KSA.dll is present, bridged by partial methods, so the rest of the mod, including tests, builds anywhere
category: getting-started
group: build
topic: build-a-mod-without-game-references
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/compile-gated-game-seam]
techniques: [msbuild, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.Universe]
tags: [msbuild, compile-gating, partial-methods, testability, ci, gatos, layering]
---

KSA's assemblies are not redistributable, so a public CI cannot reference them, and most of a large mod has nothing to
do with the game. gatOS enforces a strict layering rule and uses a C# language feature, partial methods without
implementations, to let even the mod entry project compile with **no game DLLs at all**. Only the `Game/` folder of
`gatOS.GameMod` names KSA types; all other projects speak game-free types (`SimSnapshot` for reads, `SimCommand` for
writes). The alternative that needs no game types anywhere is [Reflection-only mod with visible breakage](./build-a-mod-without-game-references-1.md); wrapping typed
access in small providers is [Static provider library](./build-a-mod-without-game-references-3.md); the CI use is
[Compile-gated game seam](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-3.md).

## Background: how the game does it

At run time the game is present and the `Game/` half works against the real types. Compile gating changes only what the
build sees. A partial method with no implementation is removed by the compiler together with every call to it, so
without the game assemblies the StarMap hooks still exist and simply do nothing. With the game, the implementation
references game types; if an assembly is missing at **run time**, the `TypeLoadException` is raised when the JIT compiles
the method that references it, which is why each implementation is `NoInlining` and called inside a `try`.

## Lifecycle and timing

The seam is compile-time. At run time with the game present, the partial implementations run in the hooks that call
them: `InstallGameLogging` at `startup:all-mods-loaded` ([Game-free logging interface swapped for a Brutal sink at load](../../lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-5.md)), telemetry sampling and command
draining on the frame hooks, UI drawing at `frame:draw-ui`. The latch design of those hooks is itself shaped by the
seam: the game-free half cannot read `Program.FrameNumber`.

## Steps

1. **State the dependency rule.** Only the entry project may reference KSA, Brutal or StarMap assemblies; inside it,
   KSA type names stay under `Game/Ksa/` (in practice `Game/` glue names `Program`, `Universe` and `Vehicle` too). Other
   projects exchange snapshots and commands.
2. **Gate the compile** on the presence of `KSA.dll`:[gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L19-L28)

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

   Each KSA reference also carries `Condition="Exists(...)"` and `Private=false`; `KSAFolder` resolves from
   `KSA_DLL_DIR`, a sibling `ksa-game-assemblies/current/dll/` checkout, or the default install path
   ([Tiered KsaDllDir resolution in Directory.Build.props](../reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-2.md)).[Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props)
3. **Declare the seams in the always-compiled half** as partial methods, and call them from the StarMap hooks:[gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L1039-L1060)

   ```csharp
   // gatOS.GameMod/Mod.cs L1040-L1046
       // assemblies; the calls drop out otherwise).
       partial void InstallGameLogging();
       partial void DrawGameUi();
       partial void SampleTelemetry(double dt);
       partial void DrainCommands();
       partial void TickSchedules(double dt);
       partial void DriveAudio();
   ```

4. **Implement them in `Game/Mod.Game.cs`**, marked `NoInlining` so a missing assembly fails at the call site (inside a
   `try`/`catch` in `Mod.cs`) rather than when the JIT compiles the calling hook, and fault-contained by the sampler's
   own `catch`:[gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L214-L250)

   ```csharp
   // gatOS.GameMod/Game/Mod.Game.cs L214-L219
       // NoInlining on the partial impls: their bodies reference game assemblies, and a missing
       // assembly must fail at the call site (where Mod.cs catches it), not at the JIT of the
       // calling method — inlining would hoist the type resolution into the caller.
       [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
       partial void InstallGameLogging() => ModLog.SetLogger(new BrutalModLogger());
   ```

5. **Keep game types out of the always-compiled half**, including attributes and signatures: one stray type reference
   breaks the no-game build.
6. **Build both ways in CI:** with the game (full) and without (game-free projects and unit tests only). The repo's
   workflow checks out the private assemblies with a token when available and still builds when not
   ([Compile-gated game seam](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-3.md)).

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | `KsaAssembliesPresent` and `Compile Remove` |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs) | Partial declarations and the hooks that call them |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs) | Typed implementations (NoInlining) |
| [Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props) | `KSAFolder` resolution |

## Variations and gotchas

- **Partial methods used this way must return `void` and have no `out` parameters.**
- **A namespace named `Camera` under `Game/Ksa/` shadows `KSA.Camera`**; gatOS aliases the game type
  (`using KsaCamera = KSA.Camera;`) where needed.
- **Tests never need the game.** Every library has an NUnit project; `gatOS.GameMod` has none by design (it is the
  game-coupled glue), and integration tests self-skip unless `GATOS_IT=1`.
- **Cost:** the seam adds an indirection and a discipline rule that must be reviewed; a type used in the wrong half is
  caught only by the no-game build.

## See also

- [Reflection-only mod with visible breakage](./build-a-mod-without-game-references-1.md), [Static provider library](./build-a-mod-without-game-references-3.md), [Build a KSA mod that references only StarMap.API and Harmony](./overview.md)
- [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md), [Logging shim and perf stats](../../../../mods/gatos/features/logging-and-perfstats.md)
- [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md), [Log from a mod: per-mod log file, game log category and log-once](../../lifecycle/write-a-per-mod-log-file/overview.md)
