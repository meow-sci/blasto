---
type: Mod Feature
title: Order mod loading with a ModDependencies graph
description: StarMap parks mods whose [[StarMap.ModDependencies]] are not registered yet, releases them when the dependency loads, and retries the rest at the end, loading mods whose only missing dependencies are Optional
mod: starmap
category: getting-started
techniques: [toml-config, inter-mod-api]
hooks: [call RuntimeMod.AllDependenciesLoaded / CheckForDependentMods / ModLoader.TryLoadWaitingMods]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.ModEntry]
tags: [starmap, dependencies, load-order, optional-dependency, mod-toml]
---

A mod declares the mods it needs in `mod.toml`, and StarMap uses those declarations for two
things: the order in which mods are initialized (and therefore in which `StarMapBeforeMain` runs)
and which assemblies each mod may resolve from another mod's load context. This page covers the
ordering; the assembly sharing is in [shared contract assemblies](shared-contract-assemblies.md).

```toml
# KSA-AdvancedFlightComputer: AdvancedFlightComputer/mod.toml L1-L9
name = "AdvancedFlightComputer"
patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

[StarMap]
EntryAssembly = "AdvancedFlightComputer"

[[StarMap.ModDependencies]]
ModId = "KittenExtensions"
Optional = true
```

## How it works

Fields of each `[[StarMap.ModDependencies]]` entry: `ModId` (the dependency's manifest id, which
is its folder name), `Optional` (default `false`) and `ImportedAssemblies` (default empty).[^sm-api-readme]

State kept per mod (`RuntimeMod`) and in the registry (`ModRegistry`):[^sm-runtimemod][^sm-modregistry]

| Field | Meaning |
|---|---|
| `ModRegistry._mods` | Every mod whose entry assembly loaded, initialized or not |
| `RuntimeMod.Dependencies` | Dependency mod -> set of assembly names this mod may import from it |
| `RuntimeMod.NotLoadedModDependencies` | Declared dependencies not in the registry yet |
| `ModRegistry.WaitingMods` | Mods parked because of missing dependencies |
| `ModRegistry.WaitingModsDependencyGraph` | Missing dependency id -> mods waiting for it |

Algorithm of `ModLoader.PrepareMods` ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104)), per enabled manifest entry in manifest order:[^sm-modloader]

1. Load the entry assembly and add the mod to the registry.
2. `AllDependenciesLoaded`: for each declared dependency, if the registry has it, record it in
   `Dependencies` with the computed import set; otherwise record it as not loaded and add this
   mod to the graph under the missing id. Any missing dependency (optional or not) parks the mod.
3. If nothing is missing, `InitializeMod`: create the `[StarMapMod]` instance, bind its hooks,
   and call its `StarMapBeforeMain` immediately.
4. `CheckForDependentMods`: for each mod waiting on the one just initialized, add it to the
   waiter's `Dependencies` (with exactly the waiter's `ImportedAssemblies`), remove it from the
   waiter's missing list, and initialize the waiter if nothing else is missing.
5. After the manifest pass, `TryLoadWaitingMods` loops over the parked mods: any mod whose
   remaining missing dependencies are all `Optional` is initialized; the loop repeats while a pass
   initialized something. Whatever is still parked is reported as failed ("Failed to load
   mod:X after all mods were loaded, missing mods (some may be optional): ...") and dropped.

## Lifecycle and timing

The graph is resolved entirely inside `StarMapCore.Init`, before KSA's `Main`
(`startup:starmap-before-main`). Its output matters later in two ways:

- **`StarMapBeforeMain` order** is initialization order. A dependency's `BeforeMain` is not
  guaranteed to have run first (see pitfalls).
- **Per-frame hook order.** `ModRegistry.AddModMethod` appends to one list per attribute type, so
  every hook (`StarMapBeforeGui`, `StarMapAfterGui`, `StarMapAllModsLoaded`, ...) is invoked across
  mods in initialization order.

For cross-mod setup that must see every mod initialized, use `StarMapAllModsLoaded`: it fires
after KSA's `ModLibrary.LoadAll`, long after the graph has settled.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L107) | `AllDependenciesLoaded`: resolve or park |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L164-L182) | `CheckForDependentMods`: release waiters |
| [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L106-L141) | `TryLoadWaitingMods`: optional-dependency pass and failure report |
| [StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs#L9-L32) | Registry and waiting structures |

## Snippets

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L78-L107
public bool AllDependenciesLoaded(ModRegistry modRegistry)
{
    foreach (var dependency in Config.ModDependencies)
    {
        if (!modRegistry.TryGetMod(dependency.ModId, out var modDependency))
        {
            NotLoadedModDependencies.Add(dependency.ModId, dependency);

            if (!modRegistry.WaitingModsDependencyGraph.TryGetValue(dependency.ModId, out var dependents))
            {
                dependents = [];
                modRegistry.WaitingModsDependencyGraph[dependency.ModId] = dependents;
            }
            dependents.Add(this);
        }
        else
        {
            Dependencies.Add(modDependency, [.. CalculateUseableAssemblies(modDependency, dependency)]);
        }
    }

    if (NotLoadedModDependencies.Count > 0)
    {
        
        modRegistry.WaitingMods.Add(this);
        return false;
    }

    return true;
}
```

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L164-L182
public List<RuntimeMod> CheckForDependentMods(ModRegistry modRegistry)
{
    List<RuntimeMod> loadableMods = [];
    if (modRegistry.WaitingModsDependencyGraph.TryGetValue(ModId, out var modDependents))
    {
        foreach (var modDependent in modDependents)
        {
            var dependencyInfo = modDependent.NotLoadedModDependencies[ModId];
            modDependent.Dependencies.Add(this, [.. dependencyInfo.ImportedAssemblies]);
            if (modDependent.NotLoadedModDependencies.Remove(ModId) && modDependent.NotLoadedModDependencies.Count == 0)
            {
                modRegistry.WaitingMods.Remove(modDependent);
                loadableMods.Add(modDependent);
            }
        }
        modRegistry.WaitingModsDependencyGraph.Remove(ModId);
    }
    return loadableMods;
}
```

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

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `ModEntry.Id` (manifest order) | [current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L23-L24) | `ModId` is compared with this id; manifest order drives the first pass |

## Pitfalls and version notes

The points below are traced from the code at the pinned commit, not tested in game.

- **"Present" means registered, not initialized.** `TryGetMod` succeeds for any mod whose entry
  assembly loaded, including one that is itself parked. If A is listed before B, B depends on A,
  and A is parked on a missing optional dependency, B finds A registered and initializes in the
  main pass, while A initializes only in `TryLoadWaitingMods`; B's `StarMapBeforeMain` runs first.
- **Chains deeper than one level depend on manifest order.** `CheckForDependentMods` is called only
  for mods initialized in the main pass, not for the waiters it releases, and `TryLoadWaitingMods`
  never clears a waiter's missing entry for a dependency that loaded during that phase. With
  `C -> B -> A` listed as C, B, A, C ends up reported as failed even though B loaded. Listing
  dependencies above dependents in `manifest.toml` avoids it.
- **Dependencies must have code.** A content-only mod never enters the registry (no entry DLL), so
  a required dependency on it can never be satisfied. Declare it `Optional` or not at all.
- **Disabled dependencies count as missing.** Disabled manifest entries are skipped before
  registration.
- **Import set depends on order too.** A dependency registered before its dependent gets the
  computed import set; one registered after gets exactly `ImportedAssemblies`. Details in
  [shared contract assemblies](shared-contract-assemblies.md).
- **Optional means "may be absent".** The mod still initializes without it and must cope. gatOS
  ships fallback copies of purrTTY's contract assemblies for that case.[^gatos-modtoml]
- **Test harness pattern.** AdvancedFlightComputer's harness-test mod declares required
  dependencies on `HeadlessHarness` and on the mod under test, so it only loads when both are
  present.[^afc-harness-modtoml][^afc-modtoml]

## See also

- [Share contract assemblies between mods](shared-contract-assemblies.md)
- [Discover code mods through KSA's mod manifest](manifest-mod-discovery.md)
- [Discover lifecycle hooks by attribute type name](attribute-hook-discovery.md)
- [Startup lifecycle hooks](startup-lifecycle-hooks.md)
- [Declare a required dependency with ImportedAssemblies](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-1.md) - how-to article demonstrating this feature
- [Make a dependency optional and ship a vendored fallback copy of the contract](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-2.md) - how-to article demonstrating this feature
- [Follow the boot sequence: host process, load contexts and hook binding](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md) - how-to article demonstrating this feature
- [Write mod.toml: KSA keys and the StarMap table](../../../howto/getting-started/loader/mod-toml-reference/mod-toml-reference.md) - how-to article demonstrating this feature

[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sm-modregistry]: StarMap.Core/ModRepository/ModRegistry.cs
[^sm-api-readme]: StarMap.API README
[^afc-modtoml]: KSA-AdvancedFlightComputer AdvancedFlightComputer/mod.toml
[^afc-harness-modtoml]: KSA-AdvancedFlightComputer harness tests mod.toml
[^gatos-modtoml]: gatOS gatOS.GameMod/mod.toml
