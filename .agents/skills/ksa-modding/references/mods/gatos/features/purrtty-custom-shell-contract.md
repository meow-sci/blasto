---
type: Mod Feature
title: purrTTY custom shell contract
description: gatOS plugs a new shell type into another mod's terminal by importing that mod's exported contract assembly through an optional StarMap mod dependency, sharing one registry instance, while still loading cleanly when the other mod is absent
mod: gatos
category: external-io
techniques: [inter-mod-api, assembly-load-context, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: []
tags: [inter-mod, starmap, assembly-load-context, purrtty, terminal, mod-dependencies]
---

purrTTY is a separate mod that draws terminal windows in KSA. It publishes a tiny contract
assembly (`purrTTY.CustomShellContract`: `ICustomShell`, `CustomShellRegistry`, metadata, start
options, exceptions) so other mods can add entries to its "New Tab" menu. gatOS registers a
`gatos` shell whose sessions are SSH connections into its VM. This is the reference pattern for
inter-mod APIs on StarMap: one mod exports, the other imports through an optional dependency, and
both see the same `Type` identity and therefore the same static registry.

## How it works

**purrTTY exports** ([purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml)):

```toml
# purrTTY.GameMod/mod.toml L6-L12 (purrtty repo)
[StarMap]
EntryAssembly = "purrTTY.GameMod"
# The published inter-mod ABI: mods implementing ICustomShell resolve these from
# purrTTY's ALC so both sides share one CustomShellRegistry.Instance (same Assembly
# -> same Type identity -> same statics). purrTTY.Logging rides along because the
# contract assembly references it - an importer needs both from the same ALC.
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

**gatOS imports, optionally** ([gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml)): a `[[StarMap.ModDependencies]]` entry
with `ModId = "purrTTY"`, `Optional = true` and
`ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]`.

**How StarMap resolves it.**

- Load order: while initializing mods from the manifest, a mod whose dependency is not loaded yet
  is parked; it is initialized right after the dependency loads, or at the end of the pass if the
  missing dependencies are all optional ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L60-L132)).
- Usable assemblies: the code intends the intersection of the dependency's `ExportedAssemblies` and
  the importer's `ImportedAssemblies`, but at StarMap 4dc60f8 `RuntimeMod.ExportedAssemblies` is never
  filled from `mod.toml` (it stays the empty default), so the importer's `ImportedAssemblies` alone
  decides; with no import list only the dependency's `EntryAssembly` would be shared
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L108-L126)). Sharing the contract
  therefore works because gatOS lists both assemblies; purrTTY's `ExportedAssemblies` line has no effect.
- The importer's `ModAssemblyLoadContext.Load` asks each dependency's ALC for those names before
  falling back to its own `AssemblyDependencyResolver`
  ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L49-L71)). So with purrTTY
  installed, gatOS binds to purrTTY's loaded copy and `CustomShellRegistry.Instance` is the
  instance purrTTY's menus read.

**Fallback when purrTTY is absent.** The vendored contract DLLs (`vendor/purrTTY/`, pinned to a
purrTTY commit) are referenced with `<Private>true</Private>`, so they are copied into the mod
folder **and** listed in `gatOS.GameMod.deps.json`. The mod-local resolver only sees deps.json
entries, so without that the fallback load would fail; with it, gatOS registers into a registry
nobody consumes and the VM is still manageable from its menu
([gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L138-L154)).

**Registration** ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L989-L1023)): `CustomShellRegistry.Instance.RegisterShell("gatos", () => new SshShellSession(broker))`.
purrTTY's registry instantiates one probe instance to validate metadata and disposes it, so the
session constructor must be trivial ([purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs#L42-L70)).
gatOS then checks where the contract assembly was loaded from: if it is its own mod folder, purrTTY
is missing and it logs that the terminal UI is unavailable.

## Lifecycle and timing

- Registration runs in `[StarMapAllModsLoaded]` (`OnFullyLoaded`), after every mod's assemblies are
  loaded and purrTTY's registry exists; nothing boots at that point.
- Sessions are created later by purrTTY when the player opens a gatOS tab, and all their work is
  off the main thread (see [SSH shell sessions into the guest](ssh-shell-session-into-guest.md)).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml) | optional dependency and imported assemblies |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L989-L1023) | registration and own-copy detection |
| [gatOS.GameMod/gatOS.GameMod.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/gatOS.GameMod.csproj#L138-L154) | vendored contract references with `Private=true` |
| [vendor/purrTTY/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/vendor/purrTTY/README.md) | contract pin and refresh procedure |
| [purrTTY.CustomShellContract/ICustomShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/ICustomShell.cs) | the shell interface |

## Snippets

```toml
# gatOS.GameMod/mod.toml L1-L17
name = "gatOS"
description = "A real, minimal Alpine Linux running in a QEMU microVM, surfaced through purrTTY terminals, with live KSA vehicle telemetry exposed as a /sim filesystem."
version = "0.1.0"
author = "meow sci"

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

```csharp
// StarMap.Core/ModRepository/ModAssemblyLoadContext.cs L49-L71
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

            var foundPath = _modDependencyResolver.ResolveAssemblyToPath(assemblyName);
            if (foundPath is null)
                return null;

```

## Game APIs touched

None. This is loader-level (StarMap) and inter-mod plumbing.

## Pitfalls and version notes

- Export the logging assembly the contract references too, or the importer cannot load the contract
  from the same ALC.
- Treat vendored contract DLLs as a pinned ABI and refresh them deliberately; a mismatch with the
  installed purrTTY shows up as missing members at runtime.
- An exporting mod must keep the contract's constructor-time behavior cheap (probe instances).
- KSA render changes can break purrTTY independently; the gatOS notes record that an unpatched
  purrTTY crashed on 5154 in a way that looked like a gatOS crash.

## See also

- [SSH shell sessions into the guest](ssh-shell-session-into-guest.md)
- [Compile-gated game seam](compile-gated-game-seam.md)
- [Build, deploy and CI](build-deploy-and-ci.md)
- [gatOS overview](../overview.md)
- [SSH PTY session with lazy VM boot, bounded input writer thread and thread-agnostic output](../../../howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-4.md) - how-to article demonstrating this feature
- [Make a dependency optional and ship a vendored fallback copy of the contract](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-2.md) - how-to article demonstrating this feature
- [Share a contract assembly: the importer lists it in ImportedAssemblies](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-1.md) - how-to article demonstrating this feature

