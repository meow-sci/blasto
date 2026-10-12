---
type: How-To
title: Review an upgrade against a coupling catalog that classifies how each coupling fails
description: Keep a catalog of every mod-to-game coupling classified by failure mode and a skill that diffs two game builds against it and reports severity per touchpoint
category: testing-and-tooling
group: game-updates
topic: upgrade-a-mod-to-a-new-ksa-build
approach: 2
difficulty: advanced
prerequisites: [git, harmony]
demonstrated_by: [mods/purrtty/features/ksa-upgrade-coupling-review-skill]
techniques: [agent-skill, coupling-catalog, decompiling]
hooks: []
lifecycle: [event:game-update, startup:all-mods-loaded]
game_types: [KSA.Program, KSA.GameSettings, KSA.ImGuiBackendVulkanImpl, KSA.Vehicle]
tags: [game-update, upgrade, coupling-catalog, severity, agent-skill, review]
---

Outcome: "is the mod still fine on the new build?" becomes a repeatable review. A skill takes two decompiled snapshots, walks a catalog of every place the mod reaches into game code, diffs the exact game members each depends on, and reports impact and severity per touchpoint. The deliverable is a review, not silent edits.
Demonstrated by purrTTY ([.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md#L6-L52)). Any mod with more than a couple of patches can copy the structure. Prerequisites: two game snapshots in the assemblies-repo layout (decompiled sources, DLLs, version file).

## Background: how the game does it

A mod touches the game in ways the compiler catches and in ways it cannot. purrTTY patches game methods with Harmony (for example a postfix on the empty `Program.DrawProgramMenusHook`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018)), injects a draw into the render pipeline, drives a second ImGui context on a Brutal Vulkan backend, and reads live game state. Each of those fails differently when the game moves, so the review is organised by failure mode rather than by feature.
The snapshot layout the skill expects is the one this corpus's game checkout uses: `decomp/`, `dll/` and `version.json` ([current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5)). The changelog can have gaps, so the decompile diff is the only complete record.

## Lifecycle and timing

Not a runtime feature; it encodes the lifecycle assumptions to re-check. For example the skill's note that the renderer is live only from `AllModsLoaded` onward is looser than the code in build 2026.10.10.5554, where `Program.GetRenderer()` is set in the `Program` constructor before any mod hook runs while the main viewport and offscreen target are created after `ModLibrary.LoadAll`; purrTTY is unaffected because it builds all GPU objects lazily inside a frame. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Classify couplings by how they fail.** This table is the core idea. Compile catches direct references; a Harmony target fails at load and is logged, not thrown; reflection by name fails silently; behavioural assumptions (draw order, alpha model, input-chain order, coordinate conventions) produce silently wrong output; asset ids fail at runtime; build references fail at compile or copy:

   `.claude/skills/upgrade-ksa/SKILL.md L60-L67`:

   ```markdown
   | Coupling kind | Breaks at | Detected by |
   |---|---|---|
   | Direct call to a referenced member (signature change) | **compile** | `dotnet build` against `<current-ksa>/dll` |
   | **Harmony patch target** (rename / removed / signature drift) | **runtime, load** | patch silently fails to apply — logged, not thrown (`Patcher` swallows per-patch) |
   | **Reflection** (string member names) | **runtime, silent** | member resolves to null; behavior degrades with no error |
   | **Behavioral / semantic** (render draw order, premultiplied alpha, input-chain order, coordinate-frame convention, the print funnel) | **runtime, silent wrong output** | nothing — only diffing KSA source + reasoning catches it |
   | **Asset id** (`ModLibrary.Get<>("UnlitMeshVert")`, `ModLibrary.Find("ModMenu")`) | **runtime** | throws / feature self-skips |
   | **Build ref** (a `$(KSAFolder)` DLL renamed/removed/split) | **compile / copy** | `dotnet build` (missing `<Reference>`) |
   ```

2. **Require both snapshots and the right baseline.** The skill refuses to proceed with one tree and never hard-codes paths. The baseline is the mod's **last validated** build, not whatever the "previous" folder holds: if an intermediate build was never reviewed, diff from the last validated one (`git diff <validated-sha> <current-sha> -- current/decomp/<path>` when snapshots are git commits). A short `commits[]` never means nothing moved.

3. **Build against the new DLLs first.** That sweeps up every compile-visible break; a clean build clears only directly referenced members, and the tests are backend-only so they exercise almost none of the coupling:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L271-L273
   # from the purrtty repo root; adjust the env var to the supplied snapshot
   KSA_DLL_DIR="<current-ksa>/dll" dotnet build purrtty.slnx
   KSA_DLL_DIR="<current-ksa>/dll" dotnet test purrtty.slnx --nologo -v quiet
   ```

4. **Check that every patch still applies, offline.** A throwaway console app loads the built mod and the new DLLs and runs `CreateClassProcessor(type).Patch()` on every patch class ([Check that every patch class still applies](../../offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-2.md)).

5. **Walk the catalog.** Eight sections enumerate every touchpoint with the mod file, the game member, what is relied on and what breaks: the Harmony patches with exact signatures, the render pipeline (target formats, samples, reverse-Z, shader ids, camera matrix conventions), the ImGui backend, game-state reads, the input chain, menu injection, StarMap lifecycle and load-context sharing, and build references plus native libraries. For each, diff the named type in both trees:

   ```bash
   # .claude/skills/upgrade-ksa/SKILL.md L83
      diff <previous-ksa>/decomp/<Asm>/<Type>.cs <current-ksa>/decomp/<Asm>/<Type>.cs
   ```

6. **Triage by severity.** Critical for required patches and the input chain, high for render coupling and the ImGui backend, medium for optional patches and asset ids, low for mechanical renames and null-tolerant reflection probes ([.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md#L283-L296)).

7. **Report in a fixed format**, one block per touchpoint: the game delta with file and line or changelog commit, the mod location, the coupling kind, the impact, and the change needed (none, mechanical, or behavioural redesign), then a summary table and a verdict. If a check could not be done, say which:

   `.claude/skills/upgrade-ksa/SKILL.md L303-L308`:

   ```markdown
   ### <touchpoint> — <SEVERITY>
   - KSA delta: <what changed between previous and current, with decomp file:line or version.json commit>
   - purrTTY location: <repo file:symbol>
   - Coupling kind: <harmony | reflection | render/behavioral | game-state | build-ref | asset-id | lifecycle>
   - Impact: <what breaks at runtime/compile, who notices>
   - Change needed: <none | mechanical: … | behavioral redesign: …>
   ```

8. **Maintain the catalog with the code.** The skill ends with a "Maintaining this skill" section and the repository's contributor rules require updating docs with code; line numbers in the catalog are hints, so navigate by symbol.

## Complete example

| File | Role |
|---|---|
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md) | The review procedure and coupling catalog |
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L38-L71) | The patch classes the catalog lists, applied independently |
| [docs/gotchas.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/gotchas.md) | Numbered gotchas the catalog cross-references |

## Variations and gotchas

- **The catalog is only as good as its maintenance.** It was last exercised for one build and spot-checked against another; treat unexercised rows as hypotheses.
- **Severity is per mod.** purrTTY rates a drifted mouse-binding gate Medium because no default binds a mouse button; your mod's ratings follow your mod's defaults.
- **Do not edit during the review.** The skill reports; edits happen when asked, compile-visible fixes first and behavioural ones proposed.
- **Pair with a pinned surface.** A generated API surface list ([Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md)) narrows the diff mechanically; the catalog adds the behavioural assumptions the surface cannot see.
- **Game updates.** This is the update review; run it from the lock and decompile tooling in [approach 1](upgrade-a-mod-to-a-new-ksa-build-1.md).

## See also

- [Overview](overview.md); [approach 1](upgrade-a-mod-to-a-new-ksa-build-1.md), [approach 3](upgrade-a-mod-to-a-new-ksa-build-3.md)
- [Check that every patch class still applies](../../offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-2.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md), [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)
- Mod feature: [purrTTY upgrade coupling review skill](../../../../mods/purrtty/features/ksa-upgrade-coupling-review-skill.md)
