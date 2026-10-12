# Game Feature

* [Compile-check a mod's GLSL without launching the game](overview.md) - Why KSA only compiles mod shaders at load, how glslangValidator can stand in for the game's shaderc with an include search path, and how to keep shader constants in step with C# by text tests

# How-To

* [Check shaders offline: a per-folder check beside asset and mesh validation](check-mod-shaders-offline-with-glslang-2.md) - Run the same glslang check as one named step of a validation suite that also checks part XML, asset paths, comment rules and meshes, so a shader typo fails the push with the other static checks
* [Check shaders offline: check-shaders.sh in a pre-push hook and CI, skipped without an install](check-mod-shaders-offline-with-glslang-1.md) - Compile a mod's compute shaders with glslangValidator against the install's include directory, install the tool on demand, skip cleanly without an install and call the same script from the hook and CI
* [Check shaders offline: keep GLSL constants in step with C# using a regex text test](check-mod-shaders-offline-with-glslang-3.md) - Read const floats and kind-tag thresholds out of the .comp source in unit tests and assert them against the C# values, catching drift that no compiler can
