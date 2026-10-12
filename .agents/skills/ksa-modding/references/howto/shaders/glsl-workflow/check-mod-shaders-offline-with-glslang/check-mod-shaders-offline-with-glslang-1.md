---
type: How-To
title: "Check shaders offline: check-shaders.sh in a pre-push hook and CI, skipped without an install"
description: Compile a mod's compute shaders with glslangValidator against the install's include directory, install the tool on demand, skip cleanly without an install and call the same script from the hook and CI
category: shaders
group: glsl-workflow
topic: check-mod-shaders-offline-with-glslang
approach: 1
difficulty: beginner
prerequisites: [bash, glslang]
demonstrated_by: [mods/ksarmory/features/repo-static-checks, mods/ksarmory/features/core-shader-library-include]
techniques: [glsl-shader]
hooks: [git pre-push, github-actions ci.yml]
lifecycle: [startup:immediate-load, thread:background]
game_types: [KSA.ShaderReference, Brutal.ShaderCApi.ShaderC]
tags: [glslang, validation, pre-push, ci, glsl, skip-with-notice, ksarmory]
---
KSArmory's `tools/check-shaders.sh` compiles every `.comp` in the mod against the game's own shader library using Khronos' `glslangValidator`. It takes about a second, installs the tool on demand, and skips with a notice when there is no tool or no game install. `tools/check-all.sh` runs it with the rest of the repository checks, and the same script is called by the git `pre-push` hook and the CI
"tooling" job, so "what I ran before pushing" and "what CI runs" cannot drift. You need bash, a shader folder and (to actually compile) a KSA install.

## Background: how the game does it

KSA only compiles a mod's shaders when it loads them, and the game's shaderc resolves includes against the requesting file with no search path. glslang differs in three ways the script absorbs: it takes `-I` search paths, it needs `#extension GL_GOOGLE_include_directive : require` to accept `#include`, and its reported line numbers are one past the original file's once that line
is inserted. The mod's shaders include a generated header ([Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md)); the script writes its own relative version of that header.

## Lifecycle and timing

Development time only: this mirrors the compile that happens at `startup:immediate-load` plus `startup:asset-load` in the game, but runs on a developer machine (`pre-push`) or in CI (`thread:background` from the game's point of view). It never runs in the game.

## Steps

1. **Locate the validator and the library; skip if either is missing.** Default the game directory with an environment variable and exit 0 with a notice so CI without an install passes visibly:

   ```bash
   # tools/check-shaders.sh L16-L41
   REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
   KSA_DIR="${KSA_DIR:-/mnt/c/Program Files/Kitten Space Agency}"
   LIBRARY="$KSA_DIR/Content/Core/Shaders"
   CACHE="$HOME/.cache/ksarmory/glslang"
   VERSION="16.6.0"

   if [[ "${1:-}" == "--install" ]]; then
       mkdir -p "$CACHE"
       curl -sSL -o "$CACHE/glslang.tar.gz" \
           "https://github.com/KhronosGroup/glslang/releases/download/$VERSION/glslang-$VERSION-linux-x86_64-release.tar.gz"
       tar xzf "$CACHE/glslang.tar.gz" -C "$CACHE"
       rm "$CACHE/glslang.tar.gz"
   fi

   GLSLANG="$(command -v glslangValidator || true)"
   [[ -z "$GLSLANG" && -x "$CACHE/bin/glslangValidator" ]] && GLSLANG="$CACHE/bin/glslangValidator"

   if [[ -z "$GLSLANG" ]]; then
       echo "skipped: no glslangValidator -- ./tools/check-shaders.sh --install fetches one"
       exit 0
   fi

   if [[ ! -d "$LIBRARY" ]]; then
       echo "skipped: no KSA shader library at '$LIBRARY' -- set KSA_DIR"
       exit 0
   fi
   ```

   Source: [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh#L16-L41). The default is a WSL path to a Windows install; set `KSA_DIR` on other layouts.

2. **Write the relative include header into a temp folder named `Content`** (the same name the game-side header uses):

   ```bash
   # tools/check-shaders.sh L43-L48
   WORK="$(mktemp -d)"
   trap 'rm -rf "$WORK"' EXIT

   mkdir -p "$WORK/Content"
   printf '#include "Common/Global.glsl"\n#include "Atmosphere/AtmosphereLuts.glsl"\n#include "Common/TextureSet.glsl"\n' \
       > "$WORK/Content/CoreAtmosphere.glsl"
   ```

   Source: [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh#L43-L48).

3. **Compile each shader with the extension inserted:**

   ```bash
   # tools/check-shaders.sh L50-L68
   failed=0
   for shader in "$REPO_ROOT"/src/KSArmory/Shaders/*.comp; do
       name="$(basename "$shader")"

       # shaderc takes #include without being asked; glslang wants the extension named.
       awk 'NR==1 { print; print "#extension GL_GOOGLE_include_directive : require"; next } 1' \
           "$shader" > "$WORK/$name"

       if out="$("$GLSLANG" -V --target-env vulkan1.3 "-I$LIBRARY" -o /dev/null "$WORK/$name" 2>&1)"; then
           echo "  $name compiles"
       else
           failed=1
           # The line numbers are the file's own plus the one inserted above.
           echo "$out" | grep -E "ERROR" | sed "s#$WORK/##" | sed 's/^/  /'
           echo "  (line numbers are one past the file's: the include directive is inserted)"
       fi
   done

   exit "$failed"
   ```

   Source: [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh#L50-L68). The shader keeps its own `#define SET_TEXTURE ...` lines, so macros resolve the way they do in game; the first line must be `#version`.

4. **Put it in one list and call that list from both places.** `check-all.sh` runs every check in a fixed order, tolerates missing tools by skipping with a notice, and lists the skipped ones; the shader step is one line:

   ```bash
   # tools/check-all.sh L101
   run "Shaders compile"               ./tools/check-shaders.sh
   ```

   Source: [tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh#L9-L15) explains the discipline ("one-script-two-callers"); the hook and CI call it:

   ```bash
   # .githooks/pre-push L14-L19
   if "$REPO_ROOT/tools/check-all.sh"; then
       exit 0
   fi
   ```

   Source: [.githooks/pre-push](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.githooks/pre-push#L14-L19). Install the hook with the repo's `tools/install-hooks.sh` (it points `core.hooksPath` at `.githooks/`), and have the CI job run `./tools/check-all.sh` ([.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml#L52-L86)).

## Complete example

| File | Role |
|---|---|
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh) | Offline compile |
| [tools/check-all.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-all.sh) | The one list of checks |
| [.githooks/pre-push](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.githooks/pre-push) | Runs the list before a push |
| [.github/workflows/ci.yml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.github/workflows/ci.yml) | Tooling job running the same list |

## Variations and gotchas

- **glslang is not shaderc.** Where a check passes offline, a path resolved by the game's include rule can still fail in game, and vice versa.
- **No install in CI.** The step skips with a notice and exits 0; the shader gate then only runs on developer machines with a game install.
- **Line numbers are one past** the file's own, because the extension line is inserted.
- **Linux download only.** `--install` fetches the Linux x86-64 release (16.6.0); on other platforms install glslang yourself.
- **Keep it fast.** The point is a second of feedback instead of a build, deploy and launch.

## See also

- Siblings: [KSAStructures variant](check-mod-shaders-offline-with-glslang-2.md), [constants in sync with C#](check-mod-shaders-offline-with-glslang-3.md), [overview](overview.md)
- [One check script shared by the pre-push hook and CI](../../../../mods/ksarmory/features/repo-static-checks.md), [A generated include that reaches KSA's own shader library from a mod shader](../../../../mods/ksarmory/features/core-shader-library-include.md)
- [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md), [Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md)
