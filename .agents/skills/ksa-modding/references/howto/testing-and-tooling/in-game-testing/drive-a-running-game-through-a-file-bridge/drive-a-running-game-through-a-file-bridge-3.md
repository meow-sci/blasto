---
type: How-To
title: A lean bridge with shader hot reload, held camera shots and a UI flag instead of work in the UI pass
description: Extend the file bridge with reload_shaders through the internal FileReference.DoLoad, a composed camera shot held every step, and capture requests that a UI button only flags
category: testing-and-tooling
group: in-game-testing
topic: drive-a-running-game-through-a-file-bridge
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
techniques: [file-ipc, reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.FileReference.DoLoad]
lifecycle: [frame:after-on-frame, startup:all-mods-loaded, frame:draw-ui, frame:viewports]
game_types: [KSA.FileReference, KSA.ShaderReference, KSA.ModLibrary, KSA.Program, KSA.Universe, KSA.GameSaves]
tags: [testing, bridge, shader-reload, camera, mcp, visual-checks]
---

Outcome: the same file protocol as [article 1](drive-a-running-game-through-a-file-bridge-1.md), trimmed to what visual work needs: edit a mod GLSL file, send
`reload_shaders`, send `camera`, `capture`, look at the picture, repeat without restarting the game. Demonstrated by
KSAStructures ([feature](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md)) and KSArmory
([feature](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)), which add a camera that holds the main view as one claimant among others and
a panel button that only sets a flag. Prerequisites: StarMap, a bridge from article 1, and a mod shader declared as a `ShaderReference` asset.

## Background: how the game does it

- KSA has a shader hot reloader, but it maps file paths by looking for `"Content"` in the path, which a mod folder under `Documents/My Games` does not
  contain ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54)). The work itself is `FileReference.DoLoad()`, an internal virtual method
  ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L166)), and a `ShaderReference` knows its on-disk `ModPath`. Mod assets are found by id with
  `ModLibrary.TryGet<T>(string, out T)` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776)).
- Screenshots are requested through the private `Program._screenshotCapture` and hide the UI while they run, which is why the bridge hook is the frame postfix and why a button
  should not run a capture: its per-frame work would stop once `DrawUI` is cleared, because `StarMapAfterGui` is inside `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)). Details in
  [Take clean screenshots from code](../take-screenshots-from-code/overview.md).
- The main view belongs to whoever last wrote the camera. Holding a pose for a shot means writing it every frame and handing the original view back afterwards, see
  [Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md).

## Lifecycle and timing

| Piece | Hook | Notes |
|---|---|---|
| Bridge poll, `reload_shaders`, `step`, `load`, `capture` | `StarMapAfterOnFrame` (`frame:after-on-frame`) | Same as article 1 |
| `DriveCamera()` | Inside the mod's per-frame step, the first of the GUI postfix, an `OnFrameCelestials` prefix or the frame postfix to reach it (usually `frame:draw-ui`) | Moving a borrowed camera is part of the step, before the render reads it |
| UI button "Capture for Claude" | `frame:draw-ui` sets a static bool | The next `Update` (`frame:after-on-frame`) starts the capture |
| Shader reload | Main thread, any frame | The old module is destroyed during `DoLoad`; pipelines holding it must be dropped after |

## Steps

1. **Start from the article 1 bridge** (folder `bridge/<Mod>/in|out`, `TryParse`, `Update`, `Answer`, gated by `Build.Developer`). KSAStructures is the leanest complete copy: [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs#L90-L125).

2. **Reload a mod shader.** Look the shader up by asset id, check the file exists (a missing file makes `DoLoad` destroy the old module and keep nothing), invoke `DoLoad` by reflection, compare the Vulkan module handle before and after, and tell the mod to rebuild pipelines built from it:

   ```csharp
   // src/KSAStructures/Ksa/Bridge.cs L302-L348
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

   The reply lists file size, write time, a SHA-1 prefix and `module before -> after`; a reload that answers "reloaded" while still drawing the old module is otherwise indistinguishable from one that worked.
   See also [Reload a mod shader in a running game](../../../shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md).

3. **Hold a composed camera shot.** KSAStructures' `camera` command builds a `SceneCamera` (east/north/up metres from the flown craft, aimed at the craft or another one, with its own field of view) and the mod calls `Drive()` once per step:

   ```csharp
   // src/KSAStructures/Ksa/SceneCamera.cs L45-L47
       /// <summary>Points the main view. Once a frame, with the step.</summary>
       public bool Drive() => TryLook(out double3 offset, out double3 forward, out double3 up)
                              && KsaWorld.TryLookFromMainViewport(offset, forward, up, FovDeg, this);
   ```

   Remember the original view before the first hold, release it on `release` and on unload (`Bridge.Release()` from `StarMapUnload`) so the player is not left in a pose nothing drives
   ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L193)).

4. **Or become one camera claimant among several (KSArmory).** The bridge implements the mod's `IViewPose`, so `DriveCamera()` runs in claim order next to the mod's other camera owners; an orbit rate in degrees per second of player time moves the pose without further commands:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L78-L81
       private static bool _playerAsked;

       /// <summary>Asks for a capture of what the player is looking at, with a note of the state.</summary>
       public static void RequestPlayerCapture() => _playerAsked = true;
   ```
   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L538-L548
       bool IViewPose.TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl,
                              out double3 upEcl, out double fovDeg)
       {
           fovDeg = _watch?.FovDeg ?? 30.0;
           forwardEcl = upEcl = offsetFromFollowed = Vec.Zero;
           if (!TryWatchPose(out offsetFromFollowed, out upEcl)) return false;

           forwardEcl = -offsetFromFollowed;
           return true;
       }

   ```

5. **Never start a capture inside the UI pass.** The panel's button only sets a flag; the bridge's next `Update` consumes it:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L116-L121
               if (_playerAsked)
               {
                   _playerAsked = false;
                   StartPlayerCapture();
                   return;
               }
   ```

6. **Run it.** Deploy, start the game, then drop files: `reload_shaders`, `camera {"east":0,"north":-8,"up":2,"fov_deg":50}`, `capture`. Expect `bridge: reloaded <id>: ... module <a> -> <b>` in the log, and the picture beside its manifest in `out/<id>/`.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Bridge.cs) | `Update`, `ReloadShaders`, `Camera`, `OfficeView`, `BeginStep`, `BeginLoad`, `BeginCapture` |
| [src/KSAStructures/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/SceneCamera.cs) | Composed shot as a view-pose source |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs) | Larger command set, `IViewPose` claimant, panel flag |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/ksa-mcp/server.py) | Dependency-free MCP server over the folders (see the MCP topic when written) |

## Variations and gotchas

- **Reflection on `DoLoad`.** It is not public API; a lookup that returns null must degrade to a refusal (`FileReference.DoLoad did not resolve`), never an exception. On a game update, re-check the method name and visibility.
- **Hold the pose, do not teleport the camera once.** Other code rewrites the view each frame; a single write is overwritten.
- **Mod shaders only.** Stock shaders live under `Content`, where the game's own reloader works.
- **One command at a time.** A long `step` or `capture` blocks later commands by design.
- **Captures hide the HUD.** Anything drawn with ImGui is absent from a captured image.

## See also

- [Overview](overview.md), [article 1](drive-a-running-game-through-a-file-bridge-1.md), [article 2](drive-a-running-game-through-a-file-bridge-2.md)
- [Take clean screenshots from code](../take-screenshots-from-code/overview.md), [Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md)
- [KSAStructures dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [KSArmory file command bridge and MCP](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
