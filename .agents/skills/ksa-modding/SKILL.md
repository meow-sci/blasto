---
name: ksa-modding
description: "Implementation knowledge for writing Kitten Space Agency (KSA) game mods in C#: 1001 step-by-step how-to articles across 312 topics (getting started with StarMap mod loading and lifecycle, Harmony patching, ImGui UI, rendering, shaders, assets, physics, vehicles and parts, flight and orbits, camera, celestials, kittens and EVA, persistence, audio, external I/O, testing and tooling) plus 613 analyzed features of 22 community mods with pinned code references and decompiled game API names. Use when creating, debugging or researching a KSA or StarMap mod, KSA game internals, mod.toml, or Harmony patches against KSA types."
license: "MIT (see LICENSE); quoted mod code under third-party licenses, see references/ATTRIBUTION.md"
compatibility: "Search script needs Python 3.8+ (stdlib only). Code links point to GitHub."
metadata:
  game-build: "2026.10.10.5554"
  corpus-commit: "4b2779c"
  generated: "2026-10-11"
  source: https://github.com/meow-sci/ksa-coffer
---

# KSA modding knowledge base

Curated, source-pinned knowledge for writing Kitten Space Agency (KSA) mods with the StarMap loader: 1001 how-to articles in 312 topics (16 categories) and 613 analyzed features of 22 community mods (34 submods). Everything is pinned to game build `2026.10.10.5554`. All content lives under `references/`; open only what you need.

## Workflow

1. Find the topic: run `python3 scripts/search.py <terms>` (paths below are relative to this skill's directory), or open the matching category catalog from the table below.
2. Read the topic `overview.md` first. It explains the engine system and has an Approaches table comparing the numbered How-To articles. Pick one approach.
3. Read the chosen How-To article (`<topic>-N.md` next to the overview).
4. For real working code, open the pages listed in its `demonstrated_by` frontmatter (ids relative to `references/`, append `.md`).
5. Verify every game type and member against the user's game build before relying on it.

Search options: `--type how-to|feature|mod|topic|submod`, `--category <slug>`, `--mod <slug>`, `--lifecycle <token>`, `--limit N`. Example: `python3 scripts/search.py harmony transpiler drag --type how-to`.

## Find by category

Each catalog lists every topic and its articles (title and difficulty); the topic overview compares the articles.

| Category | Open when | Catalog |
|---|---|---|
| Getting started | Build, deploy and understand a StarMap code mod end to end - project and mod.toml setup... | [catalog](references/catalog-howto-getting-started.md) |
| Patching | Generic Harmony technique for KSA mods - skipping prefixes, finalizers and scoped overrides... | [catalog](references/catalog-howto-patching.md) |
| UI | ImGui windows, menu bar and ModMenu entries, fonts, draw-list overlays and widgets... | [catalog](references/catalog-howto-ui.md) |
| Rendering | Drawing things the game does not draw by itself - ImGui overlays on the sky and world... | [catalog](references/catalog-howto-rendering.md) |
| Shaders | The shader-authoring side of KSA modding - writing GLSL post-processing passes... | [catalog](references/catalog-howto-shaders.md) |
| Assets | The data side of a KSA mod: the asset XML vocabulary and how ids and paths resolve... | [catalog](references/catalog-howto-assets.md) |
| Physics | Forces, drag and aero, wheels and suspension, colliders, Bepu, custom motion... | [catalog](references/catalog-howto-physics.md) |
| Vehicles and parts | Defining parts and whole craft, spawning and controlling vehicles at runtime... | [catalog](references/catalog-howto-vehicles-and-parts.md) |
| Flight and orbits | Reading a vehicle's flight state and orbit, working in KSA's coordinate frames and epochs... | [catalog](references/catalog-howto-flight-and-orbits.md) |
| Camera | Camera modes and their controllers, writing the pose in phase with the frame... | [catalog](references/catalog-howto-camera.md) |
| Celestials | Celestial data and surface placement: star systems and the body tree, body data and caches... | [catalog](references/catalog-howto-celestials.md) |
| Kittens | Characters and EVA kittens - reaching the avatar, skeleton and animation catalog... | [catalog](references/catalog-howto-kittens.md) |
| Persistence | Where a mod keeps its files, how it reads and writes settings (ini, json, toml)... | [catalog](references/catalog-howto-persistence.md) |
| Audio | Playing sound in KSA from a mod through the game's FMOD system - shipping sound XML assets... | [catalog](references/catalog-howto-audio.md) |
| External I/O | Everything that connects a KSA mod to the world outside the frame: sampling... | [catalog](references/catalog-howto-external-io.md) |
| Testing and tooling | Testing and tooling for KSA mods - game-free unit tests... | [catalog](references/catalog-howto-testing-and-tooling.md) |

## Community mods

22 community mods were analyzed feature by feature, each with pinned code references. Open [references/catalog-mods.md](references/catalog-mods.md) when you want a working reference implementation, a mod's architecture, or to see how an existing mod solved a problem. Feature pages are under `references/mods/<mod>/features/`.

## Read first for any code mod

Open [the game loop and lifecycle overview](references/howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md) before writing code that touches game state: it explains the per-frame order, threads and where each hook runs. Its articles: [Hook the frame with StarMap lifecycle attributes](references/howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md); [Hook any frame point with Harmony patches on Program and Universe](references/howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md); [Mutate simulation state safely relative to the physics step](references/howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md); [Run code inside the physics step on worker threads](references/howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md). Use them to choose when your code must run.

## Vocabulary

- [references/lifecycle.md](references/lifecycle.md): open when a page's `lifecycle` token (for example `frame:draw-ui`, `startup:all-mods-loaded`, `physics:vehicle-solver`) is unclear, or for any question about when code may run and what state is safe to touch.
- [references/techniques.md](references/techniques.md): open to decode `techniques` tokens (`harmony-transpiler`, `imgui-window`, `glb-model`, ...) or to filter by technique.

## Gotchas

- Content is pinned to game build `2026.10.10.5554`. KSA changes often: re-check type and member names against the current game assemblies.
- Code links are GitHub blob URLs pinned to specific commits; line anchors apply to that commit only.
- Links to decompiled game files (`current/decomp/...` in the meow-sci/ksa-game-assemblies repo) may not be accessible. Treat the backticked type and member names as the reference.
- Numbered articles in one topic (`foo-1.md`, `foo-2.md`) are alternative approaches, not sequential steps. Choose one via the overview's Approaches table.
- `demonstrated_by` ids are relative to `references/` and omit `.md`.
- Each directory has an `index.md` listing its pages.
- The search index covers titles, descriptions and tags only. For body-only terms use `grep -rli <term> references/howto references/mods`.

## Licensing

Skill content is MIT; quoted mod code keeps its original license. See [references/ATTRIBUTION.md](references/ATTRIBUTION.md).
