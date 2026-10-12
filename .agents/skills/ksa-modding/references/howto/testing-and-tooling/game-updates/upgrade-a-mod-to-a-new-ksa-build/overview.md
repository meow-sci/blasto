---
type: Game Feature
title: Upgrade a mod to a new KSA build and degrade gracefully when the API drifts
description: A repeatable procedure of refreshing the pinned binaries and decompiled corpus, diffing only what the mod touches, classifying each coupling by how it fails, fixing, recording the new build and flying it, plus runtime design that latches a single feature as degraded instead of crashing
category: testing-and-tooling
group: game-updates
topic: upgrade-a-mod-to-a-new-ksa-build
demonstrated_by:
  - mods/ksarmory/features/ksa-assemblies-lock
  - mods/ksarmory/features/api-surface-tracking
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksagolf/features/api-surface-and-assembly-lock
  - mods/purrtty/features/ksa-upgrade-coupling-review-skill
  - mods/gatos/features/ksa-anchor-churn-tracking
lifecycle: [event:game-update, startup:all-mods-loaded, frame:after-on-frame]
game_types: [KSA.Program, KSA.GameSettings, KSA.ImGuiBackendVulkanImpl, KSA.Vehicle]
tags: [game-update, upgrade, procedure, coupling-catalog, graceful-degradation, agent-skill]
---

KSA is pre-release with no official modding API, and its builds move often. A mod is exposed in four ways: direct member references (compile error), Harmony targets (fail at load, logged), reflection (silent) and changed behaviour behind unchanged signatures (silently wrong). The three approaches here are complementary halves of one practice: a procedure that finds all four, a catalog that says how each coupling fails, and runtime design that keeps one broken binding from taking the mod down.

The changelog is not enough. A snapshot's `current/version.json` records the build and the per-revision commit lines, and the skills in the mods warn that its commit list can have gaps; the decompile diff is the complete record.

## Engine background

| Piece | Where | Use |
|---|---|---|
| Build record | [current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5) | Build number, date and revision range of a snapshot; a first read for which subsystems moved |
| Decompiled corpus | `current/decomp` and `current/dll` in the assemblies checkout | What a snapshot diff reads; the layout all three skills assume |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) | Example of a stock method whose call order mods anchor on |
| `Vehicle.PrepareWorker` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396) | A public virtual that mods patch; an anchor-annotated or pinned target |
| Frame order | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | Phase facts that no tool can check and that an upgrade review re-reads |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: lock, decompile both builds, diff narrowed to the API surface, then update the lock](upgrade-a-mod-to-a-new-ksa-build-1.md) | ksarmory ([.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/upgrade-ksa/SKILL.md#L6-L29)) | `decompiling` | `event:game-update` | A fixed nine-step order that ends in a flight; needs a private mirror, a decompile corpus and discipline about tool versions |
| [2: coupling-catalog review skill that classifies how each coupling fails](upgrade-a-mod-to-a-new-ksa-build-2.md) | purrtty ([.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md#L60-L67)) | `agent-skill` | `event:game-update`, `startup:all-mods-loaded` | Catches the silent failures the surface cannot; the catalog needs ongoing maintenance |
| [3: KsaAnchor-annotated bindings, scope catalog and health latches](upgrade-a-mod-to-a-new-ksa-build-3.md) | gatos ([gatOS.GameMod/Game/Ksa/KsaAnchor.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/KsaAnchor.cs#L26-L49)) | `annotation` | `event:game-update`, `frame:after-on-frame` | A compile break lands on an annotated site and one failing accessor degrades alone; annotations rot unless a rule keeps them current |

Rules all three share: diff against the last **validated** build, not the adjacent one; never upgrade the decompiler as part of an upgrade; do not treat a green test suite as evidence when the tests never load the game; and record what was verified in the same change as the fix.

## See also

- [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Keep a searchable decompiled copy of the game next to your mod](../decompile-the-game-for-reference/overview.md), [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md)
- [Test Harmony patches without running the game](../../offline-tests/test-harmony-patches-without-the-game/overview.md), [Test a mod against the real game with HeadlessHarness](../../offline-tests/headless-test-harness-for-game-mods/overview.md), [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md), [Check the game version](../../../getting-started/lifecycle/check-the-game-version/overview.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Mod features: [KSArmory KSA assemblies lock](../../../../mods/ksarmory/features/ksa-assemblies-lock.md), [KSArmory API surface tracking](../../../../mods/ksarmory/features/api-surface-tracking.md), [purrTTY upgrade coupling review skill](../../../../mods/purrtty/features/ksa-upgrade-coupling-review-skill.md), [gatOS KSA anchor churn tracking](../../../../mods/gatos/features/ksa-anchor-churn-tracking.md)
