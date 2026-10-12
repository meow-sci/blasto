---
type: Mod Feature
title: KSA upgrade coupling review skill
description: An AI-assistant skill that drives a diff of two decompiled KSA builds against a catalog of every place the mod couples to game code, classified by how each coupling fails (compile, patch load, reflection, silent behavior)
mod: purrtty
category: testing-and-tooling
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks: [claude-code skill upgrade-ksa]
lifecycle: [event:game-update]
game_types:
  - KSA.Program
  - KSA.GameSettings
  - KSA.SuperMeshRenderSystem
  - Brutal.ImGuiApi.Abstractions.ConsoleWindow
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Cursor
tags: [game-updates, decompile, review, ai-tooling, harmony, maintenance]
---

KSA updates often. A mod that patches game methods, records into the renderer and reads live game
state can break in ways a successful compile does not reveal. purrTTY keeps a Claude Code skill,
`.claude/skills/upgrade-ksa/SKILL.md`, that turns "is purrTTY still OK on the new build?" into a
repeatable review: given the decompiled sources of the previous and current KSA builds, walk a
catalog of every coupling point, diff the exact game members each depends on, and report impact and
severity per touchpoint. Any mod with more than a couple of patches can copy the structure.

## How it works

**Inputs.** Two snapshots in the `ksa-game-assemblies` layout (`decomp/`, `dll/`, `version.json`).
The skill refuses to proceed with only one, never hard-codes paths, and warns that
`version.json.commits[]` can have gaps, so the decomp diff is the complete record. The baseline is
the last build the mod was validated on, not whatever the "previous" folder holds.[^upgrade-skill]

**Coupling classes** (the core idea):

| Coupling | Fails at | Caught by |
|---|---|---|
| Direct member reference | Compile | `dotnet build` against the new `dll/` |
| Harmony patch target | Load, logged not thrown | Diff plus the mod log, or an offline patch-apply harness |
| Reflection by name | Runtime, silently | Diff only |
| Behavioral (draw order, alpha model, input chain order, coordinate conventions, the console print funnel) | Runtime, silently wrong | Diff plus reasoning |
| Asset ids (`ModLibrary.Get`, `ModLibrary.Find`) | Runtime | Diff |
| Build references | Compile | Build |

**Catalog.** Eight sections enumerate every touchpoint with file, game member, what is relied on,
and what breaks: the six Harmony patches with exact signatures, the in-world render pipeline
(offscreen target formats and samples, presets, reverse-Z, shader ids, camera matrix conventions),
the ImGui backend (secondary Vulkan backend, `AddTexture`, font atlas, the `KeySuper` reflection
probe), game-state reads (vehicle/part ego transforms, decouple identity, `Cursor.GetEgoRay`), the
input chain, menu injection, StarMap lifecycle and ALC sharing, and build references plus native
libraries.

**Offline patch check.** The skill describes a throwaway console app that loads the built mod DLL
and the new game DLLs and runs `harmony.CreateClassProcessor(type).Patch()` on every patch class,
with notes on registering an `AssemblyLoadContext.Default.Resolving` probe before any method that
mentions game types is JIT-compiled, and on patching `KSA.dll`'s PE machine field in a scratch copy
to run on arm64 hosts.

**Output.** One block per touchpoint with the KSA delta, impact, severity (critical for required
patches, high for render coupling, medium for optional patches and asset ids, low for mechanical
renames and the reflection probe) and the concrete change or "no change needed". The skill is a
review; edits happen only when asked. A companion `ksa` skill holds general notes on game systems
(camera, lifecycle, parts, quads, telemetry).[^ksa-skill]

## Lifecycle and timing

Not a runtime feature. It encodes the lifecycle assumptions to re-check, for example its ordering
note that the "renderer is live only from `AllModsLoaded` onward", and that the in-world quad postfix
runs after atmosphere, clouds and ocean. Checked against build 2026.10.10.5554, the first note is
looser than the code: `Program.GetRenderer()` is set in the `Program` constructor before any mod hook
runs, while the main viewport and `Program.OffscreenTarget` are created only after `ModLibrary.LoadAll`,
that is after `StarMapAllModsLoaded`. purrTTY is unaffected because it builds all GPU objects lazily
inside a frame.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md) | The review procedure and coupling catalog |
| [.claude/skills/ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/ksa/SKILL.md) | General KSA modding notes used by the review |
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs) | The patch classes the catalog lists |
| [docs/gotchas.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/gotchas.md) | Numbered gotchas the catalog cross-references |

## Snippets

```markdown
<!-- .claude/skills/upgrade-ksa/SKILL.md L60-L67 -->
| Coupling kind | Breaks at | Detected by |
|---|---|---|
| Direct call to a referenced member (signature change) | **compile** | `dotnet build` against `<current-ksa>/dll` |
| **Harmony patch target** (rename / removed / signature drift) | **runtime, load** | patch silently fails to apply — logged, not thrown (`Patcher` swallows per-patch) |
| **Reflection** (string member names) | **runtime, silent** | member resolves to null; behavior degrades with no error |
| **Behavioral / semantic** (render draw order, premultiplied alpha, input-chain order, coordinate-frame convention, the print funnel) | **runtime, silent wrong output** | nothing — only diffing KSA source + reasoning catches it |
| **Asset id** (`ModLibrary.Get<>("UnlitMeshVert")`, `ModLibrary.Find("ModMenu")`) | **runtime** | throws / feature self-skips |
| **Build ref** (a `$(KSAFolder)` DLL renamed/removed/split) | **compile / copy** | `dotnet build` (missing `<Reference>`) |
```

## Game APIs touched

The catalog names, among others, `Program.OnKey`, `Program.DispatchKeyEvent`,
`Program.DrawProgramMenusHook`, `GameSettings.OnKeyAll`, `ConsoleWindow.Print`,
`SuperMeshRenderSystem.RenderTranslucencyPass`, `ImGuiBackendVulkanImpl`, `Cursor.GetEgoRay`. Against
build 2026.10.10.5554 ([current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json)) all of the patch targets
listed exist with the signatures the catalog records.[^ksa-version]

## Pitfalls and version notes

- The catalog is only as good as its maintenance; the skill ends with a "Maintaining this skill"
  section, and the repo's `CLAUDE.md` mandates updating docs with code changes.[^claude-md]
- Line numbers in the catalog are hints; navigate by symbol.
- The skill was last exercised for KSA 5482; this corpus checked the patch targets against 5554.

## See also

- [Failure-isolated Harmony patching](./failure-isolated-harmony-patching.md)
- [Cross-platform build and release](./cross-platform-build-and-release.md)
- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [Draw after the translucency pass](./draw-after-translucency-pass-postfix.md)
- [purrTTY overview](../overview.md)
- [Review an upgrade against a coupling catalog that classifies how each coupling fails](../../../howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-2.md) - how-to article demonstrating this feature
- [Check that every patch class still applies to a new game build with a throwaway console app](../../../howto/testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/test-harmony-patches-without-the-game-2.md) - how-to article demonstrating this feature

[^upgrade-skill]: purrTTY .claude/skills/upgrade-ksa/SKILL.md
[^ksa-skill]: purrTTY .claude/skills/ksa/SKILL.md
[^ksa-version]: ksa-game-assemblies current/version.json (build 2026.10.10.5554)
[^claude-md]: purrTTY CLAUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
