---
type: How-To
title: "Include the game's shader library: a generated KSAStructuresCore.glsl under a folder called Content"
description: Why the generated include must live under a folder named Content, why it uses forward slashes, and how the mod's own Shader asset XML references the shader that includes it
category: shaders
group: glsl-workflow
topic: include-the-game-shader-library-in-mod-glsl
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/core-shader-library-include]
techniques: [starmap-lifecycle, glsl-shader]
hooks: [starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load, startup:asset-load]
game_types: [KSA.ShaderReference, KSA.AssetReloader.ShaderReloader, KSA.FileReference, KSA.ModLibrary]
tags: [glsl, include, generated-header, content-folder, shader-reloader, shader-asset, ksastructures]
---
KSAStructures' dark-pass compute shader needs KSA's global uniform block, its bindless texture set and the atmosphere's ambient lookup. It uses the same generated-header trick as KSArmory, and this variant is a clear reference for three details the other write-up only mentions: the folder name `Content` and why it matters, the forward slashes in the header, and how
the mod's own asset XML points at the shader. Read the [KSArmory article](include-the-game-shader-library-in-mod-glsl-1.md) first for the core recipe.

## Background: how the game does it

- **The asset.** `mod.toml` lists one asset file, and that file declares `<Shader Id="KSAStructuresDarkCompute" Path="Shaders/KSAStructuresDark.comp" />` ([src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L21-L25)). KSA maps `<Shader>` to `ShaderReference` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L23)); the path is resolved against the mod's own folder
  (`FileReference.ModPath` is `Mod.GetPath`, `DirectoryPath + LocalPath`, [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L24)) and the stage comes from the extension. The mod looks the reference up later with `ModLibrary.TryGet<ShaderReference>("KSAStructuresDarkCompute")`.
- **Compile.** `FileReference.OnDataLoad` registers the reference as a loader on the non-preload pass ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55)); `ModLibrary.LoadAll` runs every loader in parallel and `ShaderReference.DoLoad` compiles with shaderc through `ShaderModuleUtils.FromFile` and the include callback
  ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L118)).
- **Why a relative path cannot be written once.** A mod lives in `Content/<id>` under the install or in `mods/<id>` under the player's documents folder, so the path from the second to the install depends on the machine and may cross drives ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L389-L394)).

## Lifecycle and timing

`[StarMapImmediateLoad]` writes the header ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L30-L40)); StarMap fires it from a prefix on `Mod.PrepareSystems`, which `ModLibrary.PrepareAll` calls per mod, before `PreloadAssetBundles` and well before `ModLibrary.LoadAll`. Shaders compile in `LoadAll` (`startup:asset-load`) on thread-pool workers; the preload pass registers no
loaders. `[StarMapAllModsLoaded]` is a postfix on `LoadAll`, so it is too late. The header is written on every load. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Name the folder `Content` and the file for your mod.** Every resolved include passes through `ShaderReference.OnIncludeResolved`, which calls `ShaderReloader.GetModPath`:

   ```csharp
   // current/decomp/KSA/ShaderReference.cs L140-L147
   	private void OnIncludeResolved(ReadOnlySpan<byte> sourcePath, ReadOnlySpan<byte> includePath)
   	{
   		string text = includePath.ToString16();
   		try
   		{
   			text = Path.GetFullPath(text);
   			text = Filepath.CorrectSeparators(ShaderReloader.GetModPath(text));
   			TempIncludePaths.Add(text);
   ```

   `GetModPath` is `fullPath.Substring(fullPath.IndexOf("Content"))` ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)). An include path with no `Content` gives `IndexOf == -1`, `Substring` throws, and the catch logs "Error resolving include path" as an error on every compile; the compile itself carries on. The key kept for the header is the text from the first `Content`
   on, so naming the file for the mod keeps one mod's key from colliding with another's. Source: [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L140-L166).

2. **Declare the constants and the three headers:**

   ```csharp
   // src/KSAStructures/Ksa/CoreShaderInclude.cs L20-L36
       // In a folder called Content: KSA's shader hot reload keys every include on the text after the
       // first "Content" in its full path, and one with none throws inside that bookkeeping and is
       // logged as an error on every compile. Named for this mod, so another mod's header of the same
       // job is never the one a search finds.
       private const string Folder = "Shaders";
       private const string Subfolder = "Content";
       private const string Generated = "KSAStructuresCore.glsl";

       // Under the game's own Content root. Global.glsl declares the set KSA binds at 0 for every
       // compute pass, the lighting block and the ambient table among it; AtmosphereLuts.glsl reads
       // them; TextureSet.glsl is KSA's bindless textures, which the ambient table is one of.
       private static readonly string[][] Wanted =
       [
           ["Content", "Core", "Shaders", "Common", "Global.glsl"],
           ["Content", "Core", "Shaders", "Atmosphere", "AtmosphereLuts.glsl"],
           ["Content", "Core", "Shaders", "Common", "TextureSet.glsl"],
       ];
   ```

   Source: [src/KSAStructures/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CoreShaderInclude.cs#L20-L36). If any file is missing the mod logs a warning and writes nothing.

3. **Write with forward slashes.** Backslashes in a GLSL `#include` are escapes:

   ```csharp
   // src/KSAStructures/Ksa/CoreShaderInclude.cs L76-L84
               // Forward slashes whatever the platform: these are GLSL includes, and a backslash in
               // one is an escape.
               string body = "// Generated at load. Points at this machine's own KSA install;\n"
                             + "// nothing of the game's is copied here. See Ksa/CoreShaderInclude.cs.\n";

               foreach (string core in found) body += $"#include \"{core.Replace('\\', '/')}\"\n";

               string into = Directory.CreateDirectory(Path.Combine(shaders, Subfolder)).FullName;
               File.WriteAllText(Path.Combine(into, Generated), body);
   ```

   Source: [src/KSAStructures/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CoreShaderInclude.cs#L76-L84).

4. **Include it from the shader, after the macros the headers need:**

   ```glsl
   // src/KSAStructures/Shaders/KSAStructuresDark.comp L13-L19
   // Set 2 is KSA's bindless textures, for the ambient table. Bindings follow ComputePipelineWrapper's
   // order: the storage image, the depth it samples, then the engine's two light results.

   #define SET_TEXTURE 2
   #define SET_CLOUD_DATA 5

   #include "Content/KSAStructuresCore.glsl"
   ```

   Source: [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp#L13-L19). The Core files' own relative includes (`../Common/Shared.glsl` and so on) resolve inside the install tree, against their own directories.

5. **Look the reference up before using it.** `ComputePipelineWrapper.BuildPipeline` throws "Compute Shader is null" if the reference never compiled, so the mod gets the `ShaderReference` first and stands the pass down with a warning when it is missing.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CoreShaderInclude.cs) | Finds the three Core files and writes the header |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs) | Calls it at `StarMapImmediateLoad` |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml) | The `<Shader>` asset |
| [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp) | Includes the generated header |

## Variations and gotchas

- **No `Content` in an include path means an error per compile,** not a failed compile; easy to miss in the log.
- **Machine-specific.** A copy shipped from another machine points at someone else's install.
- **Thread-static include state.** `ShaderCompilerResolve.OnIncludeResolved` and the compile options are `[ThreadStatic]` because shaders compile in parallel at load.
- **glslang is not shaderc.** The offline check uses `-I` search paths where shaderc uses requesting-file-relative resolution ([Compile-check a mod's GLSL without launching the game](../check-mod-shaders-offline-with-glslang/overview.md)).
- **Version.** The mod is built against KSA 2026.10.7.5541; the claims here were checked against 2026.10.10.5554.

## See also

- Siblings: [KSArmory variant](include-the-game-shader-library-in-mod-glsl-1.md), [relative include in a patched copy](include-the-game-shader-library-in-mod-glsl-3.md), [overview](overview.md)
- [Core shader library include](../../../../mods/ksastructures/features/core-shader-library-include.md)
- [Recompile a mod's shader while the game runs](../reload-a-mod-shader-in-a-running-game/overview.md), [Compile and write GLSL at runtime for a mod's own pipeline](../compile-glsl-at-runtime/overview.md)
