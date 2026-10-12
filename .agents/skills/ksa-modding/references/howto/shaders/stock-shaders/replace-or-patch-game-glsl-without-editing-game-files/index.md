# Game Feature

* [Replace or text-patch the game's GLSL without editing game files](overview.md) - How KSA compiles its own shaders from the Content/Core/Shaders tree and the one seam, ShaderModuleUtils.FromFile, that lets a mod compile patched copies instead

# How-To

* [Replace or text-patch the game's GLSL: a shadow tree, a FromFile redirect and anchor-verified edits](replace-or-patch-game-glsl-without-editing-game-files.md) - Copy Content/Core/Shaders into the mod folder at launch, redirect ShaderModuleUtils.FromFile to the copies and patch them with exact-count, idempotent text edits that log and skip on drift
