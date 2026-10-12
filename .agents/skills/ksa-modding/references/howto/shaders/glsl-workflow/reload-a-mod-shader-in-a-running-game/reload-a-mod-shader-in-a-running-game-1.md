---
type: How-To
title: "Reload a mod shader: a bridge command that invokes FileReference.DoLoad by reflection and drops cached pipelines"
description: Recompile a mod's compute shader in a running game, report the module handle and file hash before and after, and release the pass's pipelines so the next frame uses the new code
category: shaders
group: glsl-workflow
topic: reload-a-mod-shader-in-a-running-game
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksastructures/features/core-shader-library-include
  - mods/ksastructures/features/dev-file-bridge-and-mcp
techniques: [reflection]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.FileReference.DoLoad]
lifecycle: [frame:after-on-frame]
game_types: [KSA.FileReference, KSA.ShaderReference, KSA.ModLibrary]
tags: [hot-reload, shader, reflection, filereference, development, ksastructures, module-handle]
---
This is the minimal "reload shaders" command a mod can add to its developer tooling. It looks up each of the mod's shader assets, checks the file exists, calls the internal `DoLoad` by reflection, reports the module handle before and after with the file's SHA-1, and finally releases the pass's cached pipelines. KSAStructures runs it from a file-command bridge, but any hook or key
binding works. You need the shader declared as a `<Shader>` asset in your mod's XML ([Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md)) and a place to call the command.

## Background: how the game does it

The stock `ShaderReloader` cannot see a mod shader (see the overview). `ShaderReference.DoLoad` is the method that does the work; it is `internal`, so the mod finds it on the base class `FileReference` with `BindingFlags.NonPublic | BindingFlags.Instance`. In build 5554 `Compile()` throws before anything is destroyed for a missing file, path, mod or renderer
([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L118)), and `FromFile` throws on a compile error, so the old module survives a failed reload. The bridge's own comment says a missing file would make `DoLoad` destroy the old module and keep nothing, which does not hold in this build, but the check is harmless.

## Lifecycle and timing

`reload_shaders` runs from the bridge in `[StarMapAfterOnFrame]` (`frame:after-on-frame`), on the main thread, after the frame's command buffer was submitted ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L78-L82)). Destroying the old `VkShaderModule` then does not affect pipelines already recorded for that frame. Pipelines already built keep the module they were created from,
so the old ones keep drawing until the pass's cache is released and the next frame builds new pipelines. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve `DoLoad` once.** If it does not resolve, fail the command with a clear message; do not throw:

   ```csharp
   // src/KSAStructures/Ksa/Bridge.cs L302-L312
       // KSA's own reloader cannot reach a mod's shader: it maps a path by finding "Content" in it.
       // What it does is ShaderReference.DoLoad, which is internal, and the pass's pipelines are then
       // dropped so they rebuild against the new module. A file that is missing makes DoLoad destroy
       // the old module and keep nothing, so that is checked first.
       private static Reply ReloadShaders()
       {
           MethodInfo? doLoad = typeof(FileReference).GetMethod("DoLoad", BindingFlags.NonPublic | BindingFlags.Instance);
           if (doLoad is null) return Failed("FileReference.DoLoad did not resolve");

           List<string> reloaded = [];
           List<string> seen = [];
   ```

   Source: [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L302-L348).

2. **Look each shader up by its asset id and check the file.** `ModLibrary.TryGet<ShaderReference>(id, out shader)` returns the reference registered from your XML; `ShaderReference.ModPath` is the resolved file path:

   ```csharp
   // src/KSAStructures/Ksa/Bridge.cs L313-L325
           foreach (string id in ShaderIds)
           {
               if (!ModLibrary.TryGet<ShaderReference>(id, out ShaderReference? shader) || shader is null)
               {
                   return Failed($"no shader '{id}'");
               }

               if (!File.Exists(shader.ModPath)) return Failed($"'{shader.ModPath}' is not there");

               // What was compiled and whether the module changed: a reload that answers "reloaded"
               // and draws the old shader is otherwise indistinguishable from one that worked.
               FileInfo file = new(shader.ModPath);
               nint before = shader.Shader?.VkHandle ?? 0;
   ```

3. **Invoke and report.** Unwrap `TargetInvocationException` so the shader compile error reaches the caller, then log handle before and after and the SHA-1 of the file:

   ```csharp
   // src/KSAStructures/Ksa/Bridge.cs L327-L348
               try
               {
                   doLoad.Invoke(shader, null);
               }
               catch (TargetInvocationException e)
               {
                   return Failed($"{id} did not compile: {e.InnerException?.Message ?? e.Message}");
               }

               nint after = shader.Shader?.VkHandle ?? 0;
               string sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(shader.ModPath))).ToLowerInvariant();
               string line = $"{id}: {shader.ModPath}, {file.Length} bytes written {file.LastWriteTime:HH:mm:ss}, "
                             + $"sha1 {sha1[..12]}, module {before:X} -> {after:X}";
               seen.Add(line);
               Log.Info($"bridge: reloaded {line}");

               reloaded.Add(id);
           }

           DarkPass.Release();
           return Done(new() { ["reloaded"] = reloaded, ["modules"] = seen });
       }
   ```

   Source: [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L327-L348). If the module handle did not change, the file you edited is not the one the game compiles (for example the installed copy differs from your working tree).

4. **Release the pipelines that hold the old module.** The pass keeps a view cache; clearing it makes the next frame build new pipelines against the new module:

   ```csharp
   // src/KSAStructures/Ksa/DarkPass.cs L126-L132
       /// <summary>Drops the pipelines, so the next frame builds them against the shader as it is now.</summary>
       public static void Release()
       {
           Views.Clear();
           _shaderMissing = false;
           _faults = 0;
       }
   ```

   Source: [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L126-L132). A mod whose compute pass wraps a `ComputePipelineWrapper` should destroy and recreate it here; destroying a pipeline still in flight is a validation error, so release at a safe point (after submit, as above).

5. **Run the loop.** Edit the `.comp` in the deployed mod folder, send the command, check the log line (`module <old> -> <new>`, new SHA-1), then capture a frame. A compile error is returned in the reply and the previous shader keeps running.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs) | `ReloadShaders` |
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs) | `Release()` and the lazily built pipelines |
| [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp) | The shader being reloaded |

## Variations and gotchas

- **Reflection on an internal method** can break on any game update; check the lookup and fail soft.
- **Includes are not reloaded separately.** `DoLoad` recompiles the shader file and its includes as they are on disk now; the generated include header is written at load only.
- **First use.** `ComputePipelineWrapper.BuildPipeline` throws "Compute Shader is null" if the reference never compiled; look the reference up first and stand the pass down with a warning when it is missing.
- **Thread.** `DoLoad` uses `Program.GetRenderer().Device`, so call it on the main thread at a safe point.

## See also

- Sibling: [KSArmory command in an MCP-driven bridge](reload-a-mod-shader-in-a-running-game-2.md); [overview](overview.md)
- [Developer file bridge and MCP server for driving a running game](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [Core shader library include](../../../../mods/ksastructures/features/core-shader-library-include.md)
- [Compile-check a mod's GLSL without launching the game](../check-mod-shaders-offline-with-glslang/overview.md), [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md)
