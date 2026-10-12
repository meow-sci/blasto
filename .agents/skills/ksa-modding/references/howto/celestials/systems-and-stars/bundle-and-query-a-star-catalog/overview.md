---
type: Game Feature
title: Bundle a star catalog and constellation data and join them to the game's sky
description: Ship a reduced AT-HYG CSV keyed by Hipparcos number, convert equatorial J2000 to the game's ecliptic directions, and optionally load Stellarium constellation files filtered to the stars present in the stock star binary
category: celestials
group: systems-and-stars
topic: bundle-and-query-a-star-catalog
demonstrated_by: [mods/ohmystars/features/hyg-star-catalog, mods/ohmystars/features/stellarium-sky-cultures]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.Camera, KSA.Vehicle, KSA.ModLibrary, KSA.Mod]
tags: [star-catalog, hyg, hipparcos, constellations, stellarium, sky-cultures]
---
The game's star field is a binary of anonymous positions: a direction, a scale and a colour per star, no name, no catalog number. To name stars, find them by constellation, Bayer designation or proper name, or point at them, a mod ships a catalog and joins it to the sky itself.

## Engine background

- The stock star binary is listed in a mod's `mod.toml` under `starBinaries` and read by `Mod.LoadStarBinaries`: an `int32` count then 16-byte records of three `float`s (direction, normalised on load), a `byte` scale and three colour bytes, each added to the star technique at distance 19.5 ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)). Core lists its star file in [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L90-L91). The file format is covered in [read and replace star binaries](../read-and-replace-star-binaries/overview.md).
- The game's sky frame is ecliptic (camera-relative ego axes are ecliptic axes): `Mod.LoadStarBinaries` passes `distance: 19.5` and the technique places each star at the normalised direction times that distance, a sphere of radius 19.5 around the camera ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83)). A catalog in equatorial J2000 coordinates therefore needs the obliquity rotation (about 23.439 degrees about X).
- The central star is a live body, not a catalog row: `Universe.WorldSun` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106)) is the nearest star to the camera.
- The stock binary contains only part of any catalog (about 99,000 stars in Core's file), so a join must keep only matches.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: header-indexed CSV parse into HIP-keyed ecliptic directions with synthetic keys](bundle-and-query-a-star-catalog-1.md) | ohmystars | `data-file-parsing` | `startup:all-mods-loaded`, `frame:draw-ui` | 330k rows parsed synchronously at load; needs a reduced file |
| [2: parse .fab and names files, filter to stars present in the stock binary](bundle-and-query-a-star-catalog-2.md) | ohmystars | `data-file-parsing` | `startup:all-mods-loaded`, `frame:draw-ui` | 53 cultures at no per-frame cost; exact float-bit match with the stock file |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Read and replace star binaries](../../../celestials/systems-and-stars/read-and-replace-star-binaries/overview.md), [Draw lines and labels on the sky](../../../rendering/overlays/draw-lines-on-the-sky/overview.md), [Build searchable lists and browsers](../../../ui/windows/build-searchable-lists-and-browsers/overview.md)
