---
type: Game Feature
title: Recompile a mod's shader while the game runs
description: Why KSA's ShaderReloader cannot see a mod's shader and how to recompile one by calling FileReference.DoLoad through reflection and rebuilding the pipelines that held the old module
category: shaders
group: glsl-workflow
topic: reload-a-mod-shader-in-a-running-game
demonstrated_by:
  - mods/ksastructures/features/core-shader-library-include
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
lifecycle: [frame:after-on-frame, thread:background]
game_types:
  - KSA.FileReference
  - KSA.ShaderReference
  - KSA.AssetReloader.ShaderReloader
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.ModLibrary
tags: [hot-reload, shader, reflection, filereference, shaderreloader, development, pipeline-rebuild]
---
Iterating on GLSL is slow if every change needs a restart. KSA ships a shader reloader, but it cannot reach a mod's shader, so mods that develop compute shaders build their own: a command that recompiles the file in a running game, and a hook that drops the pipelines still holding the old module. This topic is the recipe and the engine facts behind it.

## Engine background

- **What the stock reloader does.** `Program` creates a `ShaderReloader` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1327)). It watches files, turns a changed path into a key with `GetModPath` (`fullPath.Substring(fullPath.IndexOf("Content"))`, [current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)) and, to reload, looks the key up in a private `_modPathsToIds` dictionary and calls
  `ShaderReference.DoLoad()` ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L102-L128)). In build 5554 nothing ever writes `_modPathsToIds` (only reads, L66, L70 and L106), `HotReloadingEnabled` is a public field defaulting to false, and a mod folder outside `Content` throws in `GetModPath`. So the stock path cannot recompile a mod's shader.
- **What does the work.** `FileReference.DoLoad` is `internal virtual` ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L166)); `ShaderReference.DoLoad` registers the reference, calls `Compile()`, installs the new module only if the compile succeeded, then destroys the old one ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L168-L181)).
  `Compile()` throws for a missing mod, path, file or renderer, and `ShaderModuleUtils.FromFile` throws on a compile error ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L118)), so in this build a failed reload leaves the old module in place.
- **Pipelines keep the module they were built from.** Pipelines already built keep drawing the old code until they are rebuilt, so a reload must also release whatever caches the pipeline (for compute passes, a `ComputePipelineWrapper` built lazily; `BuildPipeline` throws "Compute Shader is null" if the reference never compiled).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Bridge command that invokes FileReference.DoLoad by reflection and drops cached pipelines](reload-a-mod-shader-in-a-running-game-1.md) | KSAStructures | `reflection` | `frame:after-on-frame` | The minimal recipe, with before/after module handle and file SHA-1 so a no-op reload is visible |
| [Shader reload command in a developer file bridge, driven by an agent](reload-a-mod-shader-in-a-running-game-2.md) | KSArmory | `reflection` | `frame:after-on-frame` | Same reload for a list of seven shaders, exposed over an MCP server so an agent can edit GLSL and read the result in a capture |

## See also

- [Developer file bridge and MCP server for driving a running game](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [Core shader library include](../../../../mods/ksastructures/features/core-shader-library-include.md), [Drive a running game from outside through a file command bridge and an MCP server](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
- [Compile-check a mod's GLSL without launching the game](../check-mod-shaders-offline-with-glslang/overview.md), [Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md), [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md), [Let AI agents drive the game through an MCP server](../../../external-io/control/let-ai-agents-control-the-game-over-mcp/overview.md), [Patch vehicle paint and canopy shaders in memory at compile time](../../stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md)
