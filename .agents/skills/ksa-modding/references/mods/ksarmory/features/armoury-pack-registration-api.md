---
type: Mod Feature
title: A three-member, text-only registration API for code packs, with a generated contract record
description: KSArmory exposes Armoury.Register(string, string) so a dependent StarMap mod can add weapons from its entry point using only KSArmory.dll, returns faults instead of throwing, closes the door at StarMapAllModsLoaded, and guards the contract with a generated surface file that CI diffs
mod: ksarmory
category: external-io
techniques: [inter-mod-api, assembly-load-context, starmap-lifecycle, api-surface-record, result-not-exception]
hooks: [starmap StarMapBeforeMain (in the pack), starmap StarMapAllModsLoaded]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: []
tags: [inter-mod-api, starmap, mod-dependencies, assembly-load-context, api-stability, weapon-packs]
---

A weapon pack that has to compute its definitions (rather than ship them as files) is a normal
StarMap mod that depends on KSArmory and calls one static method from its own entry point.
`KSArmory.Armoury` is the entire code surface: `Schema`, `IsOpen` and
`Register(string definitions, string source)`.[^armoury] Two choices make it a good template
for any mod that wants other mods to extend it:

- **It takes text, not KSArmory or KSA types.** Building a `LauncherProfile` would need `double3`
  from `Brutal.Core.Numerics`, a game assembly that cannot be redistributed; a string-based call
  lets a pack compile against `KSArmory.dll` and `StarMap.API.dll` alone.[^armoury][^extensibility-doc]
- **It returns a result instead of throwing**, because it is called from inside another mod's
  StarMap hook; KSArmory also keeps and logs every result so an ignored return value is still
  visible.[^pack-result][^entry]

## How it works

| Piece | Behaviour |
|---|---|
| `Armoury.Register(text, source)` | `Catalogue.Register(PackReader.Read(text, source, Catalogue.Munitions, Catalogue.Sensors))`; the same path the folder scan uses, so code packs and asset-only packs are validated identically[^armoury][^catalogue] |
| `source` | Supplied by the pack and never looked up; it namespaces the pack's keys (`MyPack:AIM-9X`) and attributes every diagnostic[^armoury][^weapon-packs-doc] |
| `PackResult(Source, Registered, Faults)` / `PackFault(Source, Element, Name, Reason)` | Plain record structs; `Complete` is `Faults.Count == 0`; `PackFault.ToString()` is the log line[^pack-result][^pack-fault] |
| `Armoury.IsOpen` / `Catalogue.Freeze()` | Open from load until KSArmory's `[StarMapAllModsLoaded]`; a later `Register` returns a fault ("registered after the roster was built") rather than mutating a live registry[^catalogue][^entry] |
| `Armoury.Schema` | `PackReader.Schema` (`1`); a file declaring another schema is refused whole, with "Update KSArmory" or "Update the pack"[^armoury] |
| Collision rule | Rounds, sensors and optics merge first; a launcher is refused if any key it names was **refused** in this registration (taken by someone else), not merely if it fails to resolve, because a refused name still resolves to another pack's profile[^catalogue][^extensibility-doc] |

### What StarMap provides

A pack's `mod.toml` declares `ModDependencies = [ { ModId = "KSArmory" } ]` under `[StarMap]`.[^weapon-packs-doc]
In StarMap at the pinned ref:

- `RuntimeMod.AllDependenciesLoaded` parks a mod whose dependency is not yet loaded; `ModLoader`
  logs "Delaying load of mod" and `CheckForDependentMods` initialises it right after the
  dependency loads.[^starmap-runtimemod][^starmap-modloader]
- `InitializeMod` invokes the mod's `[StarMapBeforeMain]` method immediately, so a pack's call
  lands after KSArmory's assembly is loaded and long before KSArmory freezes the catalogue.[^starmap-runtimemod]
- `CalculateUseableAssemblies` shares the dependency's `EntryAssembly` (`KSArmory`, per its
  `mod.toml`) when neither side lists `ExportedAssemblies`/`ImportedAssemblies`, and
  `ModAssemblyLoadContext.Load` resolves those names out of the dependency's load context, so both
  mods see one `KSArmory.Armoury` type.[^starmap-runtimemod][^starmap-alc][^mod-toml]

### Guarding the contract

`tools/pack-api.py` regenerates `docs/PACK-API-SURFACE.md` from the source: the public members of
`Armoury.cs`, `PackResult.cs` and `PackFault.cs`, plus every attribute literal each `Read*` method
of `PackReader` consumes (with its kind and default). `--check` fails when the committed file
differs, and `tools/check-all.sh` runs it, so renaming an XML attribute (which compiles and passes
every test but breaks every pack in the wild) fails CI until the record is regenerated and, for a
real break, `Schema` is bumped.[^pack-api-py][^check-all][^pack-api-doc]

## Lifecycle and timing

| When | What | Why there |
|---|---|---|
| `startup:starmap-before-main` | The pack's `[StarMapBeforeMain]` calls `Armoury.Register` | Earliest code hook; StarMap has already loaded KSArmory because of the dependency; no game state is needed because registration is pure text parsing[^starmap-runtimemod] |
| `startup:all-mods-loaded` | KSArmory scans asset-only packs, then `Catalogue.Freeze()`, then the part audit (KSArmoryMod.cs L158-L167) | StarMap fires it from a postfix on `ModLibrary.LoadAll`, the first point where every code pack has run and part templates exist to audit against[^entry][^starmap-modlibrary-patches] |
| Later | `Register` returns a "registered after the roster was built" fault | The roster crews systems from the frozen catalogue in flight[^catalogue] |

`[StarMapImmediateLoad]` is also in time (the `IsOpen` doc comment says so), but it runs per mod
inside `Mod.PrepareSystems`, so ordering against KSArmory's own folder scan is not a concern
either way: the freeze is the only deadline.[^armoury]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/Armoury.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Armoury.cs#L20-L40) | The whole public surface |
| [src/KSArmory/Sim/Catalogue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Catalogue.cs#L57-L134) | Freeze, register, collision rule |
| [src/KSArmory/Sim/PackResult.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackResult.cs) / [src/KSArmory/Sim/PackFault.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackFault.cs) | Result types |
| [tools/pack-api.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/pack-api.py) | Generates and checks the contract record |
| [docs/PACK-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/PACK-API-SURFACE.md) | The generated record (8 elements, 134 attributes at this ref) |
| [tests/KSArmory.Tests/ArmouryTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ArmouryTests.cs) | Registration tests against the shared static catalogue |

## Snippets

```csharp
// src/KSArmory/Sim/Armoury.cs L20-L40
public static class Armoury
{
    /// <summary>The definition schema this build reads. A pack states it in its file.</summary>
    public static int Schema => PackReader.Schema;

    /// <summary>
    /// Whether there is still time to register. Open from load until the roster is built; a pack
    /// calling from <c>[StarMapBeforeMain]</c> or <c>[StarMapImmediateLoad]</c> is always in time.
    /// </summary>
    public static bool IsOpen => Catalogue.IsOpen;

    /// <summary>
    /// Read a pack's definitions and register what survives.
    /// </summary>
    /// <param name="definitions">The contents of a weapon-definition file.</param>
    /// <param name="source">What the pack calls itself. It supplies this; nothing looks it up,
    /// and it is what keeps two packs' identically named rounds apart.</param>
    public static PackResult Register(string definitions, string source)
        => Catalogue.Register(
            PackReader.Read(definitions, source, Catalogue.Munitions, Catalogue.Sensors));
}
```

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L109-L126
        private static IEnumerable<string> CalculateUseableAssemblies(RuntimeMod dependency, StarMapModDependency dependencyInfo)
        {

            var hasImportedAssemblies = dependencyInfo.ImportedAssemblies.Count > 0;
            var hasExportedAssemblies = dependency.ExportedAssemblies.Count > 0;

            if (!hasImportedAssemblies && !hasExportedAssemblies)
            {
                return [dependency.Config.EntryAssembly];
            }

            if (hasImportedAssemblies && !hasExportedAssemblies)
                return dependencyInfo.ImportedAssemblies;

            if (!hasImportedAssemblies && hasExportedAssemblies)
                return dependency.ExportedAssemblies;

            return dependency.ExportedAssemblies.Intersect(dependencyInfo.ImportedAssemblies);
```

## Game APIs touched

None directly: the API is pure C# over the mod's own registry. The engine-side half (when
`StarMapAllModsLoaded` fires) is the StarMap postfix on `ModLibrary.LoadAll`.[^starmap-modlibrary-patches]

## Pitfalls and version notes

- **Load order inside StarMap is not symmetric (read from source, not tested in game).** When the
  pack is processed *after* KSArmory, `AllDependenciesLoaded` records the shared assemblies via
  `CalculateUseableAssemblies` (EntryAssembly by default). When the pack is parked and released by
  `CheckForDependentMods`, StarMap records `dependencyInfo.ImportedAssemblies` verbatim
  (RuntimeMod.cs L172), which is empty by default, so `ModAssemblyLoadContext.Load` would not
  find `KSArmory` among the dependency's shared names. Declaring
  `ImportedAssemblies = ["KSArmory"]` in the dependency entry makes both paths agree.[^starmap-runtimemod][^starmap-alc][^starmap-config]
- **Bump `Schema` for a deliberate break**, so old packs are refused whole and loudly rather than
  losing attributes one at a time; `pack-api.py --check` prints that reminder.[^pack-api-py]
- The catalogue is static and shared across tests, so `ArmouryTests` registers under unique names
  and runs in one xUnit collection.[^armoury-tests]
- Semantic versioning does not signal pack breaks here: a rename inside `Sim/` is a refactor and
  cuts no release, which is why the generated record, not the version, is the guard.[^pack-api-py]

## See also

- [Weapon packs from mod folders](./weapon-packs-from-mod-folders.md)
- [Weapon registry and profiles](./weapon-registry-and-profiles.md)
- [API surface tracking](./api-surface-tracking.md)
- [Repo static checks](./repo-static-checks.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Declare a required dependency with ImportedAssemblies](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-1.md) - how-to article demonstrating this feature
- [Share a contract assembly: the importer lists it in ImportedAssemblies](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-1.md) - how-to article demonstrating this feature
- [Design a text-only registration API with fault results and a CI-checked surface record](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-2.md) - how-to article demonstrating this feature

[^armoury]: src/KSArmory/Sim/Armoury.cs
[^pack-result]: src/KSArmory/Sim/PackResult.cs
[^pack-fault]: src/KSArmory/Sim/PackFault.cs
[^catalogue]: src/KSArmory/Sim/Catalogue.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^pack-api-py]: tools/pack-api.py
[^check-all]: tools/check-all.sh
[^pack-api-doc]: docs/PACK-API-SURFACE.md
[^extensibility-doc]: docs/EXTENSIBILITY.md
[^weapon-packs-doc]: docs/WEAPON-PACKS.md
[^armoury-tests]: tests/KSArmory.Tests/ArmouryTests.cs
[^mod-toml]: src/KSArmory/mod.toml
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-alc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^starmap-config]: StarMap.Core/Config/StarMapConfig.cs
[^starmap-modlibrary-patches]: StarMap.Core/Patches/ModLibraryPatches.cs
