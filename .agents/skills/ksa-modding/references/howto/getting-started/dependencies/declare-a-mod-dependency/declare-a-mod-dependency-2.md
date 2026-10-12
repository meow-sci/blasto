---
type: How-To
title: Make a dependency optional and ship a vendored fallback copy of the contract
description: Mark a StarMap dependency Optional = true, import its contract assemblies, vendor pinned copies for when it is absent, and detect at run time which copy loaded
category: getting-started
group: dependencies
topic: declare-a-mod-dependency
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/gatos/features/purrtty-custom-shell-contract
  - mods/purrtty/features/custom-shell-contract-via-assembly-imports
  - mods/starmap/features/mod-dependency-graph
techniques: [toml-config, inter-mod-api, assembly-load-context]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: [KSA.ModLibrary]
tags: [optional-dependency, vendored, contract, purrtty, gatos, fallback, imported-assemblies]
---

gatOS exposes a VM terminal through purrTTY, but must also work when purrTTY is not installed. It declares purrTTY as
an **optional** dependency that imports purrTTY's two contract assemblies, ships its own pinned copies of those DLLs
as a fallback, and checks at run time which copy loaded. This is the template for integrating with another mod only
when it is installed. The required form is [Required dependency with ImportedAssemblies](./declare-a-mod-dependency-1.md); no declaration at all is
[Late-bound type lookup with AccessTools.TypeByName (plus: Marker registered at immediate load, or type lookup, plus copy-once settings import)](./declare-a-mod-dependency-3.md); the type-identity theory is [Importer lists the contract assembly in ImportedAssemblies](../share-types-between-mods/share-types-between-mods-1.md).

## Background: how the game does it

An optional dependency changes what happens when the target is missing. StarMap parks the mod (any missing dependency
parks it, optional or not), continues through the manifest, and in `TryLoadWaitingMods` initializes any parked mod whose
remaining missing dependencies are **all** optional:[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L106-L131)

```csharp
// StarMap.Core/ModRepository/ModLoader.cs L106-L131
private void TryLoadWaitingMods()
{
    var loadedMod = true;

    while (ModRegistry.WaitingModsDependencyGraph.Count > 0 && loadedMod)
    {
        loadedMod = false;
        foreach (var waitingMod in ModRegistry.WaitingMods)
        {
            if (waitingMod.NotLoadedModDependencies.Count == 0 || waitingMod.NotLoadedModDependencies.Values.All(dependencyInfo => dependencyInfo.Optional))
            {
                loadedMod = true;
                ModRegistry.WaitingMods.Remove(waitingMod);

                if (waitingMod.InitializeMod(ModRegistry))
                {
                    Console.WriteLine($"StarMap - Loaded mod: {waitingMod.ModId} after all mods were loaded, not loaded optional mods: {string.Join(",", waitingMod.NotLoadedModDependencies.Values.Select(mod => mod.ModId))}");
                }
                else
                {
                    Console.WriteLine($"StarMap - Failed to load mod:{waitingMod.ModId} after all mods were loaded, not loaded optional mods: {string.Join(",", waitingMod.NotLoadedModDependencies.Values.Select(mod => mod.ModId))}");
                }
                waitingMod.NotLoadedModDependencies.Clear();
            }
        }
    }
```

When the dependency *is* present, the importer's context resolves the listed assembly names from the dependency's
context **before** its own `deps.json` resolver. That is why a local copy never loads when the real one exists, and
why a local copy must still be resolvable (in `deps.json`) when it does not.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L49-L72) Optional-missing mods
initialize last, after the main pass.

## Lifecycle and timing

- Discovery and the import sets are fixed in `startup:starmap-before-main` (`StarMapCore.Init`).
- gatOS registers in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), after every mod's assemblies are loaded
  and purrTTY's registry exists; nothing boots at that point.
- Sessions are created later by purrTTY when the player opens a gatOS tab, off the main thread.

## Steps

1. **Declare it optional with imports** in `mod.toml`:[gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml)

   ```toml
   # gatOS.GameMod/mod.toml L6-L17
   [StarMap]
   EntryAssembly = "gatOS.GameMod"

   # D6: with purrTTY installed, StarMap resolves these two assemblies from purrTTY's ALC
   # (it exports them via ExportedAssemblies) — one Assembly, one Type identity, one shared
   # CustomShellRegistry.Instance that purrTTY's menus read. Optional: without purrTTY the
   # vendored copies in this mod folder load instead and gatOS idles (registration lands in
   # a registry nobody consumes; the VM stays manageable from the gatOS menu).
   [[StarMap.ModDependencies]]
   ModId = "purrTTY"
   Optional = true
   ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
   ```

   The comment credits purrTTY's `ExportedAssemblies`, but at StarMap 4dc60f8 that list is never read; the
   importer's `ImportedAssemblies` is what makes the names resolve from purrTTY ([purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml)).
2. **Vendor pinned copies and keep them in `deps.json`.** Reference the contract DLLs with `Private=true`, so they
   land in the output **and** in `gatOS.GameMod.deps.json`, without which the mod-local resolver could not find them
   and the no-purrTTY fallback would crash instead of idling:[gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L138-L154)

   ```xml
   <!-- gatOS.GameMod/gatOS.GameMod.csproj L145-L154 -->
       <ItemGroup>
           <Reference Include="purrTTY.CustomShellContract">
               <HintPath>$(MSBuildThisFileDirectory)../vendor/purrTTY/purrTTY.CustomShellContract.dll</HintPath>
               <Private>true</Private>
           </Reference>
           <Reference Include="purrTTY.Logging">
               <HintPath>$(MSBuildThisFileDirectory)../vendor/purrTTY/purrTTY.Logging.dll</HintPath>
               <Private>true</Private>
           </Reference>
       </ItemGroup>
   ```

   This is the opposite of the rule for game and StarMap assemblies (`Private=false`); see
   [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md).
3. **Register defensively.** The registry probe-instantiates one session to validate metadata, so the constructor must
   be trivial; catch duplicate-id and probe failures so only the terminal UI is lost:[gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L989-L1000)

   ```csharp
   // gatOS.GameMod/Mod.cs L989-L1003
       private static void RegisterShell(VmConnectionBroker broker)
       {
           try
           {
               // The registry probe-instantiates (and disposes) one session to validate Metadata —
               // SshShellSession's ctor is trivial by contract (T0.5), so this never touches the VM.
               CustomShellRegistry.Instance.RegisterShell(ShellId, () => new SshShellSession(broker));
           }
           catch (Exception ex)
           {
               // Duplicate id or probe failure — only the terminal UI is lost; VM management stays up.
               ModLog.Log.Error($"Could not register the '{ShellId}' shell: {ex.Message}", ex);
               return;
           }
   ```

4. **Detect which copy you got, by assembly location.** If the contract type's assembly sits in your own mod folder,
   the dependency is absent (the vendored copy loaded) and nobody consumes the registry:[gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L1003-L1023)

   ```csharp
   // gatOS.GameMod/Mod.cs L1007-L1023
           var contractDir = SafeAssemblyDir(typeof(CustomShellRegistry));
           var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
           var ownCopy = contractDir is not null && GatOsPaths.ModDir is { } modDir
                         && string.Equals(
                             Path.TrimEndingDirectorySeparator(contractDir),
                             Path.TrimEndingDirectorySeparator(modDir),
                             comparison);
           if (ownCopy)
           {
               ModLog.Log.Warn(
                   "purrTTY not detected: terminal UI unavailable. Install purrTTY to open gatOS "
                   + "sessions (the VM can still be managed from the gatOS menu).");
           }
           else
           {
               ModLog.Log.Info($"Registered shell '{ShellId}' with purrTTY (contract: {contractDir ?? "unknown"}).");
           }
   ```

   Comparing locations avoids a false positive from your vendored copy being present at all.
5. **Make the dependency's own startup order irrelevant.** purrTTY's menus enumerate `GetAvailableShells()` on every
   draw instead of caching, so it does not matter which mod's `AllModsLoaded` runs first.
6. **Treat the vendored DLLs as a pinned ABI** and refresh them deliberately; a mismatch with the installed purrTTY
   shows up as missing members at run time.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml) | Optional dependency with imports |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj) | Vendored references with `Private=true` |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs) | Registration and own-copy detection |
| [purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) | The provider's manifest (`ExportedAssemblies`, documentation only today) |

## Variations and gotchas

- **Optional-missing mods are retried last**, so your `StarMapBeforeMain` runs after all mods that were not parked.
- **Import the logging assembly the contract references too.** The contract loads in purrTTY's context and resolves
  `purrTTY.Logging` there; listing it in your own `ImportedAssemblies` makes your code see the same `Assembly`, so
  types crossing the contract boundary keep one identity (listing it in the provider's `ExportedAssemblies` does
  nothing at 4dc60f8).
- **The vendored copy must load as the same `Assembly` name**; do not rename.
- **Contract constructors must be cheap**: probe instances are created and disposed at registration.
- **Break risk is on the provider's side.** A purrTTY change that breaks the contract surfaces in gatOS as a crash
  that looks like gatOS's (gatOS notes record an unpatched purrTTY crashing on 5154 this way).

## See also

- [Required dependency with ImportedAssemblies](./declare-a-mod-dependency-1.md), [Late-bound type lookup with AccessTools.TypeByName (plus: Marker registered at immediate load, or type lookup, plus copy-once settings import)](./declare-a-mod-dependency-3.md), [Declare a dependency on another StarMap mod, required or optional](./overview.md)
- [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md), [Inter-mod shell contract shared through StarMap assembly imports](../../../../mods/purrtty/features/custom-shell-contract-via-assembly-imports.md),
  [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md)
- [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md), [Ship third-party DLLs next to your mod](../ship-third-party-dlls-with-a-mod/overview.md)
