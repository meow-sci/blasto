---
type: Game Feature
title: List exactly which game APIs your mod binds to, and pin Harmony targets at compile time
description: Read the TypeRef and MemberRef tables of your built DLL into a committed document that CI checks, and turn reflected Harmony targets into compile-time dependencies with never-called pin methods
category: testing-and-tooling
group: game-updates
topic: track-the-game-api-your-mod-uses
demonstrated_by:
  - mods/ksarmory/features/api-surface-tracking
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksagolf/features/api-surface-and-assembly-lock
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Vehicle, KSA.Program, KSA.SunbloomRenderer, KSA.PartModelRenderer]
tags: [game-update, api-surface, metadata, harmony, pin, ci]
---

KSA's API moves between builds and a mod cannot tell which movements matter without a list of what it uses. The mods analysed here generate that list from their own compiled assembly: the TypeRef and MemberRef metadata tables record exactly which external types and members the compiler bound, so the list cannot drift from the code.
The list is committed, checked in CI, and intersected with a diff of the decompiled game after an update, so a few dozen files are read instead of thousands. Harmony patch targets found by reflection leave no reference, so each is forced into the list by a private method that is never called but invokes the target.

## Engine background

| Piece | Where | Use |
|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396) | A public virtual patched by a prefix; pinnable |
| `Program.OnGameLoaded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599) | A public static patched by a postfix to see a save load; pinnable |
| `SunbloomRenderer.Render` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) | A public instance method patched to inject a pass; pinned through a null receiver |
| `PartModelRenderer.UpdateRenderData` | [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807) | A public static used for body draw hooks |
| `Program.OnFrameCelestials` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701) | Private; cannot be pinned, resolved by name with a logged fallback |
| Frame order | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | Where each patched method runs |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [tools/apisurface extractor and docs/KSA-API-SURFACE.md checked in CI](track-the-game-api-your-mod-uses.md) | ksarmory ([tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apisurface/Program.cs#L40-L114)) | `metadata-analysis` | `startup:all-mods-loaded` | Exact and cheap; blind to `nameof`, reflection by string, private targets and behaviour changes behind unchanged signatures |

## See also

- [Keep a searchable decompiled copy of the game next to your mod](../decompile-the-game-for-reference/overview.md), [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md), [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md)
- [Test Harmony patches without running the game](../../offline-tests/test-harmony-patches-without-the-game/overview.md), [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Mod features: [KSArmory API surface tracking](../../../../mods/ksarmory/features/api-surface-tracking.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md)
