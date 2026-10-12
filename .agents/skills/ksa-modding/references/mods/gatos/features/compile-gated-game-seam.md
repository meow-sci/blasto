---
type: Mod Feature
title: Compile-gated game seam
description: Split the mod class into a game-free half and a Game/ half compiled only when KSA.dll is present, joined by C# partial methods that vanish when the game assemblies are absent, so CI and every library build and test without private game DLLs
mod: gatos
category: getting-started
techniques: [partial-methods, msbuild-conditions, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: []
tags: [architecture, msbuild, partial-methods, testing, ci, dependency-rule]
---

KSA's assemblies are not redistributable, so a public CI cannot reference them, and most of a large
mod has nothing to do with the game. gatOS enforces a strict layering rule and uses a C# language
feature, partial methods without implementations, to let even the mod entry project compile with no
game DLLs at all.

## How it works

**Dependency rule** (repo AGENTS.md): only `gatOS.GameMod` may reference KSA, Brutal or StarMap
assemblies, and inside it KSA type names are meant to stay under `gatOS.GameMod/Game/Ksa/` (in
practice the `Game/` glue such as `Mod.Game.cs` and `TelemetrySampler.cs` also names `Program`,
`Universe` and `Vehicle`). All other
projects (VM host, SSH, 9p server, `/sim` tree, HTTP, MQTT, MCP, serial, paint rules) speak
game-free types: `SimSnapshot` for reads and `SimCommand` for writes.

**Compile gating** ([gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L19-L28)): the project sets
`KsaAssembliesPresent` only when `$(KSAFolder)/KSA.dll` exists and otherwise removes `Game/**/*.cs`
from compilation; each KSA reference also carries `Condition="Exists(...)"` and `Private=false`.

**Partial-method seam** ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L1040-L1060)): `Mod.cs` (always compiled)
declares `partial void SampleTelemetry(double dt);`, `DrainCommands()`, `DriveWelds(double dt)`,
`InstallSolverHook()` and so on, and calls them from its StarMap hooks. The implementations live in
`Game/Mod.Game.cs`. A partial method with no implementation is removed by the compiler together
with every call to it, so without the game assemblies the hooks still exist and simply do nothing.

**`NoInlining` on implementations** ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L215-L225)): each
implementation is marked `MethodImplOptions.NoInlining` so that, if a game assembly is missing at
runtime, the `TypeLoadException` is raised at the call site inside a try/catch in `Mod.cs` instead
of when the JIT compiles the calling hook method.

**KSAFolder resolution** ([Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props#L28-L64)): env `KSA_DLL_DIR`, else a sibling
`ksa-game-assemblies/current/dll/` checkout, else the default install path per OS.

## Lifecycle and timing

The seam is compile-time. At runtime with the game present, the partial implementations run in the
hooks described in [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md); the
latch design of those hooks is itself shaped by the seam (the game-free half cannot read
`Program.FrameNumber`).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | `KsaAssembliesPresent` and `Compile Remove` |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L1038-L1060) | partial method declarations |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs) | game-coupled implementations |
| [Directory.Build.props](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/Directory.Build.props) | `KSAFolder` and deploy path resolution, warnings as errors |
| [gatos.slnx](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatos.slnx) | 21-project solution: 11 libraries and the mod, 10 test projects |

## Snippets

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

## Game APIs touched

None directly; this is a build and architecture technique.

## Pitfalls and version notes

- Partial methods used this way must return `void` and have no `out` parameters (C# rules for
  implementation-optional partials).
- Keep KSA types out of the always-compiled half, including in attributes and signatures; one stray
  type reference breaks the no-game build.
- A namespace named `Camera` under `Game/Ksa/` shadows `KSA.Camera`; gatOS aliases the game type
  (`using KsaCamera = KSA.Camera;`) where needed.

## See also

- [Build, deploy and CI](build-deploy-and-ci.md)
- [KSA anchor churn tracking](ksa-anchor-churn-tracking.md)
- [Logging shim and perf stats](logging-and-perfstats.md)
- [gatOS overview](../overview.md)
- [Split a mod into a game-free half and a compile-gated Game half](../../../howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-2.md) - how-to article demonstrating this feature
- [Build and test in CI with no game at all using a compile-gated game seam](../../../howto/getting-started/build/build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-3.md) - how-to article demonstrating this feature
- [StarMapAllModsLoaded: work that needs the full mod set](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md) - how-to article demonstrating this feature
- [Compile-gate the game-facing half with partial methods and gate slow tests behind an opt-in switch](../../../howto/testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/unit-test-mod-logic-without-game-5.md) - how-to article demonstrating this feature

