---
type: Mod Feature
title: Core shader library include
description: A header written at StarMapImmediateLoad holds absolute include paths into the player's install, so a mod's Shader asset can include KSA's Global.glsl, AtmosphereLuts.glsl and TextureSet.glsl
mod: ksastructures
category: shaders
techniques: [starmap-lifecycle, glsl-shader, xml-asset, reflection]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterOnFrame, reflection KSA.FileReference.DoLoad]
lifecycle: [startup:immediate-load, startup:asset-load, frame:after-on-frame]
game_types:
  - KSA.ShaderReference
  - KSA.FileReference
  - KSA.ModLibrary
  - KSA.Mod
  - KSA.AssetBundle
  - KSA.AssetReloader.ShaderReloader
  - RenderCore.Shaders.ShaderCompilerResolve
  - RenderCore.ShaderModuleUtils
  - Brutal.ShaderCApi.ShaderC
  - KSA.Rendering.ComputePipelineWrapper
tags: [glsl, shaderc, include, shader-asset, hot-reload, glslang, tooling]
---

The dark pass's compute shader needs KSA's global uniform block, its bindless texture set and the
atmosphere's ambient lookup, all of which live in the game's own shader library under
`Content/Core/Shaders`. The mod does not copy those files. At load it writes a small header whose
three `#include`s are absolute paths into the player's install. The mod's shader includes that header
by a path relative to itself. A `glslangValidator` script compiles the shader offline against the
same library, and the developer bridge can recompile it in a running game.

## How it works

**The shader asset.** `mod.toml` lists one asset file, and that file declares
`<Shader Id="KSAStructuresDarkCompute" Path="Shaders/KSAStructuresDark.comp" />`
([src/KSAStructures/mod.toml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/mod.toml), [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L21-L25)). KSA maps
`<Shader>` to `ShaderReference` ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L23)). The path is resolved
against the mod's own folder (`FileReference.ModPath` -> `Mod.GetPath` = `DirectoryPath + LocalPath`,
[current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L24), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)) and the
stage comes from the extension. The mod looks it up later with
`ModLibrary.TryGet<ShaderReference>("KSAStructuresDarkCompute")`
([src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L242-L247)).

**How KSA compiles it.** On the non-preload pass, `FileReference.OnDataLoad` registers the reference
as a loader ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55)). `ModLibrary.LoadAll` then runs every
loader's `Load()` in parallel (`Parallel.ForEachAsync`), and `Load` calls `DoLoad`
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519), [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L77)).
`ShaderReference.DoLoad` compiles with shaderc through `ShaderModuleUtils.FromFile` and an include
callback. It installs the new module and destroys the old one only after a successful compile, and
compile errors throw ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L118),
[current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L168-L181),
[current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L146)).[^ksa-shaderreference]

**How includes resolve.** The callback resolves `#include "x"` as
`Path.Combine(GetDirectoryName(requestingFile), x)`, with no search path at all
([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L254),
[current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs#L16-L36)). That is
`System.IO.Path.Combine` underneath ([current/decomp/Brutal.Strings/Utf8.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Strings/Utf8.cs#L260-L263)), which
returns the second argument whenever it is rooted, so an absolute include works. A relative one
cannot be written once for everyone: a mod lives in `Content/<id>` under the install or in
`mods/<id>` under the player's documents folder, and the path from the second to the install depends
on the machine and may cross drives
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L389-L394), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)).

**The generated header** ([src/KSAStructures/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CoreShaderInclude.cs#L46-L96)):

1. Resolves `Content/Core/Shaders/Common/Global.glsl`, `Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl`
   and `Content/Core/Shaders/Common/TextureSet.glsl` with `Path.GetFullPath`, relative to the working
   directory. That is the install root; KSA itself opens `Content` relatively
   ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L375)). If any file is missing it logs a warning and
   writes nothing.
2. Writes `<mod>/Shaders/Content/KSAStructuresCore.glsl` with one
   `#include "<absolute path>"` per file. Backslashes become forward slashes, because a backslash is an
   escape in a GLSL include.
3. The shader then does `#define SET_TEXTURE 2` and `#include "Content/KSAStructuresCore.glsl"`
   ([src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp#L13-L19)). The Core files' own relative
   includes (`../Common/Shared.glsl` and so on) resolve inside the install tree, against their own
   directories.

**Why the folder is called `Content`.** Every resolved include passes through
`ShaderReference.OnIncludeResolved`, which calls `ShaderReloader.GetModPath`:
`fullPath.Substring(fullPath.IndexOf("Content"))`
([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L140-L166),
[current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)). An include path with no
`Content` in it gives `IndexOf` = -1, `Substring` throws, and the catch logs "Error resolving include
path" as an error on every compile. The compile itself carries on. The key kept for the header is the
text from the first `Content` on, so naming the file for the mod keeps one mod's key from colliding
with another's (`Content/KSAStructuresCore.glsl` for a mod in the documents folder).

**Reload from the bridge.** The game's `ShaderReloader` cannot reach a mod's shader in this build: its
`_modPathsToIds` map is never filled, so `ReloadShader` has nothing to look up
([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L28),
[current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L102-L128)). The bridge's `reload_shaders`
command gets the `internal virtual FileReference.DoLoad` by reflection
([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L166)). For each id it checks the file exists, invokes
`DoLoad`, logs the module handle before and after with the file's SHA-1, and finally calls
`DarkPass.Release()` so the next frame builds new pipelines against the new module
([src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L302-L348), [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L126-L132)).
Pipelines already built keep the module they were created from, so the old ones keep drawing until
that release.

**Offline check.** `tools/check-shaders.sh` compiles every `Shaders/*.comp` with Khronos
`glslangValidator -V --target-env vulkan1.3 -I<install>/Content/Core/Shaders`. Into a temp
directory it writes its own `Content/KSAStructuresCore.glsl` with *relative* includes
(`Common/Global.glsl`, ...) found through `-I`. It inserts
`#extension GL_GOOGLE_include_directive : require` after line 1, because shaderc accepts `#include`
without it and glslang does not, and it warns that reported line numbers are one past the file's.
It skips, and exits 0, without a validator or an install, which is the CI case
([tools/check-shaders.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-shaders.sh#L1-L68)).

## Lifecycle and timing

- **`[StarMapImmediateLoad]` writes the header** ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L30-L40)).
  StarMap fires it from a prefix on `Mod.PrepareSystems`. KSA calls that per mod inside
  `ModLibrary.PrepareAll` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L429-L445)), which `Program` runs
  before `PreloadAssetBundles` and well before `ModLibrary.LoadAll`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L900), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)).
- **Shaders compile in `ModLibrary.LoadAll`** (`startup:asset-load`), on thread-pool workers, after
  every mod's `PrepareSystems`. The preload pass registers no loaders (`OnDataLoad` checks
  `mod.Preload`). `[StarMapAllModsLoaded]` is a postfix on `LoadAll`, so it is too late to write
  the header. Immediate load is the last mod hook before the compile.
- **Written every load.** The path it holds is only true of the install that wrote it.
- **`reload_shaders`** runs from the bridge in `[StarMapAfterOnFrame]` (`frame:after-on-frame`), on
  the main thread, after the frame's command buffer was submitted.
- **First use** is the dark pass's `Build`, inside the render. `ComputePipelineWrapper.BuildPipeline`
  throws "Compute Shader is null" if the reference never compiled
  ([current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L224-L229)), so the mod looks the
  reference up first and stands the pass down with a warning when it is missing.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CoreShaderInclude.cs#L18-L96) | Finds the three Core files and writes the header |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L30-L40) | Calls it at `StarMapImmediateLoad`, before anything compiles |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L21-L25) | The `<Shader>` asset |
| [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp#L13-L19) | `SET_TEXTURE` and the include of the generated header |
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L302-L348) | `reload_shaders`: reflective `DoLoad` and pipeline release |
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/check-shaders.sh) | Offline compile against the install's library |

## Snippets

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

```bash
# tools/check-shaders.sh L46-L58
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

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `ShaderReference` (`<Shader>` asset), `DoLoad`, `Shader` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L21-L181) | Compile and module swap; `Shader.VkHandle` logged by the bridge |
| `FileReference.DoLoad` (internal), `ModPath` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L24-L166) | Reflective recompile; path resolution |
| `ShaderReloader.GetModPath` | [current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58) | The "Content" keying that dictates the folder name |
| `ShaderCompilerResolve` / `ShaderC.Callbacks.Resolve` | [current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs#L16-L36) | Include resolution relative to the requesting file |
| `ModLibrary.PrepareAll`, `ModLibrary.LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522) | Where immediate load fires and where shaders compile |
| `ModLibrary.TryGet<ShaderReference>` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776) | Lookup by id |
| `ComputePipelineWrapper.BuildPipeline` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L224-L268) | Consumes `ShaderReference.Shader` |

## Pitfalls and version notes

- **No `Content` in an include path means an error per compile**, not a failed compile; it is easy to
  miss in the log.
- **The header is machine-specific.** It is written into the installed mod folder at every load and
  is not in the repository; a copy shipped from another machine would point at someone else's install.
- **The bridge comment over-states the risk of a missing file.** It says a missing file would make
  `DoLoad` destroy the old module and keep nothing. In build 2026.10.10.5554, `Compile()` throws for a
  missing file before anything is destroyed ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L95-L98)),
  so the pre-check is harmless but not what saves the module.
- **Thread-static include state.** `ShaderCompilerResolve.OnIncludeResolved` and the compile options
  are `[ThreadStatic]`, because shaders compile in parallel at load; do not assume one shader compiles
  at a time.
- **glslang is not shaderc.** The offline check uses `-I` search paths where shaderc uses
  requesting-file-relative resolution, so a path that resolves offline can still fail in game, and the
  other way round.
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554, and the decompiled diff between the two touches none of the shader-loading code.

## See also

- [Underground dark compute pass](./underground-dark-compute-pass.md): the pass this shader belongs to
- [Dev file bridge and MCP](./dev-file-bridge-and-mcp.md): the bridge that carries `reload_shaders`
- [Headless tests and CI](./headless-tests-and-ci.md): where `check-shaders.sh` runs and skips
- [Asset and mesh validation tools](./asset-and-mesh-validation-tools.md): the other offline checks
- [Check shaders offline: a per-folder check beside asset and mesh validation](../../../howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-2.md) - how-to article demonstrating this feature
- [Include the game's shader library: a generated Shaders/Content/CoreAtmosphere.glsl pulling in atmosphere and texture headers](../../../howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-1.md) - how-to article demonstrating this feature
- [Include the game's shader library: a generated KSAStructuresCore.glsl under a folder called Content](../../../howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-2.md) - how-to article demonstrating this feature
- [Reload a mod shader: a bridge command that invokes FileReference.DoLoad by reflection and drops cached pipelines](../../../howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/reload-a-mod-shader-in-a-running-game-1.md) - how-to article demonstrating this feature

[^ksa-shaderreference]: KSA.ShaderReference (decompiled)
