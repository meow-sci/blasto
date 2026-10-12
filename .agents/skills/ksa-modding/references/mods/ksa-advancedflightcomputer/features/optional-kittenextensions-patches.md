---
type: Mod Feature
title: Optional KittenExtensions XML patches from mod.toml
description: Declare XML patch files in mod.toml and KittenExtensions as an optional StarMap dependency, so data patches apply when it is installed and the code-side features degrade cleanly when it is not
mod: ksa-advancedflightcomputer
category: assets
techniques: [xml-patch, starmap-lifecycle, toml-config]
hooks:
  - harmony-prefix KSA.ModLibrary.PrepareAll (KittenExtensions)
  - harmony-transpiler KSA.Mod.LoadAssetBundles (KittenExtensions)
  - harmony-transpiler KSA.Mod.LoadPlanetMeshes (KittenExtensions)
  - harmony-transpiler KSA.Mod.LoadSystems (KittenExtensions)
  - harmony-transpiler KSA.Mod.PrepareSystems (KittenExtensions)
lifecycle: [startup:asset-load]
game_types: [KSA.ModLibrary, KSA.Mod, KSA.XmlLoader]
tags: [xml-patch, kittenextensions, mod-toml, optional-dependency, gauges, game-data]
---

AFC changes two pieces of game data: it adds an AUTOSTAGE button to the EngineControl gauge and a
mass and sphere of influence to the interstellar comets. Neither is possible from C# alone at the
right time, so AFC ships two KittenExtensions patch files and declares KittenExtensions as an
optional dependency. Everything else in the mod works without it.

## How it works

1. **Declare the files.** `mod.toml` lists `patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]`
   and an optional `[[StarMap.ModDependencies]] ModId = "KittenExtensions"`
   ([AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml)). The game itself does not read `patches`; KittenExtensions
   does, by parsing every mod's `mod.toml` with Tomlet into its own `ModToml` with a `patches` field
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L133-L190)).
2. **When they run.** KittenExtensions runs `XmlPatcher.OnPrepare` as a prefix on
   `ModLibrary.PrepareAll`: it loads every mod's system templates, assets and patch files into one
   document, executes each `<Patch>` in order, and then a transpiler on `Mod.LoadAssetBundles`,
   `LoadPlanetMeshes`, `LoadSystems` and `PrepareSystems` swaps `XmlLoader.Load<T>` for its own loader
   that deserializes from the patched document ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L60)).
3. **The operation.** `<Copy Path="XPath" Pos="Append">children</Copy>` appends the element's children
   to every node the XPath selects (`Replace`, `Append`, `Prepend`, `Before`, `After`; see the
   KittenExtensions README) ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L95-L116)). AFC uses it to append a `<Button>` to the
   `Buttons` component of `GaugeCanvas[@Id='EngineControl']` and `<Mass>`/`<SphereOfInfluence>` to
   `InterstellarComet` entries.
4. **Code that pairs with the data.** The gauge button only works because the matching enum is
   registered at immediate load (see [AUTOSTAGE gauge button](autostage-gauge-button.md)); the comet
   SOI only works with the Harmony patches in [hyperbolic transfer targets](hyperbolic-transfer-targets.md).
5. **Degrade without it.** With KittenExtensions absent nothing applies the XML: the comets keep no
   SOI and the target-list postfix skips them with a one-time log line; the AUTOSTAGE switch is still
   reachable on the Mods settings page. StarMap loads AFC anyway because the dependency is optional.

### Disabled mods still patch

KittenExtensions builds its patch domain from `ModLibrary.Manifest.Mods` without checking each entry's
`Enabled` flag ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L104-L111)). A mod that is installed but
disabled therefore still applies its XML patches. This is why AFC's README tells players to delete
the folders of the standalone AutoStage, AutoRemoveFinishedBurns and PoweredGuidance mods instead of
disabling them.

## Lifecycle and timing

`startup:asset-load`: the patch document is built and executed in the `ModLibrary.PrepareAll` prefix,
before any mod's `PrepareSystems` and before `ModLibrary.LoadAll` deserializes gauges and systems.
A patch can only add or change data; anything that must exist in code when the data is bound (such as
an enum type looked up by name) has to be registered by `[StarMapImmediateLoad]` at the latest.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml) | `patches` list and optional dependency |
| [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml) | Gauge button patch |
| [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml) | Comet mass and SOI patch |
| [AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj) | `PatchFile` items copied into the release package |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs) | KittenExtensions: reads `patches`, runs the patch document |
| [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs) | KittenExtensions: the Harmony hooks that run and consume the patches |

## Snippets

```toml
# AdvancedFlightComputer/mod.toml L1-L9
name = "AdvancedFlightComputer"
patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

[StarMap]
EntryAssembly = "AdvancedFlightComputer"

[[StarMap.ModDependencies]]
ModId = "KittenExtensions"
Optional = true
```

## Game APIs touched

| Member | Use |
|---|---|
| `ModLibrary.PrepareAll` / `LoadAll`, `ModLibrary.Manifest` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs)) | Load phases KittenExtensions hooks |
| `Mod.PrepareSystems` and the other loaders ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs)) | Call sites of `XmlLoader.Load<T>` that KittenExtensions redirects |
| `Core/Gauges.xml` EngineControl canvas ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765)) | Target of the button patch |

## Pitfalls and version notes

- XML patch paths are XPath against KittenExtensions' combined `<Root>`; `//GaugeCanvas[@Id='...']`
  matches across all mods.
- AFC's README links KittenExtensions at `tsholmes/KittenExtensions` and tests v0.4.0; the copy
  analyzed here is the `AMPW-german/KittenExtensions` checkout pinned in this catalog.
- A failing patch makes KittenExtensions show an error popup and exit the game, so keep patches
  minimal and targeted.

## See also

- [AUTOSTAGE gauge button](autostage-gauge-button.md)
- [Hyperbolic transfer targets](hyperbolic-transfer-targets.md)
- [Mod deploy MSBuild targets](mod-deploy-msbuild-targets.md) (how the patch files are packaged)
- [Ship XML patches that apply only when KittenExtensions is installed](../../../howto/assets/xml-patching/patch-game-xml-data/patch-game-xml-data-2.md) - how-to article demonstrating this feature
- [Probe for another mod at AllModsLoaded and stand down when a conflicting one is present](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-3.md) - how-to article demonstrating this feature

