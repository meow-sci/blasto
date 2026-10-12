# Game Feature

* [Patch vehicle paint and canopy shaders in memory at compile time](overview.md) - How a Harmony prefix on ShaderModuleUtils.FromFile and Utils.SetShaderFromMod can compile transformed GLSL source instead of the file on disk, and how to make the game recompile

# How-To

* [Patch shaders in memory: a compile-time transform with packed colours, a conflict check and runtime masters](patch-game-shaders-in-memory-at-compile-time-3.md) - Arm and disarm an in-memory shader transform at runtime with dynamic Harmony patches, refuse when another mod owns the compile seam, and force the recompile only on the setting change
* [Patch shaders in memory: a MeshIndirect.frag patch for vehicle paint, recompiled on a requested rebuild](patch-game-shaders-in-memory-at-compile-time-1.md) - Prefix ShaderModuleUtils.FromFile to compile a transformed copy of the part fragment shader, then set Program.RendererRebuildNeeded so the game recompiles every part pipeline
* [Patch shaders in memory: Model.vert, Model_Skinned.vert and ModelPbr.frag patched for canopy projection](patch-game-shaders-in-memory-at-compile-time-2.md) - Force three model shaders through the compile path with a SetShaderFromMod prefix, patch their source in a FromFile prefix and key the new behaviour on a material marker
