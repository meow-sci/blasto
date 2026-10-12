---
type: Game Feature
title: Build a KSA mod that references only StarMap.API and Harmony
description: Four ways to limit how much of the game a mod's build and code depend on - pure reflection, a compile-gated Game half, a provider library and a per-frame census
category: getting-started
group: build
topic: build-a-mod-without-game-references
demonstrated_by:
  - mods/realatmospheres/features/reflection-only-mod-build
  - mods/gatos/features/compile-gated-game-seam
  - mods/unscience/features/lib-game-state-providers
  - mods/ksacars/features/frame-step-latch-and-car-registry
lifecycle: [startup:starmap-before-main, frame:draw-ui]
game_types: [KSA.Program, KSA.ModLibrary, KSA.Universe, KSA.Vehicle, KSA.CelestialSystem]
tags: [reflection, compile-gating, providers, resilience, ci, game-references]
---

Referencing `KSA.dll` gives a mod compile-time safety and a hard dependency on the game's current shape and on having
the game installed to build. This topic collects the techniques that trade some of that away: no reference at all
(reflection), references confined to a folder that compiles only when the game is there, references confined to a
provider library, and a per-frame census that avoids a buffer that is empty at the wrong time.

## Engine background

| Game member | Where it matters | Source |
|---|---|---|
| `Universe.CurrentSystem` and `CelestialSystem.All` | the stable source of every vehicle in the system, from any hook | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2402) (`GetLastSimStep`, `GetElapsedTime`) |
| `Program.ControlledVehicle` | the player-flown vehicle | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2290) |
| `Program.VehiclesInFrame` | filled in `PrepareFrame` (`RefreshVehiclesInFrame`, L582), cleared in `PostRender`, so empty after render | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L582-L589) |

Timing: reflection-only installs happen at `startup:starmap-before-main`; provider and census reads happen at
`frame:draw-ui` or `frame:after-on-frame` ([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Reflection-only mod with visible breakage](./build-a-mod-without-game-references-1.md) | RealAtmospheres | `reflection` | `startup:starmap-before-main` | Builds with no game and survives game changes piece by piece; no compiler help, boxing costs, heavy discipline |
| [Game-free half and compile-gated Game half](./build-a-mod-without-game-references-2.md) | gatOS | `msbuild` | `startup:all-mods-loaded`, `frame:draw-ui` | Typed access where the game is present, everything else builds and tests anywhere; a layering rule to uphold |
| [Static provider library](./build-a-mod-without-game-references-3.md) | unscience | `reflection` | `frame:draw-ui` | A game change breaks one file; still references KSA, main-thread only |
| [Per-frame census from Universe.CurrentSystem.All](./build-a-mod-without-game-references-4.md) | KSACars | `starmap-lifecycle` | `frame:draw-ui`, `frame:after-on-frame` | Correct from either hook; needs a latch and registry upkeep |

## Choosing

- Need CI without the game and tests for a large core: approach 2 ([Compile-gated game seam](../build-in-ci-without-the-game-installed/build-in-ci-without-the-game-installed-3.md)).
- Small patch mod that must survive many builds: approach 1.
- Many features reading the same state: approach 3.
- Acting on specific craft each frame: approach 4.

## See also

- [Build a mod in CI when the game DLLs are private](../build-in-ci-without-the-game-installed/overview.md), [Reference the game's assemblies from a mod project without shipping them](../reference-game-assemblies-from-a-mod-project/overview.md),
  [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md); the topic `isolate-patch-failures-and-fault-proof-patch-bodies` covers patch bodies
- [Reflection-only mod build](../../../../mods/realatmospheres/features/reflection-only-mod-build.md), [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md),
  [Thin static providers that funnel game-state access through one library](../../../../mods/unscience/features/lib-game-state-providers.md), [Once-per-frame car step from two StarMap hooks, with a registry of the mod's craft](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) - related topic

