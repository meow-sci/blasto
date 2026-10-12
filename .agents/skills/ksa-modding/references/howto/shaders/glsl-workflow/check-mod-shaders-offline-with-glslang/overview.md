---
type: Game Feature
title: Compile-check a mod's GLSL without launching the game
description: Why KSA only compiles mod shaders at load, how glslangValidator can stand in for the game's shaderc with an include search path, and how to keep shader constants in step with C# by text tests
category: shaders
group: glsl-workflow
topic: check-mod-shaders-offline-with-glslang
demonstrated_by:
  - mods/ksarmory/features/repo-static-checks
  - mods/ksarmory/features/core-shader-library-include
  - mods/ksastructures/features/asset-and-mesh-validation-tools
  - mods/ksastructures/features/core-shader-library-include
  - mods/ksarmory/features/raymarched-mushroom-cloud
lifecycle: [startup:immediate-load, thread:background]
game_types: [KSA.ShaderReference, Brutal.ShaderCApi.ShaderC]
tags: [glslang, shaderc, ci, pre-push, glsl, validation, text-test, offline]
---
KSA compiles a mod's GLSL only when it loads the mod, so a typo, a reserved word used as a name or a missing include otherwise costs a build, a deploy and a launch to discover. Two mods compile their shaders in a second with Khronos `glslangValidator` against the install's shader library, from a pre-push hook and CI, and one adds unit tests that read constants out of
the shader text so C# and GLSL cannot drift apart silently. This topic collects those scripts and their traps.

## Engine background

- **Compile happens at load.** `ShaderReference.DoLoad` compiles each `<Shader>` with shaderc during `ModLibrary.LoadAll` ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L181)); nothing validates a shader earlier.
- **shaderc and glslang resolve includes differently.** The game's include callback builds `directory-of-requester + include text` ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)) and accepts `#include` without the Vulkan GLSL extension; glslang takes search paths (`-I`) and wants `#extension GL_GOOGLE_include_directive : require`. A path that resolves offline can still fail in
  game, and the other way round, so the offline check is a gate for typos and type errors, not proof.
- **The game's headers are the game's.** The install's `Content/Core/Shaders` is not in the mod repository, so the check skips with a notice on a machine with no install, which is the CI case.
- **The generated include header.** The mod's shader includes a header written at load with absolute paths ([Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md)); the offline check writes the same header with relative includes and passes the library as `-I`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [check-shaders.sh in the pre-push hook and CI, skipped without an install](check-mod-shaders-offline-with-glslang-1.md) | KSArmory | `glsl-shader` | `startup:immediate-load` (what it mirrors), `thread:background` (CI) | The full script, installer, skip rules and the one-script-two-callers discipline |
| [Per-folder check beside asset and mesh validation](check-mod-shaders-offline-with-glslang-2.md) | KSAStructures | `glsl-shader` | `startup:immediate-load` (what it mirrors) | Same script as one named check in a validation suite alongside XML, asset path and mesh checks |
| [Regex over const floats in the .comp file asserted against C# values](check-mod-shaders-offline-with-glslang-3.md) | KSArmory | `glsl-shader` | `thread:background` (tests) | Catches drift no compiler will: constants, kind-tag thresholds, tunable ids |

## See also

- [One check script shared by the pre-push hook and CI](../../../../mods/ksarmory/features/repo-static-checks.md), [Asset XML, mesh and shader checks that run without the game](../../../../mods/ksastructures/features/asset-and-mesh-validation-tools.md), [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md)
- [Recompile a mod's shader while the game runs](../reload-a-mod-shader-in-a-running-game/overview.md), [Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md), [Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Run one check script from the pre-push hook and from CI](../../../testing-and-tooling/offline-tests/run-repo-checks-from-a-pre-push-hook-and-ci/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../../../assets/validation/validate-asset-xml-references/overview.md)
