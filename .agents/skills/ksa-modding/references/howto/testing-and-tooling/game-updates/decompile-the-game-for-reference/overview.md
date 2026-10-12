---
type: Game Feature
title: Keep a searchable decompiled copy of the game next to your mod
description: Decompile the game's assemblies with ilspycmd into a gitignored folder stamped with the game version, or into a private repository where two builds produce a readable diff
category: testing-and-tooling
group: game-updates
topic: decompile-the-game-for-reference
demonstrated_by:
  - mods/compendium/features/decompiled-source-refresh
  - mods/ksarmory/features/api-surface-tracking
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksagolf/features/api-surface-and-assembly-lock
lifecycle: [event:game-update]
game_types: []
tags: [decompile, ilspy, ilspycmd, reference, game-update, diff]
---

KSA is pre-release and has no official modding API; the analysed mods all say so. The only authoritative description of an engine type is its compiled code, so every serious mod keeps a decompiled copy within reach: for the developer, for search tools, and for AI assistants that would otherwise guess.
Two setups appear. The simple one keeps a gitignored `decompiled/` folder in the mod repository with a version stamp. The heavier one keeps every build's decompile in a private repository so that a game update becomes a `git diff`.

## Engine background

| Piece | Where | Use |
|---|---|---|
| Decompiled layout | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) (example) | ILSpy writes `<Namespace>/<Type>.cs`; this corpus's game checkout follows it under `current/decomp` |
| Build record | [current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5) | Build number, date and revision range; the changelog commits can have gaps, so the decompile diff is the complete record |
| Assemblies | `current/dll` in the game checkout | What the decompiler reads; resolving cross-assembly types needs the whole set |

No lifecycle position applies: this is development tooling. Frame-order claims you derive from the decompiled code are checked against the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: Refresh-Decompiled.ps1 with a version stamp and assistant instructions](decompile-the-game-for-reference-1.md) | compendium ([tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1#L3-L35)) | `decompiling` | none | One command and no extra repository; a stale tree is only caught by a human or an assistant comparing the stamp |
| [2: decompile-assemblies.sh with one recommended ilspycmd version, for diffing builds](decompile-the-game-for-reference-2.md) | ksarmory ([tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/decompile-assemblies.sh#L17-L29)) | `decompiling` | `event:game-update` | A readable diff narrowed to your API surface; needs a private repository and discipline about the tool version and the assembly set |

Whichever you choose: keep decompiled game code out of public repositories, regenerate after every update, and keep the tool version and the set of assemblies fixed so a diff shows game changes and not decompiler churn.

## See also

- [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md), [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md)
- Mod features: [Compendium decompiled source refresh](../../../../mods/compendium/features/decompiled-source-refresh.md), [KSArmory API surface tracking](../../../../mods/ksarmory/features/api-surface-tracking.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md)
