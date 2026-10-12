---
type: Mod Feature
title: Inter-mod shell contract shared through StarMap assembly imports
description: Ship a small contract assembly that other mods resolve from purrTTY's AssemblyLoadContext by listing it in their mod.toml ImportedAssemblies, so both mods share one CustomShellRegistry.Instance, with a vendored fallback when purrTTY is absent
mod: purrtty
category: external-io
techniques: [inter-mod-api, assembly-load-context, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, mod.toml StarMap.ModDependencies.ImportedAssemblies]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: []
tags: [inter-mod, abi, assembly-load-context, starmap, mod-toml, contract, gatos]
---

purrTTY publishes a tiny, versioned API so other mods can add their own shells to its terminal
menus. gatOS uses it to expose an SSH session into its Linux VM as the "gatos" shell. The hard
part in KSA is type identity: every StarMap mod loads into its own `AssemblyLoadContext`, so two
mods that each load `purrTTY.CustomShellContract.dll` from their own folders would get two
different `CustomShellRegistry` types with two different static `Instance` fields, and gatOS's
registration would land in a registry purrTTY never reads. StarMap's assembly import mechanism
solves this: the consumer lists the contract in its own `ImportedAssemblies`, and StarMap resolves
it from purrTTY's context. purrTTY also declares the same names in `ExportedAssemblies`, but the
pinned StarMap never reads that list (see Pitfalls).

## How it works

1. **A separate contract assembly.** `purrTTY.CustomShellContract` holds only `ICustomShell`,
   `CustomShellRegistry`, metadata, start options, events, exceptions, base classes and
   `WellKnownShellEnvironment`. It depends on nothing game-specific except `purrTTY.Logging`
   (Brutal logging).[^contract-csproj]
2. **Provider declares exports** in its `mod.toml`:
   `[StarMap] ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]`.[^mod-toml]
   At StarMap 4dc60f8 this line has no effect: `StarMapConfig.ExportedAssemblies` is parsed, but
   nothing copies it into `RuntimeMod.ExportedAssemblies`, which stays empty.[^starmap-config][^starmap-runtime-mod]
3. **Consumer declares an optional dependency with imports** in its `mod.toml`. This is the list
   that takes effect:
   `[[StarMap.ModDependencies]] ModId = "purrTTY"`, `Optional = true`,
   `ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]`.[^gatos-mod-toml]
4. **StarMap resolves imports before local files.** `ModAssemblyLoadContext.Load` checks, in order:
   assemblies already in the default context, assemblies in the game context, then for each
   dependency mod whose usable-assembly list contains the name (with an empty export set that list
   is the importer's `ImportedAssemblies`),
   `dependency.ModAssemblyLoadContext.LoadFromAssemblyName(name)`, and only then the mod's own
   `AssemblyDependencyResolver`.[^starmap-mod-alc] So gatOS gets purrTTY's loaded `Assembly`
   object, the same `Type`, and the same static `CustomShellRegistry.Instance`.
5. **Registration.** gatOS calls `CustomShellRegistry.Instance.RegisterShell("gatos", () => new
   SshShellSession(broker))` from its `[StarMapAllModsLoaded]` handler.[^gatos-mod]
6. **Load-order independence.** purrTTY also initializes in `[StarMapAllModsLoaded]`, and the order
   between mods is not something either relies on: purrTTY's New Tab / New Window menus enumerate
   `GetAvailableShells()` live on every draw instead of caching a snapshot.[^menus]
7. **Fallback without purrTTY.** gatOS ships its own copies of both DLLs (`vendor/purrTTY/`,
   referenced with `Private=true` so they are in its output and `deps.json`). If purrTTY is not
   installed the optional dependency is absent, the local copies load, the registration lands in a
   private registry, and gatOS detects this by comparing the contract assembly's directory with its
   own mod folder and logs "purrTTY not detected".[^gatos-csproj][^gatos-vendor-readme]

## Lifecycle and timing

| Moment | What happens |
|---|---|
| StarMap mod discovery (`startup:starmap-before-main`: `StarMapCore.Init` runs before the game entry point) | `RuntimeMod.TryCreateMod` creates one `ModAssemblyLoadContext` per mod and loads its entry assembly. Each dependency is then recorded with its usable assembly list: by `AllDependenciesLoaded` (`CalculateUseableAssemblies`) when purrTTY registered first, or by purrTTY's `CheckForDependentMods` (`ImportedAssemblies` copied as-is) when gatOS registered first and was parked. Either way the list is gatOS's `ImportedAssemblies`, and it is in place before gatOS's mod class is constructed[^starmap-runtime-mod] |
| First use of a contract type in gatOS | `Load` resolves the name through purrTTY's context |
| `startup:all-mods-loaded` | Both mods' `StarMapAllModsLoaded` handlers run: purrTTY discovers its built-in shells, gatOS registers "gatos" |
| `frame:draw-ui` | purrTTY's menu reads the registry live; launching the shell calls the factory on a session-creation pool thread |

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) | `ExportedAssemblies` (declarative only at the pinned StarMap) |
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml) | Optional dependency with `ImportedAssemblies` (gatOS repo) |
| [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L73) | Resolution order: default, game, dependency mods, local (StarMap repo) |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L127) | `CalculateUseableAssemblies` (StarMap repo) |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L164-L182) | `CheckForDependentMods`: the late-registration path (StarMap repo) |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L989-L1023) | Registration and the "is this purrTTY's copy?" check (gatOS repo) |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L138-L153) | Vendored fallback references (gatOS repo) |
| [purrTTY.GameMod/TerminalMenus.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMenus.cs#L192-L218) | Live enumeration of other mods' shells |

## Snippets

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

gatOS's comment credits purrTTY's `ExportedAssemblies`; at the pinned StarMap it is this
`ImportedAssemblies` list alone that makes the two names resolve from purrTTY's context.

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L49-L66
            if (RuntimeMod is RuntimeMod modInfo && modInfo.Dependencies.Count > 0)
            {
                foreach (var (dependency, importedAssemblies) in modInfo.Dependencies)
                {
                    if (importedAssemblies.Contains(assemblyName.Name ?? string.Empty))
                    {
                        try
                        {
                            var asm = dependency.ModAssemblyLoadContext.LoadFromAssemblyName(assemblyName);
                            if (asm != null)
                                return asm;
                        }
                        catch (FileNotFoundException)
                        {
                        }
                    }
                }
            }
```

## Game APIs touched

None from KSA. The mechanism is StarMap's (`StarMapModDependency.ImportedAssemblies`,
`RuntimeMod.Dependencies`, `ModAssemblyLoadContext`); `StarMapConfig.ExportedAssemblies` is parsed
but unused at the pinned ref.[^starmap-config]

## Pitfalls and version notes

- **At the pinned StarMap ref the export list is never read.** `StarMapConfig.ExportedAssemblies`
  is parsed from `mod.toml`, but nothing copies it into `RuntimeMod.ExportedAssemblies`, so only
  the importer's `ImportedAssemblies` matters. If purrTTY registered first,
  `CalculateUseableAssemblies` sees an empty export set and returns `ImportedAssemblies` (or
  purrTTY's `EntryAssembly` when that list is empty); if gatOS registered first,
  `CheckForDependentMods` copies `ImportedAssemblies` exactly, so an empty list would share nothing.
  The sharing works because gatOS lists both assemblies. Keeping `ExportedAssemblies` documents the
  intended ABI and costs nothing; `CalculateUseableAssemblies` already contains the intersect branch
  a populated export list would take.[^starmap-runtime-mod]
- Treat the contract's public surface as a versioned ABI: gatOS compiles against a pinned copy and
  refreshes it deliberately (vendor README).
- `RegisterShell` probe-instantiates and disposes one instance to validate metadata, so a shell
  constructor must be trivial and `Dispose` must be safe on a never-started instance.
- Keep the contract free of display and game types; shell configuration travels as launch
  environment variables (`WellKnownShellEnvironment`).
- Both DLLs must come from the same context: the contract references `purrTTY.Logging`, and purrTTY
  exports it for that reason (comment in `mod.toml`).

## See also

- [Custom shell registry and lifecycle](./custom-shell-registry-and-lifecycle.md)
- [Brutal logging category](./brutal-logging-category.md)
- [Game console shell](./game-console-shell.md) (the built-in implementation)
- [Per-platform native library resolver](./per-platform-native-library-resolver.md) (the unmanaged side of the same ALC)
- [purrTTY overview](../overview.md)
- [Make a dependency optional and ship a vendored fallback copy of the contract](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-2.md) - how-to article demonstrating this feature
- [Share a contract assembly: the importer lists it in ImportedAssemblies](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-1.md) - how-to article demonstrating this feature

[^contract-csproj]: purrTTY.CustomShellContract/purrTTY.CustomShellContract.csproj
[^mod-toml]: purrTTY.GameMod/mod.toml
[^gatos-mod-toml]: gatOS.GameMod/mod.toml
[^starmap-mod-alc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^gatos-mod]: gatOS.GameMod/Mod.cs
[^menus]: purrTTY.GameMod/TerminalMenus.cs
[^gatos-csproj]: gatOS.GameMod/gatOS.GameMod.csproj
[^gatos-vendor-readme]: gatOS vendor/purrTTY/README.md
[^starmap-runtime-mod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-config]: StarMap.Core/Config/StarMapConfig.cs
