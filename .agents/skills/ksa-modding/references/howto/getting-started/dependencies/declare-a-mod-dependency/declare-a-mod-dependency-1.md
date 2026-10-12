---
type: How-To
title: Declare a required dependency with ImportedAssemblies
description: Add a [[StarMap.ModDependencies]] entry so StarMap loads your mod after another, shares the assembly you compile against, and parks your mod until the dependency initializes
category: getting-started
group: dependencies
topic: declare-a-mod-dependency
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/mod-dependency-graph
  - mods/ksarmory/features/armoury-pack-registration-api
  - mods/shaderextensions/features/generated-mod-toml
techniques: [toml-config, assembly-load-context, inter-mod-api]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: [KSA.ModLibrary]
tags: [mod-dependency, mod-toml, imported-assemblies, load-order, starmap, armoury]
---

When your mod needs another mod's code, a `[[StarMap.ModDependencies]]` entry in `mod.toml` tells StarMap three things:
initialize me after that mod, let my load context resolve these assembly names from that mod's context, and park me
until it is up. KSArmory weapon packs and ShaderExtensions (which requires KittenExtensions Continued) are the
reference users. This article covers a **required** dependency; optional ones with fallbacks are
[Optional = true with vendored fallback copies of the contract](./declare-a-mod-dependency-2.md) and probing without a declaration is [Late-bound type lookup with AccessTools.TypeByName (plus: Marker registered at immediate load, or type lookup, plus copy-once settings import)](./declare-a-mod-dependency-3.md). Sharing types
is the topic [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md).

## Background: how the game does it

KSA knows nothing about dependencies; StarMap implements them in `ModLoader.PrepareMods`. Per enabled manifest entry
it loads the entry assembly and calls `AllDependenciesLoaded`: each declared dependency that is already registered is
recorded with its import set, any that is not is recorded as missing and the mod is parked in `WaitingMods`. A mod
with nothing missing is initialized (`StarMapBeforeMain` runs right away); after each initialization
`CheckForDependentMods` releases the waiters that were waiting for it.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L107)

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

At load time your context resolves each requested assembly name by asking, in order, `Default`, the game context, the
contexts of dependencies whose import set contains the name, then your own `deps.json`.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L72) The full
algorithm is in [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md).

## Lifecycle and timing

The graph resolves during `StarMapCore.Init`, i.e. `startup:starmap-before-main`, before `Program.Main`. Effects:

- `StarMapBeforeMain` order across mods is initialization order. A dependency's `BeforeMain` is not guaranteed to
  have run before yours if it was itself parked (see gotchas).
- A pack's call into the dependency from its own `StarMapBeforeMain` lands after the dependency's assembly is loaded
  and long before the dependency's `StarMapAllModsLoaded`, which KSArmory uses to close registration.
- `StarMapAllModsLoaded` and later hooks run in initialization order; all mods are initialized by then.

## Steps

1. **Find the dependency's id.** It is the dependency's mod **folder name** (the manifest id), not its display
   `name`. For KittenExtensions Continued the id is `KittenExtensionsContinued` while the assembly is
   `KittenExtensions`.
2. **Reference its assembly at compile time without shipping it.** ShaderExtensions references the dependency's
   DLL by `HintPath` with `Private=False`, and declares the dependency as a project item that its build turns into
   `mod.toml`:[ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L57-L72)

   ```xml
   <!-- ShaderExtensions/ShaderExtensions.csproj L57-L72 -->
   	<ItemGroup>
   		<Reference Include="StarMap.API">
   			<HintPath>$(KSAModsDir)StarMap/StarMap.API.dll</HintPath>
   			<Private>False</Private>
   		</Reference>
   		<Reference Include="KittenExtensions">
   			<HintPath>$(KSAModsDir)KittenExtensionsContinued/KittenExtensions.dll</HintPath>
   			<Private>False</Private>
   		</Reference>
   	</ItemGroup>

   	<ItemGroup>
   		<StarMapModDependency Include="KittenExtensionsContinued">
   			<ImportedAssemblies>KittenExtensions</ImportedAssemblies>
   		</StarMapModDependency>
   	</ItemGroup>
   ```

   Generating the manifest from these items is [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md).
3. **Declare it in `mod.toml`** (the generated result for ShaderExtensions):

   ```toml
   # adapted: the shape ShaderExtensions/CopyOutput.targets L56-L95 generates from the csproj items above
   [StarMap]
   EntryAssembly = "ShaderExtensions"

   [[StarMap.ModDependencies]]
   ModId = "KittenExtensionsContinued"
   Optional = false
   ImportedAssemblies = [ "KittenExtensions" ]
   ```

   `Optional` defaults to `false`; `ImportedAssemblies` defaults to empty. KSArmory's design notes show the short
   inline form for a pack: `ModDependencies = [ { ModId = "KSArmory" } ]` under `[StarMap]`.[docs/EXTENSIBILITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/EXTENSIBILITY.md#L50-L90)
4. **Always list `ImportedAssemblies` explicitly.** With neither side listing anything, StarMap shares only the
   dependency's `EntryAssembly`, but only if the dependency was registered *before* you. If you were parked and then
   released, `CheckForDependentMods` records exactly your `ImportedAssemblies`, which is empty by default, so
   nothing would be shared: the name resolves only through your own `deps.json`, giving a second copy if you shipped
   the DLL and a load failure if you did not (read from the source, not tested in game). Naming the assembly makes both paths agree.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L126)
5. **Call into the dependency from the earliest hook that suffices.** A KSArmory pack registers from
   `StarMapBeforeMain`, with the entry class `[StarMapMod] ... [StarMapBeforeMain] public void OnLoad()` calling
   `Armoury.Register(File.ReadAllText(...), "MyWeaponPack")`; the public surface is `Schema`, `IsOpen` and
   `Register(string, string)`:[src/KSArmory/Sim/Armoury.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Armoury.cs#L20-L40)

   ```csharp
   // src/KSArmory/Sim/Armoury.cs L36-L40
       public static PackResult Register(string definitions, string source)
           => Catalogue.Register(
               PackReader.Read(definitions, source, Catalogue.Munitions, Catalogue.Sensors));
   ```

6. **List dependencies above dependents in `manifest.toml`** and avoid deep chains: the chain `C -> B -> A` listed as
   C, B, A can leave C reported as failed even though B loaded (a quirk of the single pass, read from source).
7. **Read the StarMap console.** `StarMap - Delaying load of mod: X due to missing dependencies: Y` and, if Y never
   arrives, `Failed to load mod:X after all mods were loaded, missing mods (some may be optional): ...`.[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L141)

## Complete example

| File | Role |
|---|---|
| [docs/EXTENSIBILITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/EXTENSIBILITY.md) | KSArmory's explanation and the pack entry point |
| [src/KSArmory/Sim/Armoury.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Armoury.cs) | The consumed surface |
| [ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj) | Reference plus dependency item for a build-generated manifest |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs) | `AllDependenciesLoaded`, `CalculateUseableAssemblies`, `CheckForDependentMods` |

## Variations and gotchas

- **`ModId` is the folder name.** The StarMap API README says it must match the dependency's `name`; the code
  compares against the manifest id only.[StarMap.API/README.md](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/README.md)
- **Missing or disabled dependencies park the mod** and, for a required one, never load it; a content-only mod (no
  DLL) can never satisfy a dependency because it never enters the registry.
- **"Present" means registered, not initialized.** If A (listed first) is parked on a missing optional dependency and
  B depends on A, B initializes first (read from source).
- **ShaderExtensions needs KittenExtensions Continued, not KittenExtensions**; the two cannot be installed together
  (README note).
- **Break the contract carefully.** KSArmory guards its pack surface with a CI-checked record, see
  [Text-only registration members with fault results, required dependency and a CI-checked API surface](../share-types-between-mods/share-types-between-mods-2.md).

## See also

- [Optional = true with vendored fallback copies of the contract](./declare-a-mod-dependency-2.md), [Late-bound type lookup with AccessTools.TypeByName (plus: Marker registered at immediate load, or type lookup, plus copy-once settings import)](./declare-a-mod-dependency-3.md), [Declare a dependency on another StarMap mod, required or optional](./overview.md)
- [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md), [A three-member, text-only registration API for code packs, with a generated contract record](../../../../mods/ksarmory/features/armoury-pack-registration-api.md),
  [mod.toml generated by MSBuild with a StarMap dependency](../../../../mods/shaderextensions/features/generated-mod-toml.md)
- [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md), [Write mod.toml - KSA keys and the [StarMap] table](../../loader/mod-toml-reference/overview.md), [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md)
