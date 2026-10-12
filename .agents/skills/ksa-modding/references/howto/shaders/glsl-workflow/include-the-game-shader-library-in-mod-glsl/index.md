# Game Feature

* [Include KSA's own GLSL library from a mod shader](overview.md) - How KSA resolves shader includes and why a mod needs a generated header with absolute paths to reuse Global.glsl, AtmosphereLuts.glsl and TextureSet.glsl

# How-To

* [Include the game's shader library: a generated KSAStructuresCore.glsl under a folder called Content](include-the-game-shader-library-in-mod-glsl-2.md) - Why the generated include must live under a folder named Content, why it uses forward slashes, and how the mod's own Shader asset XML references the shader that includes it
* [Include the game's shader library: a generated Shaders/Content/CoreAtmosphere.glsl pulling in atmosphere and texture headers](include-the-game-shader-library-in-mod-glsl-1.md) - Write a header with absolute includes of Global.glsl, AtmosphereLuts.glsl and TextureSet.glsl at StarMapImmediateLoad and include it from the mod's compute shaders by a fixed relative name
* [Include the game's shader library: a relative include of a stock helper inside a patched copy](include-the-game-shader-library-in-mod-glsl-3.md) - When the mod already runs a copy of the stock shader tree, a plain relative include of a stock helper works with no generated header, because includes follow the redirected path
