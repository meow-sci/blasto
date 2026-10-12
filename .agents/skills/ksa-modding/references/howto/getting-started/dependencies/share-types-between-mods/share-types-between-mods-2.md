---
type: How-To
title: Design a text-only registration API with fault results and a CI-checked surface record
description: Shape the surface other mods bind to so it survives refactors - text in, results out, a closing deadline, a schema constant - and diff a generated API-surface record in CI
category: getting-started
group: dependencies
topic: share-types-between-mods
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/armoury-pack-registration-api]
techniques: [inter-mod-api, toml-config]
hooks: [starmap StarMapBeforeMain, starmap StarMapAllModsLoaded]
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [inter-mod-api, registration, api-surface, ci, schema, ksarmory, weapon-packs]
---

A weapon pack for KSArmory is an ordinary StarMap mod that depends on KSArmory and calls one static method from its own
entry point. The whole API is `Armoury.Schema`, `Armoury.IsOpen` and `Armoury.Register(string definitions, string
source)`. The choices behind it are a template for any mod that wants others to extend it: **text in** instead of game
types, **a result out** instead of an exception, a **deadline**, a **schema** constant, and a **generated surface
record** that CI diffs, because a pack lives in another repository and never builds against your CI. The type-sharing
mechanics are in [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md); the dependency declaration is [Required dependency with ImportedAssemblies](../declare-a-mod-dependency/declare-a-mod-dependency-1.md).

## Background: how the game does it

StarMap provides the load-order and type-sharing half: a pack's `mod.toml` declares
`ModDependencies = [ { ModId = "KSArmory" } ]`; StarMap initializes the pack only after KSArmory (parking it when it comes first in the
manifest), so the pack's `StarMapBeforeMain` runs after KSArmory's (so the call lands after KSArmory's assembly is loaded and long before
KSArmory freezes the catalogue), and shares the dependency's `EntryAssembly` by default so both mods see one
`KSArmory.Armoury` type.[docs/EXTENSIBILITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/EXTENSIBILITY.md#L50-L90)[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L78-L126)[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104) (Read the caveat about parked
dependents and `ImportedAssemblies` in [Required dependency with ImportedAssemblies](../declare-a-mod-dependency/declare-a-mod-dependency-1.md).)

## Lifecycle and timing

| When | What | Why there |
|---|---|---|
| `startup:starmap-before-main` | The pack's hook calls `Armoury.Register` | Earliest code hook; StarMap has loaded KSArmory because of the dependency; registration is pure text parsing, no game state needed |
| `startup:all-mods-loaded` | KSArmory scans asset-only packs, calls `Catalogue.Freeze()`, then audits packs against loaded part templates | The first point where every code pack has run and part templates exist to audit against |
| Later | `Register` returns a "registered after the roster was built" fault | The roster crews systems from the frozen catalogue in flight |

## Steps

1. **Take text, not your types.** A pack that builds your profile types would need `double3` from
   `Brutal.Core.Numerics`, a game assembly that cannot be redistributed. `Register(string, string)` lets a pack compile
   against `KSArmory.dll` and `StarMap.API.dll` alone:[src/KSArmory/Sim/Armoury.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Armoury.cs#L20-L40)

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

2. **Return a result instead of throwing**, because the call runs inside another mod's StarMap hook, and also keep and
   log it so an ignored return value is still visible:[src/KSArmory/Sim/PackResult.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackResult.cs)

   ```csharp
   // src/KSArmory/Sim/PackResult.cs L9-L13
   public readonly record struct PackResult(string Source, int Registered, IReadOnlyList<PackFault> Faults)
   {
       /// <summary>True when everything the pack offered was taken.</summary>
       public bool Complete => Faults.Count == 0;
   }
   ```

3. **Close the door.** Registration is open from load until `[StarMapAllModsLoaded]`; afterwards `Register` returns a
   fault rather than mutating a live registry:[src/KSArmory/Sim/Catalogue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Catalogue.cs#L36-L80)

   ```csharp
   // src/KSArmory/Sim/Catalogue.cs L74-L79
           if (!_open)
           {
               faults.Add(new PackFault(pack.Source, "WeaponPack", "",
                                        "registered after the roster was built; nothing can be added now"));
               return Record(new PackResult(pack.Source, 0, faults));
           }
   ```

4. **Version the vocabulary with a schema constant** that a file states and the reader checks; a mismatch refuses the
   whole file with an actionable message:[src/KSArmory/Sim/PackReader.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackReader.cs#L58-L73)

   ```csharp
   // src/KSArmory/Sim/PackReader.cs L69-L74
           if (schema != Schema)
           {
               return Refused(source, faults,
                              $"written for schema {schema}; this build reads {Schema}. "
                              + (schema > Schema ? "Update KSArmory." : "Update the pack."));
           }
   ```

5. **Call it from the pack:** a dependent mod orders after the host, registers during its own startup hook, and
   receives a fault result if it registers too late (KSArmory's own design note shows the ten-line entry point
   using `[StarMapMod]` and `[StarMapBeforeMain]`).[docs/EXTENSIBILITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/EXTENSIBILITY.md#L96-L110)
6. **Generate a record of the surface and fail CI when it drifts.** `tools/pack-api.py` writes
   `docs/PACK-API-SURFACE.md` from source: the public members of `Armoury.cs`, `PackResult.cs`, `PackFault.cs`, plus
   every attribute literal the `Read*` methods of `PackReader` consume. `--check` fails when the committed file differs
   and the all-checks script runs it:[tools/pack-api.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/pack-api.py#L1-L30)

   ```python
   # tools/pack-api.py L2-L10 (module docstring)
   """Records the API a KSArmory weapon pack binds to.

       ./tools/pack-api.py            # regenerate docs/PACK-API-SURFACE.md
       ./tools/pack-api.py --check    # fail if the committed file is stale (for CI)

   The mirror of tools/api-surface.sh, pointed the other way. That one records what this mod
   depends on, because KSA moves underneath it. This records what depends on *this mod*, because
   nothing else can: a pack lives in somebody else's repository, never builds in CI, and its
   breakage surfaces as a bug report against a mod that used to work.
   ```

   ```bash
   # tools/check-all.sh L99
   run "Pack API surface"              ./tools/pack-api.py --check
   ```

   Committing the regenerated file is the acknowledgement of a change; for a deliberate break, bump `Schema` so old
   packs are refused whole.[docs/PACK-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/PACK-API-SURFACE.md#L1-L40)
7. **Make the record read like a checklist.** The generated file starts with the entry point members
   (`static int Schema { get; }`, `static bool IsOpen { get; }`, `static PackResult Register(string definitions, string
   source)`) and then the definition format, element by element with every attribute, so a diff shows exactly what a
   pack could notice.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/Armoury.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Armoury.cs), [src/KSArmory/Sim/PackResult.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackResult.cs) | The public surface |
| [src/KSArmory/Sim/Catalogue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Catalogue.cs), [src/KSArmory/Sim/PackReader.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PackReader.cs) | Deadline and schema |
| [tools/pack-api.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/pack-api.py), [tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh) | Surface generator and CI hook |
| [docs/PACK-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/PACK-API-SURFACE.md) | The committed record |
| [docs/EXTENSIBILITY.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/EXTENSIBILITY.md) | Design notes with the pack entry point |

## Variations and gotchas

- **Semantic versioning does not signal pack breaks here**: a rename inside the host's internals is a refactor and cuts
  no release, so the generated record, not the version, is the guard.
- **The XML vocabulary breaks packs without a compiler error.** Renaming an attribute compiles and passes tests but
  makes every pack's munitions refused by name; reading the literals out of the reader source is the point of the
  generator, and it fails closed (the record shrinks and `--check` fires).
- **Collision rule.** Rounds, sensors and optics merge first; a launcher is refused if any key it names was *refused* in
  this registration, since a refused name still resolves to another pack's profile.
- **Registration is static and shared across tests**, so tests register under unique names in one collection.
- **`source` is supplied by the pack**, never looked up; it namespaces keys (`MyPack:AIM-9X`) and attributes every
  diagnostic.

## See also

- [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md), [Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](./share-types-between-mods-3.md), [Share types and design an inter-mod API between StarMap mods](./overview.md)
- [A three-member, text-only registration API for code packs, with a generated contract record](../../../../mods/ksarmory/features/armoury-pack-registration-api.md)
- [Declare a dependency on another StarMap mod, required or optional](../declare-a-mod-dependency/overview.md), [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md)
