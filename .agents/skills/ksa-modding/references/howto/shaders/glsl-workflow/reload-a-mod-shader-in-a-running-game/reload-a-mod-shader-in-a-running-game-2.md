---
type: How-To
title: "Reload a mod shader: a reload command in a developer file bridge that an agent can drive"
description: Expose the reflective shader reload as a bridge command for a list of shaders so an agent or script can edit GLSL, reload it and read the result in a captured frame
category: shaders
group: glsl-workflow
topic: reload-a-mod-shader-in-a-running-game
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/file-command-bridge-and-mcp
  - mods/ksarmory/features/core-shader-library-include
techniques: [reflection]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.FileReference.DoLoad]
lifecycle: [frame:after-on-frame, thread:background]
game_types: [KSA.FileReference, KSA.ShaderReference, KSA.AssetReloader.ShaderReloader, KSA.ModLibrary]
tags: [hot-reload, shader, reflection, file-bridge, mcp, agent, screenshot, ksarmory]
---
KSArmory has seven compute shaders and a visual result that is hard to judge from a log. Its developer builds run a file-based command bridge: an external process drops JSON commands into a folder, the mod executes them in the game loop and writes replies. One command reloads every shader; another captures frames. A small MCP server wraps the folder so a coding agent can edit GLSL, reload it and look at the
screenshot in one conversation. This article shows how the reload command fits into that loop, for a mod with several shaders; the mechanism itself is the previous article.

## Background: how the game does it

The same facts as the first article: the stock `ShaderReloader` cannot reach a mod's shader (nothing fills its path-to-id map and `GetModPath` keys on the text after `Content`), `FileReference.DoLoad` is `internal virtual` and compiles the shader file, and pipelines already built keep the old module until released. Screenshots are requested through the private
`Program._screenshotCapture` by reflection once and the public `ScreenshotCapture.Request(scale, flags)`; a capture hides the HUD (it sets `Program.DrawUI = false` while a high-resolution capture runs), so anything drawn with ImGui is absent from captures.

## Lifecycle and timing

- **Created** at `startup:all-mods-loaded` only when a `developer` file sits beside the DLL (`Build.Developer`), in `KSArmoryMod.OnFullyLoaded`; a player install never constructs it.
- **Runs** at `frame:after-on-frame`: `KSArmoryMod.OnAfterFrame` (the `[StarMapAfterOnFrame]` postfix on `Program.OnFrame`) calls `_bridge?.Update(dtPlayer, simStep)` ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L268-L292)). It does not use the GUI hook: `[StarMapAfterGui]` is inside `if (DrawUI)`, and a hi-res capture sets `DrawUI` false, so a bridge there would stop the moment it asked for a screenshot and would not run with the HUD hidden.
- **Polling** is every 0.1 s of player time, one command at a time; multi-frame commands (`step`, `capture`, `load`) hold the queue so a sequence of files runs as a script. The reload is a single-frame command. The agent side runs in its own process (`thread:background` from the game's point of view). See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **List your shaders by asset id.** The command reloads exactly these:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L44-L46
       private static readonly string[] ShaderIds =
           ["KSArmoryCloudCompute", "KSArmoryCloudResolveCompute", "KSArmoryShockCompute",
            "KSArmoryFireLightCompute", "KSArmoryRingCompute", "KSArmoryHoleCompute", "KSArmoryLeakCompute"];
   ```

   Source: [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L44-L46). Each id is the `Id` of a `<Shader>` asset in the mod's XML.

2. **Implement the reload exactly as in the first article:** resolve `DoLoad` once, `ModLibrary.TryGet<ShaderReference>(id, ...)`, check `File.Exists(shader.ModPath)`, read `shader.Shader?.VkHandle` before and after, hash the file, unwrap `TargetInvocationException`:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L1129-L1143
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
   ```

   Source: [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1124-L1172). After the loop it releases the pass: `CloudPass.Release();` so the pipelines rebuild against the new modules, and replies with the list of reloaded ids and their module lines.

3. **Return a line per shader that proves what happened.** The reply carries `module <before> -> <after>` and the file's size, write time and SHA-1, because "reloaded" with the old shader still drawing is otherwise indistinguishable from success:

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L1160-L1172
               nint after = shader.Shader?.VkHandle ?? 0;
               string sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(shader.ModPath))).ToLowerInvariant();
               string line = $"{id}: {shader.ModPath}, {file.Length} bytes written {file.LastWriteTime:HH:mm:ss}, "
                             + $"sha1 {sha1[..12]}, module {before:X} -> {after:X}";
               seen.Add(line);
               Log.Info($"bridge: reloaded {line}");

               reloaded.Add(id);
           }

           CloudPass.Release();
           return Done(new() { ["reloaded"] = reloaded, ["modules"] = seen });
       }
   ```

   Source: [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1160-L1172).

4. **Write replies atomically so the other end never reads half a reply:**

   ```csharp
   // src/KSArmory/Ksa/Bridge.cs L276-L280
           // Written aside and moved, so the other end never reads half a reply.
           string final = Path.Combine(Outbox, command.Id + ".json");
           string partial = final + ".part";
           File.WriteAllText(partial, JsonSerializer.Serialize(body, JsonOptions));
           File.Move(partial, final, overwrite: true);
   ```

   Source: [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L276-L280). The inbox and outbox are `<KSA user dir>/Logs/bridge/in` and `out`.

5. **Close the loop from the agent side, and verify what the game compiled.** The MCP server (`tools/ksa-mcp/server.py`, about 540 lines, standard library only, registered in `.mcp.json`) wraps the folder as tools. Its `reload_shaders` tool copies the `.comp` files from the repository into the installed mod, fsyncs them, and repeats the in-game reload until the reply's size and SHA-1 per file match the tree,
   because a write from WSL has been seen to reach the game late, so the game compiled the old file and reported "reloaded":

   ```python
   # tools/ksa-mcp/server.py L269-L295
   def reload_shaders() -> str:
       """Copies the shaders from the tree into the installed mod and recompiles them in the game.

       A write from WSL reaches the game late: flushed or not, the game has been seen compiling the file
       as it was before the copy, and reporting "reloaded". So the reply's size for each file is checked
       against the tree's, and the reload is repeated until the game compiled what the tree holds."""
       src = REPO / "src" / "KSArmory" / "Shaders"
       dst = user_dir() / "mods" / "KSArmory" / "Shaders"
       want = {}
       for f in src.glob("*.comp"):
           shutil.copy2(f, dst / f.name)
           with open(dst / f.name, "rb+") as out:
               os.fsync(out.fileno())
           want[f.name] = (f.stat().st_size, hashlib.sha1(f.read_bytes()).hexdigest()[:12])

       for attempt in range(8):
           data = send("reload_shaders", timeout=60)
           seen = " ".join(data.get("modules") or [])
           # By content where the game reports it, and by size where it does not.
           stale = [name for name, (size, sha1) in want.items()
                    if name in seen and f"{name}, {size} bytes" not in seen
                    or (name in seen and "sha1 " in seen and sha1 not in seen)]
           if not stale:
               return f"reloaded {data.get('reloaded')} as the tree has them" + (f" (attempt {attempt + 1})" if attempt else "")
           time.sleep(0.5)

       raise Refused(f"the game kept compiling an older {', '.join(stale)} than the tree's")
   ```

   Source: [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-mcp/server.py#L269-L295). After a successful reload the agent calls the capture tool and receives the screenshot inline. The full tool surface and how to drive a running game this way are in [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md) and [Let AI agents drive the game through an MCP server](../../../external-io/control/let-ai-agents-control-the-game-over-mcp/overview.md).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs) | Poll, dispatch, `ReloadShaders`, capture series |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Creates the bridge and calls `Update` each frame |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-mcp/server.py) | The agent-side MCP server |

## Variations and gotchas

- **Developer installs only.** Everything here is reflection or internal, and each lookup degrades to a logged "did not resolve" rather than an exception.
- **A bad compile fails the whole command** at the first broken shader (`<id> did not compile: ...`); earlier shaders in the list are already reloaded, so fix and resend.
- **One command at a time.** A long `step` or `capture` blocks later commands by design.
- **The offline check is the fast first gate.** Compile with glslang before reloading ([Compile-check a mod's GLSL without launching the game](../check-mod-shaders-offline-with-glslang/overview.md)); the reload then tests only game-specific behaviour.
- **Captures hide the HUD,** one reason the mod draws its flashes and rings into the scene image instead of with ImGui.

## See also

- Sibling: [minimal reload command](reload-a-mod-shader-in-a-running-game-1.md); [overview](overview.md)
- [Drive a running game from outside through a file command bridge and an MCP server](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md), [A generated include that reaches KSA's own shader library from a mod shader](../../../../mods/ksarmory/features/core-shader-library-include.md)
- [Drive a running game from scripts through a file-drop command bridge](../../../testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/overview.md), [Let AI agents drive the game through an MCP server](../../../external-io/control/let-ai-agents-control-the-game-over-mcp/overview.md), [Patch vehicle paint and canopy shaders in memory at compile time](../../stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md)
