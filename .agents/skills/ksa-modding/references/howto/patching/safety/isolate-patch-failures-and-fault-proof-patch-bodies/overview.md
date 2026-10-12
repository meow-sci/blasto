---
type: Game Feature
title: Isolating patch failures and keeping patch bodies from throwing
description: How KSA mods localise damage when a game update breaks one Harmony target and how they write patch bodies that cannot take the game's frame loop down, from per-class application to fault counters
category: patching
group: safety
topic: isolate-patch-failures-and-fault-proof-patch-bodies
demonstrated_by:
  - mods/purrtty/features/failure-isolated-harmony-patching
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/unscience/features/unscience-consolidated-patcher
  - mods/unscience/features/lib-isubmod-contract
  - mods/ksagolf/features/degrading-harmony-patches
  - mods/ksagolf/features/dual-hook-frame-stepping
  - mods/realatmospheres/features/reflection-only-mod-build
lifecycle:
  - startup:all-mods-loaded
  - event:starmap-unload
  - frame:draw-ui
  - frame:celestials
  - frame:after-on-frame
  - startup:starmap-before-main
game_types:
  - KSA.Program
  - KSA.GameSettings
  - HarmonyLib.Harmony
  - KSA.Universe
  - KSA.SuperMeshRenderSystem
  - KSA.AtmosphereRenderer
tags: [harmony, isolation, fault-proof, createclassprocessor, tryapply, once-only-logging, patching]
---

`Harmony.PatchAll` stops at the first failure, and a prefix that throws every frame breaks the method it decorates. Two
separate problems follow: a patch set that fails as a whole when one target changes, and patch bodies that are allowed to
throw into the game's frame loop. KSA mods solve the first by applying patches per class or per feature, and the second by
catching everything in the body, logging once per failure kind, nulling the delegate the patch calls, degrading with a
recorded reason and, in the worst case, removing all patches after a fault limit.

## Engine background

| Type | Member | Why it matters here |
|---|---|---|
| `Program` | `OnKey`, private `DispatchKeyEvent`, `DrawProgramMenusHook`, private `OnFrameCelestials` | Targets that drift; each patched in its own class ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1830)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2319-L2323)) |
| `GameSettings` | `OnKeyAll` | Separate hotkey guard class ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435)[^ksa-gamesettings]) |
| `Universe` | `GetLastSimStep`, `ExecuteNextVehicleSolvers` | Shared seams applied once ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139)[^ksa-universe]) |
| `AtmosphereRenderer` | private `FillPlanetAtmosphereData` | Parameter names checked before patching ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136)[^ksa-atmosphererenderer]) |
| `HarmonyLib.Harmony` | `PatchAll`, `CreateClassProcessor`, `UnpatchAll(id)` | The mechanism being isolated |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): patches go in at `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`) or `[StarMapBeforeMain]`; patch bodies
run in `frame:draw-ui`, `frame:celestials`, `frame:after-on-frame` or on workers; `[StarMapUnload]` (`event:starmap-unload`) runs after
StarMap's id-less `UnpatchAll()`.

## Facts checked against Lib.Harmony 2.4.2

| Question | Observed |
|---|---|
| `PatchAll` with a throwing class | throws `HarmonyException`; classes apply in declaration order and stop at the first failure |
| `CreateClassProcessor(type).Patch()` for a missing target | throws `HarmonyException: Undefined target method ...` for that class only |
| A body throws in a prefix | the exception reaches the caller of the patched method (the game) |
| `UnpatchAll(id)` | removes only that owner's patches |

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [CreateClassProcessor per patch class, required versus optional](isolate-patch-failures-and-fault-proof-patch-bodies-1.md) | purrTTY, KSA-MeasureTools | `CreateClassProcessor` per class, required versus optional, once-only logging, typed handles | `startup:all-mods-loaded` | Simple; classes must be small |
| 2 | [Consolidated patcher with TryApply and ordered shared seams](isolate-patch-failures-and-fault-proof-patch-bodies-2.md) | unscience | One Harmony id, `TryApply`/`TryRemove`, ordered shared seams, idempotent helpers | `startup:all-mods-loaded` | Scales to thirty features; isolation is by `try`, not by owner |
| 3 | [Static patch classes that look up the target, log once and stand down](isolate-patch-failures-and-fault-proof-patch-bodies-3.md) | KSAGolf | Static patch classes that look up the target, log once and stand down | `startup:all-mods-loaded`, `frame:celestials` | Every class has a written consequence and a fallback |
| 4 | [Reflection-only mod with parameter-name and field-type pre-checks](isolate-patch-failures-and-fault-proof-patch-bodies-4.md) | Real Atmospheres | Reflection-only build with parameter-name and field-type pre-checks | `startup:starmap-before-main` | No compile-time safety; shape checks take the place of it |
| 5 | [Fault counter that pulls every patch after a limit](isolate-patch-failures-and-fault-proof-patch-bodies-5.md) | KSAGolf | Fault counter that pulls every patch after a limit | `frame:draw-ui`, `frame:after-on-frame` | Safe for the game; the mod is off for the session |

## See also

- [Refusing a patch and rolling back when the game changes](../refuse-a-patch-and-roll-back-when-the-game-changes/overview.md) for per-feature owners and rollback by id
- [Validating patch targets and detecting game-update breakage](../validate-patch-targets-and-detect-game-update-breakage/overview.md) for finding breakage before a player does
- [Removing Harmony patches safely at runtime and at exit](../remove-your-patches-safely-at-runtime-and-at-exit/overview.md) for removal
- [Coexisting with other mods that patch the same method](../../harmony/coexist-with-other-mods-patching-the-same-method/overview.md)
- Related topics: contain-exceptions-in-starmap-hooks, write-a-per-mod-log-file, draw-ui-while-the-hud-is-hidden, check-the-game-version, build-a-mod-without-game-references
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Keep mod exceptions out of KSA's frame loop](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md) - related topic
- [Log from a mod: per-mod log file, game log category and log-once](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md) - related topic
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md) - related topic
- [Warn when the running game build differs from the one a mod was tested on](../../../getting-started/lifecycle/check-the-game-version/overview.md) - related topic
- [Build a KSA mod that references only StarMap.API and Harmony](../../../getting-started/build/build-a-mod-without-game-references/overview.md) - related topic

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
