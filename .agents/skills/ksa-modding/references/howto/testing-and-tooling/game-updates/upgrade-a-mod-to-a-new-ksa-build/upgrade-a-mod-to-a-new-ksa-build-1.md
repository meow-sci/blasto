---
type: How-To
title: Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last
description: A repeatable upgrade procedure of confirm, refresh binaries and decompiled corpus, diff only what the mod touches, diff the game XML, fix, record the new build everywhere, run the chain and fly it, then push the private mirror first
category: testing-and-tooling
group: game-updates
topic: upgrade-a-mod-to-a-new-ksa-build
approach: 1
difficulty: intermediate
prerequisites: [bash, git, ilspycmd]
demonstrated_by:
  - mods/ksarmory/features/ksa-assemblies-lock
  - mods/ksarmory/features/api-surface-tracking
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksagolf/features/api-surface-and-assembly-lock
techniques: [decompiling, api-surface, agent-skill]
hooks: []
lifecycle: [event:game-update]
game_types: [KSA.Vehicle, KSA.Program]
tags: [game-update, upgrade, procedure, decompile, diff, lock, agent-skill]
---

Outcome: when KSA ships a new build, you can move the mod onto it in a fixed order of steps, each of which surfaces a different class of breakage, and finish with evidence from a real flight rather than a green test suite. KSArmory packages the procedure as an agent skill (`upgrade-ksa`); the steps below are that skill's, with the commands.
Demonstrated by KSArmory ([.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/upgrade-ksa/SKILL.md#L6-L29)). Prerequisites: the lock and decompile tooling from [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md) and [Keep a searchable decompiled copy of the game](../decompile-the-game-for-reference/overview.md), and an API surface list from [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md).

## Background: how the game does it

KSA is pre-release with no official code-modding API. Members get renamed (the compiler finds those) and, worse, members keep their name and change meaning: different units, a frame of reference, an enum's order. The second kind is invisible to the compiler and has to be read out of the decompiled sources. The changelog is also not a complete record: this corpus's own `current/version.json` lists the commits between two revisions ([current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5)), and the decompile diff is the full one.
Typical contracts that no tool can check include phase and frame-order facts in `Program.OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141)) and what `Vehicle.PrepareWorker` hands the mod ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)); KSArmory keeps a table of such "named contracts" in the skill and adds a row whenever a fix depends on the engine doing something rather than declaring it.

## Lifecycle and timing

Development time (`event:game-update`). The one runtime link is that the mod logs the build it was built for against the build running (`startup:immediate-load`), and hides optional features when they differ; releasing promptly restores them. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Confirm the update is real.** Compare the published build and the installed build with what you pin. If both say up to date, stop:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L34-L35
   ./tools/check-ksa-version.sh          # what RocketWerkz publish vs what we pin
   ./tools/check-assemblies.sh --game    # what is installed vs what we pin
   ```

2. **Refresh the binaries and the corpus, then commit them together** in the private repository, with the new build number in its `KSA_BUILD` file. Committing apart makes the next diff straddle two commits. Do not upgrade `ilspycmd` as part of an upgrade: a different decompiler version rewrites the whole corpus and buries the real diff:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L46-L48
   ./tools/sync-import.sh                                    # local Import/
   ./tools/sync-assemblies.sh      ../ksa-game-assemblies    # the mirror's DLLs
   ./tools/decompile-assemblies.sh ../ksa-game-assemblies    # the mirror's sources
   ```

3. **Find what actually broke.** Read the surface list against the new corpus. Missing members are breaks you must fix (`MOVED` usually means a refactor into a base class: check, do not assume). Changed files are the ones to read, because the compiler will not tell you about a changed meaning:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L60
   ./tools/ksa-api-diff.sh ../ksa-game-assemblies
   ```

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L80
   git -C ../ksa-game-assemblies diff HEAD~1 -- current/src/KSA/KSA/Vehicle.cs
   ```

   Pay particular attention to reference frames (a change in what a property is relative to is catastrophic and silent), units, transform plumbing the mod writes every frame, and anything called once per frame.

4. **Diff the game's XML too.** The mod also binds to the XML serialisation contract (part definitions, editor tags, connectors, materials, particles, sounds). None of it compiles against anything, so a renamed attribute defaults silently. Core's own content ships in the install and is the reference schema:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L135-L136
   diff -ru "<old install>/Content/Core" "/mnt/c/Program Files/Kitten Space Agency/Content/Core" \
       --include='*.xml' | head -200
   ```

   With no old install to hand, compare the `[XmlElement]` and `[XmlAttribute]` names of the deserialised types in the decompiled corpus against what the mod writes. Run asset validators against the install, not offline mode, or Core ids cannot be seen.

5. **Build, fix and test.** Fix compile errors against the decompiled sources, not by guessing; ask narrow questions of the game DLLs with a metadata dump tool. The tests link only the pure folder, so passing proves the simulation, not the binding:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L162-L163
   ./tools/build.sh
   ./tools/test.sh
   ```

6. **Record the new build everywhere it is written down.** Re-record the digests (`check-assemblies.sh --update`), edit the `build` line in the lock by hand (every other check reads it), set the private mirror's `KSA_BUILD`, and update the prose files that name the build. A documentation check fails any file that names a build number but not the lock's, so a missed one is a red CI run. Do not sweep the number with `sed`: a doc that says it was read against the new build is asserting someone read it. History lines (what was flown against which build) are not updated. Then regenerate the surface, because fixing breakages usually changes what the mod binds to:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L181
   ./tools/check-assemblies.sh --update      # rewrites the digests in ksa-assemblies.lock
   ```

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L221
   ./tools/api-surface.sh
   ```

7. **Verify the whole chain.** Run the one check script that CI runs (it carries the documentation check that proves step 6), validators against the install, and any slow sweep. Then tick the recheck list of things the engine does not yet do or expose (calls it does not make, modules it does not have), which nothing else will surface:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L227-L229
   ./tools/check-all.sh                 # all 21, and the pre-push hook runs it anyway
   ./tools/validate-parts.py            # against the install, NOT --offline, so Core is readable
   ./tools/model/checkswept.py          # nothing adrift or passing through anything
   ```

8. **Fly it.** A green suite is not evidence: nothing above has run a line of KSA. Fly an unattended scenario that exercises the whole binding and fails when the outcome is wrong ([Fly scripted scenarios unattended](../../in-game-testing/run-unattended-scenarios-and-report-a-verdict/overview.md)), then look at it, because a scenario checks the outcome and not the picture. Anything that misbehaves goes in the checklist as a finding against the new build, not in the retarget commit as a silent fix:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L249
   ./tools/scenario.sh head-on          # a whole engagement, unattended, pass/fail
   ```

9. **Push, private repository first.** CI here fails against a lock it cannot satisfy otherwise. Commit with a conventional message (`build(ksa): retarget <build>`), keep behaviour changes in their own commit, and release promptly if your mod hides features on a build mismatch:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L274
   git -C ../ksa-game-assemblies push        # MUST be first
   ```

## Complete example

| File | Role |
|---|---|
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/upgrade-ksa/SKILL.md) | The whole procedure as an agent skill |
| [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-api-diff.sh) | Missing members and changed files |
| [tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/decompile-assemblies.sh) | Deterministic corpus |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-assemblies.sh) | Digest check and `--update` |
| [tools/check-ksa-version.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-ksa-version.sh) | Published build against the pin |

## Variations and gotchas

- **Huge unreadable corpus diff.** Either `ilspycmd` was upgraded or the mirrored assembly set changed; both rewrite everything. Redo the corpus with the same tool version and set.
- **CI fails on the lock but builds locally.** The private repository was not pushed, or was pushed after the mod repository.
- **Compile errors mentioning types that clearly exist.** `Import/` and the mirror drifted; the digest check says which folder the build resolved.
- **A part loads but behaves differently with nothing in any log.** An XML attribute was renamed and sits at its default (step 4).
- **Tests pass and the game misbehaves.** Expected; steps 3 and 8 exist for exactly that.

## See also

- [Overview](overview.md); [approach 2](upgrade-a-mod-to-a-new-ksa-build-2.md), [approach 3](upgrade-a-mod-to-a-new-ksa-build-3.md)
- [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md), [Keep a searchable decompiled copy of the game](../decompile-the-game-for-reference/overview.md)
- [Test Harmony patches without running the game](../../offline-tests/test-harmony-patches-without-the-game/overview.md), [Check the game version](../../../getting-started/lifecycle/check-the-game-version/overview.md)
- Mod features: [KSArmory KSA assemblies lock](../../../../mods/ksarmory/features/ksa-assemblies-lock.md), [KSArmory API surface tracking](../../../../mods/ksarmory/features/api-surface-tracking.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md)
