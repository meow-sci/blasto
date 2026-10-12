# Game Feature

* [Recompile a mod's shader while the game runs](overview.md) - Why KSA's ShaderReloader cannot see a mod's shader and how to recompile one by calling FileReference.DoLoad through reflection and rebuilding the pipelines that held the old module

# How-To

* [Reload a mod shader: a bridge command that invokes FileReference.DoLoad by reflection and drops cached pipelines](reload-a-mod-shader-in-a-running-game-1.md) - Recompile a mod's compute shader in a running game, report the module handle and file hash before and after, and release the pass's pipelines so the next frame uses the new code
* [Reload a mod shader: a reload command in a developer file bridge that an agent can drive](reload-a-mod-shader-in-a-running-game-2.md) - Expose the reflective shader reload as a bridge command for a list of shaders so an agent or script can edit GLSL, reload it and read the result in a captured frame
