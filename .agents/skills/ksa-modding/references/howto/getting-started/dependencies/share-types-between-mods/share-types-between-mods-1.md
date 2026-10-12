---
type: How-To
title: "Share a contract assembly: the importer lists it in ImportedAssemblies"
description: Publish a game-free contract assembly and let consumer mods resolve it from your load context so both sides share one Assembly, one Type identity and one static registry
category: getting-started
group: dependencies
topic: share-types-between-mods
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/purrtty/features/custom-shell-contract-via-assembly-imports
  - mods/starmap/features/shared-contract-assemblies
  - mods/gatos/features/purrtty-custom-shell-contract
  - mods/ksarmory/features/armoury-pack-registration-api
  - mods/purrtty/features/custom-shell-registry-and-lifecycle
techniques: [inter-mod-api, assembly-load-context, toml-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [inter-mod-api, contract-assembly, imported-assemblies, type-identity, assembly-load-context, purrtty]
---

Every StarMap mod loads into its own `AssemblyLoadContext`. If two mods each load `Contract.dll` from their own folder,
they get two different `Contract` assemblies: an `ICustomShell` from one is not the `ICustomShell` the other checks
for, and static fields are duplicated. purrTTY solves it for its shell API: it publishes a small contract assembly and
dependents list it in their own `ImportedAssemblies`, so StarMap hands them purrTTY's already-loaded copy. This is the
reference pattern for inter-mod APIs; designing the surface itself is [Text-only registration members with fault results, required dependency and a CI-checked API surface](./share-types-between-mods-2.md), and the
no-reference alternative is [Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](./share-types-between-mods-3.md). The dependency declarations are in
[Optional = true with vendored fallback copies of the contract](../declare-a-mod-dependency/declare-a-mod-dependency-2.md).

## Background: how the game does it

When mod B depends on mod A, StarMap computes a per-dependency **import set**; B's context asks A's context for those
assembly names before consulting B's own folder.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L126)[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L49-L72) The computation honors both
sides' lists in principle, but at StarMap 4dc60f8 `RuntimeMod.ExportedAssemblies` is never filled from `mod.toml`
(the config class holds the list, nothing copies it):[StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L7-L25)

| B's `ImportedAssemblies` | A's `ExportedAssemblies` | Effective at the pinned commit |
|---|---|---|
| empty | empty | A's `EntryAssembly` only (A registered first); nothing (A registered second) |
| set | any | B's `ImportedAssemblies` |
| empty | set | A's `EntryAssembly` only (A registered first); nothing (A second) |

So **only the importer's list is effective**. Shipping your own copy of the contract in the importer's folder produces a
second identity unless the import takes priority, which it does for names in the import set.

## Lifecycle and timing

Import sets are fixed during `StarMapCore.Init` (`startup:starmap-before-main`); resolution is lazy, at the first use of
a contract type in the importer, from any hook or thread. Registration with the host registry happens at
`startup:all-mods-loaded`; the host (purrTTY) enumerates the registry live on every draw, so neither mod relies on the
order of the two `AllModsLoaded` handlers.

## Steps

1. **Put the contract in its own assembly**, free of game and UI types. purrTTY's `purrTTY.CustomShellContract`
   holds `ICustomShell`, `CustomShellRegistry`, metadata, start options, events and exceptions; its only reference is
   `purrTTY.Logging` (a tiny Brutal `LogCategory` holder, see [One LogCategory in a tiny shared assembly](../../lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-4.md)):[purrTTY.CustomShellContract/purrTTY.CustomShellContract.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/purrTTY.CustomShellContract.csproj)

   ```xml
   <!-- purrTTY.CustomShellContract/purrTTY.CustomShellContract.csproj L13-L22 -->
       <ItemGroup>
           <ProjectReference Include="..\purrTTY.Logging\purrTTY.Logging.csproj" />
       </ItemGroup>

       <ItemGroup>
           <Reference Include="Brutal.Core.Logging" Condition="Exists('$(KSAFolder)/Brutal.Core.Logging.dll')">
               <HintPath>$(KSAFolder)/Brutal.Core.Logging.dll</HintPath>
               <Private>false</Private>
           </Reference>
       </ItemGroup>
   ```

2. **Document the exports in the provider's `mod.toml`** (for readers; not effective today) and include assemblies the
   contract references:[purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml)

   ```toml
   # purrTTY.GameMod/mod.toml L6-L12
   [StarMap]
   EntryAssembly = "purrTTY.GameMod"
   # The published inter-mod ABI: mods implementing ICustomShell resolve these from
   # purrTTY's ALC so both sides share one CustomShellRegistry.Instance (same Assembly
   # -> same Type identity -> same statics). purrTTY.Logging rides along because the
   # contract assembly references it - an importer needs both from the same ALC.
   ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
   ```

3. **In the consumer, import both assemblies** with an optional dependency (the effective half):[gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml)

   ```toml
   # gatOS.GameMod/mod.toml L14-L17
   [[StarMap.ModDependencies]]
   ModId = "purrTTY"
   Optional = true
   ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
   ```

4. **Reference the contract at compile time; vendor a pinned copy only if the dependency is optional**
   (`Private=true` puts it in `deps.json`; with purrTTY installed the import wins and the local copy never loads). The
   vendoring recipe is [Optional = true with vendored fallback copies of the contract](../declare-a-mod-dependency/declare-a-mod-dependency-2.md); for a required dependency use `Private=false` and no copy.
5. **Register from `StarMapAllModsLoaded`** and keep the registration cheap and fault-proof. The registry validates a
   shell by creating a temporary instance, so constructors must be trivial and `Dispose` safe on a never-started
   instance:[purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs#L36-L75)

   ```csharp
   // purrTTY.CustomShellContract/CustomShellRegistry.cs L42-L58 (excerpt)
       public void RegisterShell<T>(string shellId, Func<T> factory) where T : class, ICustomShell
       {
           if (string.IsNullOrWhiteSpace(shellId))
           {
               throw new ArgumentException("Shell ID cannot be null or empty", nameof(shellId));
           }

           if (factory == null)
           {
               throw new ArgumentNullException(nameof(factory));
           }

           try
           {
               // Validate the shell implementation by creating a temporary instance
               using var testInstance = factory();
               ValidateShellImplementation(testInstance, shellId);
   ```

   The consumer side in gatOS: `CustomShellRegistry.Instance.RegisterShell(ShellId, () => new SshShellSession(broker))`
   inside a `try`/`catch`, followed by an own-copy check (assembly location versus the mod folder) that logs "purrTTY
   not detected" when the vendored copy loaded.[gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L989-L1023)
6. **Stay inert when the host is absent.** The registration lands in a private registry nobody reads; the rest of the
   mod keeps working. Never throw from the registration path.

## Complete example

| File | Role |
|---|---|
| [purrTTY.CustomShellContract/purrTTY.CustomShellContract.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/purrTTY.CustomShellContract.csproj), [purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs) | The contract assembly and its static registry |
| [purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) | Provider manifest (exports are documentation today) |
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml), [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | Consumer manifest and vendored references |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs) | Registration and own-copy detection |

Also merged here from the registry-contract notes: `ICustomShell` carries `Metadata`, `IsRunning`, `OutputReceived`,
`Terminated`, `StartAsync`, `StopAsync`, `WriteInputAsync` and `NotifyTerminalResize`; the host presents in-process
shells as pseudo-terminal process managers. Keep the contract free of display and game types and pass configuration as
environment variables (`WellKnownShellEnvironment`).

## Variations and gotchas

- **Import the logging assembly the contract references too.** The importer must list both names in its own
  `ImportedAssemblies` (step 3) so both resolve from the provider's context; otherwise the types split. Listing them in
  the provider's `ExportedAssemblies` alone does nothing.
- **ExportedAssemblies is parsed but never effective at 4dc60f8.** Rely on `ImportedAssemblies`; keep exports as
  documentation for a future fix. The API README claims otherwise.
- **Names are exact** (case-sensitive simple assembly names, no `.dll`).
- **Treat the surface as a versioned ABI.** A mismatch with the installed provider shows up as missing members at run
  time.
- **Optional = true with `Private = true`** copies the contract into deps.json; detection by assembly location prevents
  mistaking your vendored copy for the real host.
- **Both mods must not have loaded their own copy first**; B's own copy can only load through B's resolver, which runs
  after the dependency step.

## See also

- [Text-only registration members with fault results, required dependency and a CI-checked API surface](./share-types-between-mods-2.md), [Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](./share-types-between-mods-3.md), [StarMap's name-based attribute binding](./share-types-between-mods-4.md), [Share types and design an inter-mod API between StarMap mods](./overview.md)
- [Inter-mod shell contract shared through StarMap assembly imports](../../../../mods/purrtty/features/custom-shell-contract-via-assembly-imports.md), [Share contract assemblies between mods with ExportedAssemblies and ImportedAssemblies](../../../../mods/starmap/features/shared-contract-assemblies.md),
  [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md), [Custom shell registry, lifecycle and PTY bridge](../../../../mods/purrtty/features/custom-shell-registry-and-lifecycle.md)
- [Declare a dependency on another StarMap mod, required or optional](../declare-a-mod-dependency/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md), [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md)
