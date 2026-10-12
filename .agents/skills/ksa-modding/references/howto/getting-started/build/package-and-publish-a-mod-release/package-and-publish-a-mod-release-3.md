---
type: How-To
title: Ship data files and third-party licences with the mod
description: Copy data folders and licence texts into the mod with TargetPath items, add a NOTICE for GPL and CC BY-SA components, and avoid a hand-committed release zip that goes stale
category: getting-started
group: build
topic: package-and-publish-a-mod-release
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/mod-root-and-packaging]
techniques: [msbuild]
hooks: [msbuild AfterTargets=AfterBuild]
lifecycle: []
game_types: []
tags: [packaging, licenses, data-files, notice, gpl, msbuild, ohmystars]
---

OhMyStars ships a 69 MB star catalog, 53 sky-culture folders, a boundary file and the licence texts of everything it
bundles beside its DLL. Two things make that robust: an explicit `ModContent` item list with `TargetPath` metadata that
mirrors folder structure into the mod folder, and a `NOTICE.txt` that states which licence covers which component. The
mod itself is GPL-2.0; the bundled data is GPL-2.0 (Stellarium sky cultures, trimmed) and CC BY-SA 4.0 (AT-HYG catalog,
Pierre Barbier's boundary lines). The mod finds those files at run time through its own assembly location
([Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md)). Related shapes: [Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md) (zip target) and
[Payload list verified file by file](./package-and-publish-a-mod-release-2.md) (verification).

## Background: how the game does it

KSA and StarMap load only `mod.toml`, the entry DLL and the content `mod.toml` lists; data a code mod reads is invisible
to both loaders, so it is entirely the mod's job to ship and locate. StarMap loads a mod's DLL from a path, so
`Assembly.Location` is the mod folder, and data beside it is found there.[src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs#L29-L52) Nothing in the game
checks licences; redistribution terms are yours to state.

## Lifecycle and timing

Build and package time for the copy; run time for reading the files (first from `StarMapAllModsLoaded`).

## Steps

1. **Declare content as items with a target path** that preserves structure:[src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj#L25-L35)

   ```xml
   <!-- OhMyStars/src/OhMyStars.csproj L25-L35 -->
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

2. **Copy them (and the explicit files) in the deploy target**, with `SkipUnchangedFiles` for big blobs; the same item list
   can feed a package target ([Explicit payload list shared by deploy and packaging](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-3.md)). `mod.toml`, the DLL, `deps.json`, `settings.ini`, the
   catalog CSV and `NOTICE.txt` are copied by name ([Post-build copy with an overridable game directory](../deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md)).
3. **Write a NOTICE** that lists every third-party component, its source and its licence, and keep the full texts under
   `licenses/`:[NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt)

   ```text
   # NOTICE.txt L1-L13
   This repository contains material under multiple licenses.
   See the sections below for license information applicable to each
   component.

   This mod contains original code and content created by David Krug. Unless otherwise noted, original code and content are licensed under
   the terms described in LICENSE.

   Third-party components:

   1. skycultures/

   The contents of the skycultures directory are derived from data obtained
   from Stellarium.
   ```

4. **Trim what you redistribute** to what the mod uses (OhMyStars keeps two files per sky culture) and say so in the NOTICE.
5. **Resolve the root at run time from the assembly directory**, not a hard-coded path, so every shipped file is found
   under instance paths and stale copies ([Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](../../loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md)).
6. **Generate the archive; never commit it.** Kitten-Engineer-Redux commits `dist/KittenEngineerRedux.zip` with no script
   that builds it: its DLL carries the build stamp `v0.5.2` while the source says `v0.5.7`, so it is stale. Build from
   source or produce the zip in CI.[KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj)

## Complete example

| File | Role |
|---|---|
| [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj) | `ModContent` items and the copy target |
| [NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt) | Multi-licence statement |
| [src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs) | Finding the shipped files at run time |
| [KittenEngineerRedux.csproj](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/KittenEngineerRedux.csproj) | The contrasting hand-committed zip (no script) |

## Variations and gotchas

- **README drift.** OhMyStars' README install tree lists `LICENSE` and `hyg_v42.csv`; the deploy target ships neither.
- **Windows-only copy.** `%USERPROFILE%` paths; `ModsFolder` is not overridable like `KSAGameDir`.
- **GPL and CC BY-SA obligations travel with the data**: keep the texts and attribution in the zip, not only in the repo.
- **Large files** belong behind `SkipUnchangedFiles` in dev deploys and should be named in the verification list
  ([Payload list verified file by file](./package-and-publish-a-mod-release-2.md)).
- **No version gate in the manifest.** Record the tested game build in the zip name or notes
  ([Warn when the running game build differs from the one a mod was tested on](../../lifecycle/check-the-game-version/overview.md)).

## See also

- [Release target that zips the deployed folder](./package-and-publish-a-mod-release-1.md), [Payload list verified file by file](./package-and-publish-a-mod-release-2.md), [Package and publish a mod release (zip, CI publishing, installer)](./overview.md)
- [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md)
- [Ship assets and code in one mod folder and find it at runtime](../../loader/ship-content-and-code-in-one-mod/overview.md), [Deploy a mod on every build and debug it from an IDE](../deploy-a-mod-on-every-build/overview.md)
