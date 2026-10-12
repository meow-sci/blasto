---
type: How-To
title: "Check shaders offline: a per-folder check beside asset and mesh validation"
description: Run the same glslang check as one named step of a validation suite that also checks part XML, asset paths, comment rules and meshes, so a shader typo fails the push with the other static checks
category: shaders
group: glsl-workflow
topic: check-mod-shaders-offline-with-glslang
approach: 2
difficulty: beginner
prerequisites: [bash, glslang]
demonstrated_by:
  - mods/ksastructures/features/asset-and-mesh-validation-tools
  - mods/ksastructures/features/core-shader-library-include
techniques: [glsl-shader]
hooks: [git pre-push, github-actions ci.yml]
lifecycle: [startup:immediate-load]
game_types: [KSA.ShaderReference]
tags: [glslang, validation, suite, pre-push, ci, glsl, ksastructures]
---
KSAStructures has one compute shader, and the same script as in the [KSArmory article](check-mod-shaders-offline-with-glslang-1.md) guards it, but here it is one named check among several in `tools/check-all.sh`: part XML well formed, asset paths resolve, comment rules, shader compiles, no tracked artefacts, mesh checks. This article shows how the shader check slots into such a suite and what differs for a mod with a different
shader folder and header name.

## Background: how the game does it

The game compiles `Shaders/KSAStructuresDark.comp` at load with the generated header `Shaders/Content/KSAStructuresCore.glsl` (absolute paths, written at `[StarMapImmediateLoad]`, see [Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md)). The offline check must reproduce just enough of that environment: the same header name under a `Content` folder, relative includes, the install's `Content/Core/Shaders` on the
search path. glslang takes the include as a search path where shaderc takes the absolute one, and wants `#extension GL_GOOGLE_include_directive : require`.

## Lifecycle and timing

Development time only; it mirrors the compile at `startup:immediate-load` plus `startup:asset-load`. The suite runs from `tools/check-all.sh` (called by the pre-push hook and the CI tooling job) and from `tools/deploy.sh`.

## Steps

1. **Name your header in the temporary folder exactly as the shader includes it.** The shader includes `Content/KSAStructuresCore.glsl`, so the script writes that file with relative includes of the three Core headers:

   ```bash
   # tools/check-shaders.sh L43-L58
   WORK="$(mktemp -d)"
   trap 'rm -rf "$WORK"' EXIT

   mkdir -p "$WORK/Content"
   printf '#include "Common/Global.glsl"\n#include "Atmosphere/AtmosphereLuts.glsl"\n#include "Common/TextureSet.glsl"\n' \
       > "$WORK/Content/KSAStructuresCore.glsl"

   failed=0
   for shader in "$REPO_ROOT"/src/KSAStructures/Shaders/*.comp; do
       name="$(basename "$shader")"

       # shaderc takes #include without being asked; glslang wants the extension named.
       awk 'NR==1 { print; print "#extension GL_GOOGLE_include_directive : require"; next } 1' \
           "$shader" > "$WORK/$name"

       if out="$("$GLSLANG" -V --target-env vulkan1.3 "-I$LIBRARY" -o /dev/null "$WORK/$name" 2>&1)"; then
   ```

   Source: [tools/check-shaders.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-shaders.sh#L43-L58). The rest of the script (skip rules, `--install`, error echo) is as in the KSArmory version, with the cache directory named for the mod.

2. **Add it as one line of the suite**, between the other offline checks:

   ```bash
   # tools/check-all.sh L79-L84
   run "Sim/ is free of KSA types"     ./tools/check-boundary.sh
   run "Part XML is well formed"       ./tools/check-xml.sh
   run "Asset paths resolve"           ./tools/validate-parts.py --offline
   run "Comment rules"                 ./tools/check-comments.sh
   run "Shader compiles"               ./tools/check-shaders.sh
   run "No artefacts tracked"          ./tools/check-tracked.sh
   ```

   Source: [tools/check-all.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-all.sh#L79-L84). The suite's `run` helper counts passes, collects failures and skips, and prints a summary; a check that depends on the game exits 0 with a "skipped" notice rather than failing.

3. **Make the hook and CI call the suite, not the pieces:**

   ```bash
   # .githooks/pre-push L14-L19
   if "$REPO_ROOT/tools/check-all.sh"; then
       exit 0
   fi
   ```

   Source: [.githooks/pre-push](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.githooks/pre-push#L14-L19). The CI tooling job runs `./tools/check-all.sh` ([.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.github/workflows/ci.yml#L65-L69)).

4. **Reload in game for anything the compiler cannot judge.** Once the offline check passes, the in-game reload ([Recompile a mod's shader while the game runs](../reload-a-mod-shader-in-a-running-game/overview.md)) tests behaviour against the real game libraries.

## Complete example

| File | Role |
|---|---|
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-shaders.sh) | Offline compile |
| [tools/check-all.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-all.sh) | The suite |
| [.githooks/pre-push](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.githooks/pre-push) | Pre-push caller |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/.github/workflows/ci.yml) | CI caller |

## Variations and gotchas

- **Header names must match.** The temp header must be named exactly as the shader includes it, and live in a `Content` folder, or the offline include fails where the game's would succeed (or the reverse).
- **glslang is not shaderc,** so a path that resolves offline can still fail in game.
- **Skipped in CI.** With no install the shader step prints a "skipped" notice and exits 0; the in-game compile is the final gate.
- **Line numbers are one past** the file's own.

## See also

- Siblings: [KSArmory variant](check-mod-shaders-offline-with-glslang-1.md), [constants in sync with C#](check-mod-shaders-offline-with-glslang-3.md), [overview](overview.md)
- [Asset XML, mesh and shader checks that run without the game](../../../../mods/ksastructures/features/asset-and-mesh-validation-tools.md), [Core shader library include](../../../../mods/ksastructures/features/core-shader-library-include.md)
- [Validate asset XML ids and paths before launching the game (offline)](../../../assets/validation/validate-asset-xml-references/overview.md), [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md)
