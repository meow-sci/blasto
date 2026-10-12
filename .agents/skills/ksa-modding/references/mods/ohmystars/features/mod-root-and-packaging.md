---
type: Mod Feature
title: Mod root and packaging
description: Resolve the mod folder from the running assembly location, ship data beside the DLL with an overridable game path and an explicit deploy target, and rely on StarMap default entry-assembly resolution
mod: ohmystars
category: getting-started
techniques: [starmap-lifecycle, msbuild-deploy]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.ModLibrary, KSA.Mod]
tags: [packaging, build, msbuild, mod-folder, starmap, licensing]
---

Oh My Stars is a code mod with a lot of data beside the DLL: 53 sky-culture folders, a 69 MB star
catalog, a boundary file, two ini files and third-party licence texts. Two small techniques make that
robust: the mod resolves its own folder from the running assembly's location instead of trusting the
default mods path, and the build copies an explicit list of files into the game's mods folder with
an overridable game path. The `mod.toml` has no `[StarMap]` section at all.[^oms-modpaths-cs] [^oms-ohmystars-csproj]

## How it works

1. **Minimal `mod.toml`.** Only `name="Oh My Stars"`, `version = "1.3.0"`, `author = "nanonestor"`
   ([packaging/mod.toml](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/packaging/mod.toml)).[^oms-packaging-mod-toml] With no `[StarMap]` table, StarMap
   falls back to `EntryAssembly = <manifest id>` and loads `<mod folder>/<id>.dll`, so the folder
   must be named `OhMyStars` to match `OhMyStars.dll`
   ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L60)).[^starmap-modrepository-runtimemod-cs]
   The game looks for the folder under `Content/` first and then under the Documents mods folder.
2. **Find the mod root.** `ModPaths.ModRoot` is
   `Path.GetDirectoryName(typeof(ModPaths).Assembly.Location)`, falling back to
   `ModLibrary.Find("OhMyStars").DirectoryPath` and finally the default Documents mods path
   ([src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs#L11-L53)). The comment explains why: an external loader may load the mod
   from an instance-specific folder, or a stale copy may sit in the default folder. Every data file
   is opened through `ModPaths.Combine`. The Star Editor uses the same root to find its own `Mod`
   entry by comparing full directory paths ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L488-L514)).
3. **Build inputs.** `net10.0`, unsafe code on, `Lib.Harmony` 2.4.2 and `StarMap.API` 0.3.6 from
   NuGet, `ModMenu.Attributes.dll` by `HintPath`, and game assemblies (`KSA`, `Brutal.*`,
   `BepuPhysics`, `BepuUtilities`, `Planet.Render.Core`) from `$(KSAGameDir)`
   ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L1-L15), [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L50-L124)).
4. **Overridable game path.** `KSAGameDir` defaults to `$(ProgramFiles)\Kitten Space Agency` only
   when unset, so an environment variable or the untracked, git-ignored
   `OhMyStars.csproj.user.props` (imported if it exists) can point elsewhere
   ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L10-L15), [.gitignore](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/.gitignore)).[^oms-gitignore]
5. **Deploy target.** `CopyCustomContent` runs after build and copies an explicit list to
   `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\OhMyStars`: `mod.toml`,
   `OhMyStars.deps.json`, `OhMyStars.dll`, `ModMenu.Attributes.dll`, `settings.ini` (always),
   `athyg_32_reduced_m10.csv`, `NOTICE.txt`, then `licenses/**`, `skycultures/**` and
   `lines_in_20.txt` through a `ModContent` item group with `TargetPath` metadata
   ([src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L25-L48)). Game DLLs referenced for compilation are not copied to
   the mods folder.
6. **Licensing layout.** `LICENSE` is GPL-2.0; `NOTICE.txt` lists the third-party parts: Stellarium
   sky cultures (GPL-2.0, trimmed to two files per culture), the AT-HYG catalog (CC BY-SA 4.0) and
   Pierre Barbier's boundary lines (CC BY-SA 4.0), with full texts under `licenses/`
   ([NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt)).[^oms-notice-txt]

## Lifecycle and timing

- **Loader time.** StarMap reads `mod.toml` and resolves the entry assembly while preparing the
  manifest, before KSA's `Program` exists, and runs `[StarMapBeforeMain]` methods right after
  registering the mod's attributes ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)).
- **`ModPaths.ModRoot`** is lazy and cached; the first call happens from `[StarMapAllModsLoaded]`
  when the renderers load their data, where `ModLibrary.Find` would also work
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L13-L26)).[^oms-ohmystarsentrypoint-cs]
- **Build time** for everything in the csproj.

## Key code

| Path | Role |
|---|---|
| [src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs#L11-L53) | Mod root from assembly location with fallbacks |
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L10-L48) | Game path override, content items, deploy target |
| [packaging/mod.toml](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/packaging/mod.toml) | Manifest without a `[StarMap]` table |
| [NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt) | Third-party data licences |

## Snippets

```csharp
// src/ModPaths.cs L29-L52
    private static string ResolveModRoot() {
        // Prefer the running assembly's own directory: the mod's assets are always
        // deployed next to OhMyStars.dll, and this stays correct even when an external
        // modloader loads the mod from a folder the game's ModLibrary doesn't know about
        // (or when a second, stale copy exists in the default mods folder).
        string? assemblyDirectory = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location);
        if(!string.IsNullOrWhiteSpace(assemblyDirectory)) {
            return assemblyDirectory;
        }

        // Fall back to the game's own mod registry (mod id == mod folder name).
        Mod? mod = ModLibrary.Find("OhMyStars");
        if(mod is not null && mod != Mod.Empty && !string.IsNullOrWhiteSpace(mod.DirectoryPath)) {
            return mod.DirectoryPath;
        }

        // Last resort: the default game mods folder under Documents.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "Kitten Space Agency",
            "mods",
            "OhMyStars");
    }
```

```xml
<!-- src/OhMyStars.csproj L10-L15, L25-L35 -->
  <!-- Path to the KSA install; override via KSAGameDir env var or a local, untracked .user.props file -->
  <PropertyGroup>
    <KSAGameDir Condition="'$(KSAGameDir)' == ''">$(ProgramFiles)\Kitten Space Agency</KSAGameDir>
  </PropertyGroup>

  <Import Project="OhMyStars.csproj.user.props" Condition="Exists('OhMyStars.csproj.user.props')" />
<!-- ... -->
  <ItemGroup>
    <ModContent Include="..\licenses\**\*">
      <TargetPath>licenses\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
    </ModContent>
    <ModContent Include="..\skycultures\**\*">
      <TargetPath>skycultures\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
    </ModContent>
    <ModContent Include="..\lines_in_20.txt">
      <TargetPath>lines_in_20.txt</TargetPath>
    </ModContent>
  </ItemGroup>
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModLibrary.Find(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187) | Fallback mod folder |
| `ModLibrary.LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178) | The Documents mods folder StarMap also searches |
| `Mod.Empty`, `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L24-L91) | Fallback guard and path |

## Pitfalls and version notes

- **Windows-only deploy.** `%USERPROFILE%` and backslash paths tie the copy step to Windows; the
  `ModsFolder` is not overridable the way `KSAGameDir` is.
- **README drift.** The README's install tree shows `LICENSE` and `hyg_v42.csv`; the deploy target
  ships neither ([README.md](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/README.md)).[^oms-readme-md]
- **Hard-coded fallbacks.** The last-resort path ignores StarMap's `-InstancePath` override of
  `Constants.DocumentsFolderPath`, though it is only reached if `Assembly.Location` is empty.
- **No version gate.** Neither `mod.toml` nor the code checks the game build.

## See also

- [settings.ini persistence](ini-settings-persistence.md)
- [ModMenu entry and game-styled windows](modmenu-entry-and-console-style-windows.md)
- [Save-scoped settings sidecar](save-scoped-settings-sidecar.md)
- [Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly](../../../howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-1.md) - how-to article demonstrating this feature
- [Add a plain post-build copy with an overridable KSAGameDir](../../../howto/getting-started/build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md) - how-to article demonstrating this feature
- [Ship data files and third-party licences with the mod](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-3.md) - how-to article demonstrating this feature
- [Write mod.toml: KSA keys and the StarMap table](../../../howto/getting-started/loader/mod-toml-reference/mod-toml-reference.md) - how-to article demonstrating this feature
- [Find your mod's install folder at runtime: Assembly.Location, ModLibrary.Find, Documents](../../../howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md) - how-to article demonstrating this feature
- [Keep settings beside the mod DLL, resolved from the loaded assembly](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-1.md) - how-to article demonstrating this feature

[^oms-modpaths-cs]: OhMyStars src/ModPaths.cs
[^oms-ohmystars-csproj]: OhMyStars src/OhMyStars.csproj
[^oms-packaging-mod-toml]: OhMyStars packaging/mod.toml
[^starmap-modrepository-runtimemod-cs]: StarMap StarMap.Core/ModRepository/RuntimeMod.cs
[^oms-gitignore]: OhMyStars .gitignore
[^oms-notice-txt]: OhMyStars NOTICE.txt
[^oms-ohmystarsentrypoint-cs]: OhMyStars src/OhMyStarsEntryPoint.cs
[^oms-readme-md]: OhMyStars README.md
