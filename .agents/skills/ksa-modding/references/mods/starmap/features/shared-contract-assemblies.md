---
type: Mod Feature
title: Share contract assemblies between mods with ExportedAssemblies and ImportedAssemblies
description: A dependent mod's load context resolves selected assembly names from its dependency's load context, so both mods see one Assembly, one Type identity and one set of statics for an inter-mod contract
mod: starmap
category: external-io
techniques: [inter-mod-api, assembly-load-context, toml-config]
hooks: [assembly-load-context ModAssemblyLoadContext.Load (dependency step)]
lifecycle: [startup:starmap-before-main]
game_types: []
tags: [starmap, inter-mod, contract, type-identity, assembly-load-context, mod-toml]
---

Because every mod lives in its own load context, two mods that each ship `Contract.dll` get two
different `Contract` assemblies, and an `ICustomShell` from one is not the `ICustomShell` the
other checks for. StarMap's answer is a per-dependency import set: when mod B declares a
dependency on mod A, B's load context asks A's load context for the assembly names in that set
before it looks in B's own folder. Both sides then hold the same `Assembly` object, so type
checks, casts and static fields line up. purrTTY and gatOS use exactly this to share a shell
registry.

## How it works

The import set for a dependency is computed by `CalculateUseableAssemblies` when the dependency is
already registered. The method reads A's exports from `RuntimeMod.ExportedAssemblies`, a property
that nothing in this checkout assigns (the `mod.toml` value is stored in
`RuntimeMod.Config.ExportedAssemblies` instead), so at the pinned commit the exporter side is
always empty:[^sm-runtimemod]

| B's `ImportedAssemblies` | A's `ExportedAssemblies` in `mod.toml` | Intended by the code | Effective at the pinned commit |
|---|---|---|---|
| empty | empty | A's `EntryAssembly` only | A's `EntryAssembly` only |
| set | empty | B's `ImportedAssemblies` | B's `ImportedAssemblies` |
| empty | set | A's `ExportedAssemblies` | A's `EntryAssembly` only |
| set | set | the intersection | B's `ImportedAssemblies` |

When the dependency registers after the dependent (the dependent was parked and is released by
`CheckForDependentMods`), the set is exactly B's `ImportedAssemblies`, and A's
`ExportedAssemblies` plays no part. An empty `ImportedAssemblies` then shares nothing at all, not
even A's entry assembly.

At run time `ModAssemblyLoadContext.Load` consults dependencies only after the `Default` context
and the game's context failed to supply the name, and before the mod's own `deps.json` resolver.
For each dependency whose set contains the requested simple name it calls
`dependency.ModAssemblyLoadContext.LoadFromAssemblyName(name)`, which returns A's already-loaded
copy or loads it from A's folder.[^sm-modalc]

purrTTY publishes its contract and the logging assembly the contract references:[^purrtty-modtoml]

```toml
# purrtty: purrTTY.GameMod/mod.toml L6-L12
[StarMap]
EntryAssembly = "purrTTY.GameMod"
# The published inter-mod ABI: mods implementing ICustomShell resolve these from
# purrTTY's ALC so both sides share one CustomShellRegistry.Instance (same Assembly
# -> same Type identity -> same statics). purrTTY.Logging rides along because the
# contract assembly references it - an importer needs both from the same ALC.
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

gatOS imports the same two names, optionally:[^gatos-modtoml]

```toml
# gatOS: gatOS.GameMod/mod.toml L6-L17
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

Because both lists name the same two assemblies, gatOS gets the same import set whichever of the
two mods comes first in the manifest. gatOS also references vendored copies of both DLLs with
`Private=true`, so they are in its output and its `deps.json`: with purrTTY present the
dependency step wins and the local copies never load; without purrTTY the local resolver finds
them and gatOS still loads.[^gatos-csproj]

## Lifecycle and timing

The import sets are fixed during `StarMapCore.Init` (`startup:starmap-before-main`). The actual
cross-context resolution happens lazily, the first time code in B needs a type from the shared
assembly, which can be in any hook or on any thread. Both mods must not have loaded their own
copy before then; in practice B's copy can only load through B's resolver, which runs after the
dependency step, so it never does when A is present.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L127) | `CalculateUseableAssemblies` |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L171-L172) | Late-registered dependency: import set is `ImportedAssemblies` as written |
| [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L49-L66) | Dependency step of `Load` |
| [purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) | purrTTY: exporter side |
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml) | gatOS: importer side |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L138-L154) | gatOS: vendored fallback copies of the contract |

## Snippets

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L109-L127
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
}
```

## Game APIs touched

None. This is loader-level assembly resolution.

## Pitfalls and version notes

The first two points are traced from the code at the pinned commit, not tested in game.

- **The exporter's list does not restrict or supply anything today.** Because
  `RuntimeMod.ExportedAssemblies` is never filled from `mod.toml`, the import set is B's
  `ImportedAssemblies`; when B lists nothing it is A's entry assembly if A was registered first,
  and empty if A registered after B. An importer that relies on
  purrTTY's `ExportedAssemblies` alone would resolve only `purrTTY.GameMod` from purrTTY and load
  the contract from its own folder, splitting type identity. Always list `ImportedAssemblies`
  explicitly on the importing side, as gatOS does, and keep `ExportedAssemblies` on the exporter
  as documentation and for a future fix.
- **README versus code.** The API README says that by default all of a mod's assemblies are
  accessible to other mods; in code, with neither list set, only the dependency's entry assembly is
  shared.[^sm-api-readme]
- **Ship transitive contract references together.** purrTTY exports `purrTTY.Logging` because the
  contract assembly references it; an importer that resolved one from purrTTY and the other locally
  would split identities.
- **Names are exact.** The import set is a case-sensitive `HashSet<string>` of simple assembly
  names (no `.dll`).
- **Game and loader assemblies never go through this path.** Anything resolvable from `Default`
  or the game's context is returned before dependencies are consulted.

## See also

- [Isolate each mod in its own AssemblyLoadContext](per-mod-assembly-load-context.md)
- [Order mod loading with a ModDependencies graph](mod-dependency-graph.md)
- [Share a contract assembly: the importer lists it in ImportedAssemblies](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-1.md) - how-to article demonstrating this feature

[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^sm-api-readme]: StarMap.API README
[^purrtty-modtoml]: purrtty purrTTY.GameMod/mod.toml
[^gatos-modtoml]: gatOS gatOS.GameMod/mod.toml
[^gatos-csproj]: gatOS gatOS.GameMod/gatOS.GameMod.csproj
