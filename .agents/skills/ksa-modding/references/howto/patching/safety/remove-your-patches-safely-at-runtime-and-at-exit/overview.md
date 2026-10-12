---
type: Game Feature
title: Removing Harmony patches safely at runtime and at exit
description: How to remove only your own Harmony patches, from a main-thread hook, after nulling the delegates they call, and why StarMap's shutdown order means unload code usually finds nothing left to remove
category: patching
group: safety
topic: remove-your-patches-safely-at-runtime-and-at-exit
demonstrated_by:
  - mods/ksagolf/features/degrading-harmony-patches
  - mods/ksaero/features/patch-refusal-and-rollback
  - mods/gatos/features/scale-and-force-render-cheats
  - mods/unscience/features/sphinx-static-colliders
  - mods/unscience/features/unscience-consolidated-patcher
lifecycle: [event:starmap-unload, frame:after-on-frame, frame:draw-ui]
game_types: [HarmonyLib.Harmony, KSA.Program]
tags: [harmony, unpatch, shutdown, starmap, remove, shared-instance]
---

`UnpatchAll()` with no id removes every mod's patches. Remove yours by id, or by patch method when the Harmony instance is
shared, from a main-thread hook, after nulling the delegates the patches call. The shutdown fact that surprises most
authors: `StarMapCore.DeInit` runs an id-less `UnpatchAll()` before it invokes any `[StarMapUnload]` method, so unload code usually
finds nothing to remove. Removal during play (a fault limit, a toggle, a feature switched off) is the case that still matters.

## Engine and loader background

| Piece | What it does |
|---|---|
| `GameSurveyer.RunGame` and `Dispose` | The game's entry point runs inside StarMap's `Main`; `Dispose` runs after it returns ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L51-L61)[^starmap-gamesurveyer]) |
| `StarMapCore.DeInit` | `_harmony.UnpatchAll()` with no id, then `_loader.Dispose()` ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)[^starmap-starmapcore]) |
| `ModLoader.Dispose` | Invokes every `[StarMapUnload]` method ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)[^starmap-modloader]) |
| `Program.OnFrame` hooks | `[StarMapAfterOnFrame]` and `[StarMapAfterGui]` are the main-thread places to remove during play ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2276)[^ksa-program]) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): unload is `event:starmap-unload` at process exit; removal during play runs in `frame:after-on-frame` or
`frame:draw-ui`.

## Facts checked against Lib.Harmony 2.4.2

| Call | Result in a local test |
|---|---|
| `UnpatchAll("mod.a")` | removes only `mod.a`'s patches |
| `UnpatchAll()` with no id (any instance) | removes every owner's patches |
| `Harmony.GetAllPatchedMethods()` plus `GetPatchInfo` plus `Unpatch(original, patchMethod)` | removes one named patch method and leaves others, including others under the same id |

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [UnpatchAll(ownId) from a main-thread hook after nulling delegates](remove-your-patches-safely-at-runtime-and-at-exit-1.md) | KSAGolf, KSAero | `UnpatchAll(ownId)` from a main-thread hook after nulling delegates | `event:starmap-unload`, `frame:after-on-frame` | Simple; needs one id per class or feature |
| 2 | [Lazy install and removal of one patch while it is in use](remove-your-patches-safely-at-runtime-and-at-exit-2.md) | gatOS | Lazy install on first use, removal on last, in the frame drain | `frame:render` | Zero cost by default; install and removal logic to maintain |
| 3 | [Unpatch by patch method on a shared Harmony instance](remove-your-patches-safely-at-runtime-and-at-exit-3.md) | unscience | Unpatch by patch method on a shared instance | `event:starmap-unload`, `physics:bepu` | Safe on a shared id; walks every patched method |

## See also

- [Refusing a patch and rolling back when the game changes](../refuse-a-patch-and-roll-back-when-the-game-changes/overview.md) for rollback by id after a failed apply
- [Isolating patch failures and keeping patch bodies from throwing](../isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) for the fault-limit removal path
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) for the install half of approach 2
- Related topics: clean-up-on-game-exit
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md) - related topic

[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
[^starmap-starmapcore]: StarMap/StarMap.Core/StarMapCore.cs
[^starmap-modloader]: StarMap/StarMap.Core/ModRepository/ModLoader.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
