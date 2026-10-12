---
type: How-To
title: "Include the game's shader library: a generated Shaders/Content/CoreAtmosphere.glsl pulling in atmosphere and texture headers"
description: Write a header with absolute includes of Global.glsl, AtmosphereLuts.glsl and TextureSet.glsl at StarMapImmediateLoad and include it from the mod's compute shaders by a fixed relative name
category: shaders
group: glsl-workflow
topic: include-the-game-shader-library-in-mod-glsl
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/core-shader-library-include
  - mods/ksastructures/features/core-shader-library-include
techniques: [starmap-lifecycle, glsl-shader]
hooks: [starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load, startup:asset-load]
game_types:
  - KSA.ShaderReference
  - KSA.AssetReloader.ShaderReloader
  - RenderCore.Shaders.ShaderCompilerResolve
  - KSA.Mod
  - KSA.ModLibrary
tags: [glsl, include, generated-header, compute, atmosphere-luts, absolute-path, immediate-load, ksarmory]
---
KSArmory's cloud and fire-light compute shaders call KSA's own atmosphere code: the same LUT lookups, the global uniform block and the bindless texture array. This article is the recipe: at `[StarMapImmediateLoad]` write `Shaders/Content/CoreAtmosphere.glsl` into the mod folder with three `#include` lines containing absolute paths into the running install, and include that
header by a fixed relative name from the `.comp` files. Nothing of the game's is copied or redistributed, and the header is regenerated every launch so it always names the install that is running. You need StarMap and a mod whose `mod.toml` lists the shader assets.

## Background: how the game does it

See the topic overview for the resolver: includes resolve relative to the requesting file, a rooted include wins, and every resolved path must contain `Content` for the reload bookkeeping. The game's shader headers expect certain macros from the includer: `Global.glsl` has none; `AtmosphereLuts.glsl` pulls in `Common/Shared.glsl` and
needs the LUT samplers bound at the set the shader declares; `TextureSet.glsl` needs `SET_TEXTURE` defined first. `ComputePipelineWrapper` binds the game's global set at set 0 for every compute pass.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Write the header | First statement of `OnImmediateLoad`, a StarMap prefix on `Mod.PrepareSystems`, called per mod from `ModLibrary.PrepareAll` | `startup:immediate-load` |
| Compile the mod's `<Shader>` assets | `ModLibrary.LoadAll`, in parallel on thread-pool workers, after every mod's `PrepareSystems` | `startup:asset-load` |

Writing the header any later (for example at `[StarMapAllModsLoaded]`) would be after the compile. `Mod.DirectoryPath` is the folder of the mod's manifest ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91-L109)). See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the header first thing at immediate load:**

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L132-L137
       [StarMapImmediateLoad]
       public void OnImmediateLoad(Mod mod)
       {
           // Before anything asks KSA to compile a shader: the generated header names this machine's
           // own install, and a .comp that includes it will not compile until it is there.
           CoreShaderInclude.Write(mod.DirectoryPath);
   ```

   Source: [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L132-L137).

2. **Resolve the three Core headers against the working directory** (the game's install root; the mod assumes this, and the decompilation does not show how KSA sets it) and require each to exist:

   ```csharp
   // src/KSArmory/Ksa/CoreShaderInclude.cs L39-L44
       private static readonly string[][] Wanted =
       [
           ["Content", "Core", "Shaders", "Common", "Global.glsl"],
           ["Content", "Core", "Shaders", "Atmosphere", "AtmosphereLuts.glsl"],
           ["Content", "Core", "Shaders", "Common", "TextureSet.glsl"],
       ];
   ```

   Source: [src/KSArmory/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CoreShaderInclude.cs#L39-L44).

3. **Generate the header with forward slashes, into a folder called `Content`:**

   ```csharp
   // src/KSArmory/Ksa/CoreShaderInclude.cs L63-L94
               List<string> found = [];

               foreach (string[] parts in Wanted)
               {
                   string core = Path.GetFullPath(Path.Combine(parts));

                   if (!File.Exists(core))
                   {
                       Log.Warn($"core shaders: '{core}' is not there, so the cloud keeps its "
                                + "approximated sky; KSA's own atmosphere needs the game's shader tree");
                       return false;
                   }

                   found.Add(core);
               }

               string shaders = Path.Combine(modFolder, Folder);
               if (!Directory.Exists(shaders))
               {
                   Log.Warn($"core shaders: no '{shaders}' to write into");
                   return false;
               }

               // Forward slashes whatever the platform: these are GLSL includes, not paths the shell
               // will ever see, and a backslash in one is an escape.
               string body = "// Generated at load. Points at this machine's own KSA install;\n"
                             + "// nothing of the game's is copied here. See Ksa/CoreShaderInclude.cs.\n";

               foreach (string core in found) body += $"#include \"{core.Replace('\\', '/')}\"\n";

               string into = Directory.CreateDirectory(Path.Combine(shaders, Subfolder)).FullName;
               File.WriteAllText(Path.Combine(into, Generated), body);
   ```

   Source: [src/KSArmory/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CoreShaderInclude.cs#L63-L94). The file is per machine: never commit or ship it.

4. **Define the macros the headers need, then include the generated header by relative name:**

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L1-L19
   #version 450

   // KSA's own atmosphere, through a header this mod writes at load pointing at the player's install:
   // shaderc resolves an include against the requesting file, and Core's shader tree and a mod's do
   // not meet by any relative path. Ksa/CoreShaderInclude.cs writes it; nothing of the game's is
   // copied here.
   // ...
   #define SET_TEXTURE 2
   #define SET_CLOUD_DATA 5

   #include "Content/CoreAtmosphere.glsl"
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1-L19). `SET_CLOUD_DATA 5` is defensive: none of the three headers includes `Clouds/CloudData.glsl` in build 5554.

5. **Declare the shader in asset XML** (`<Shader Id="..." Path="Shaders/KSArmoryCloud.comp" />`); KSA compiles it with shaderc at load, the include resolves next to the `.comp`, and the header's absolute includes then resolve, with their own relative includes resolving inside the Core tree.

6. **Check offline before you ship.** KSA compiles mod shaders only at load, so the repo compiles them in a second with glslangValidator against the install's library; see [Compile-check a mod's GLSL without launching the game](../check-mod-shaders-offline-with-glslang/overview.md) (the script writes a temporary header with relative includes and uses `-I`, because glslang takes a search path where shaderc took the absolute one).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CoreShaderInclude.cs) | Writes the header |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Calls it at immediate load |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp), [src/KSArmory/Shaders/KSArmoryFireLight.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryFireLight.comp) | The includers |
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh) | Offline compile |

## Variations and gotchas

- **No fallback.** The class comment says that without the header "the cloud still draws, on the approximated sky", but both `.comp` files include it unconditionally and GLSL has no conditional include. A failed `Write` on a fresh install is a compile failure of those shaders; the cloud pipeline build then throws and the compute pass stands down.
- **Per machine.** The header holds an absolute install path and is regenerated each launch.
- **Working directory assumption.** `Path.GetFullPath("Content/...")` relies on the game's working directory being its install root (unverified here).
- **Coupled to KSA's shader internals.** A rename or signature change in `AtmosphereLuts.glsl`, `Global.glsl` or `TextureSet.glsl` breaks the mod's shaders at load; the offline check catches it only on a machine with the install present.
- **glslang is not shaderc,** so a path that resolves offline can still fail in game.

## See also

- Siblings: [KSAStructures variant](include-the-game-shader-library-in-mod-glsl-2.md), [relative include in a patched copy](include-the-game-shader-library-in-mod-glsl-3.md), [overview](overview.md)
- [A generated include that reaches KSA's own shader library from a mod shader](../../../../mods/ksarmory/features/core-shader-library-include.md), [Core shader library include](../../../../mods/ksastructures/features/core-shader-library-include.md)
- [Compile and write GLSL at runtime for a mod's own pipeline](../compile-glsl-at-runtime/overview.md), [Recompile a mod's shader while the game runs](../reload-a-mod-shader-in-a-running-game/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)
