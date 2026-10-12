---
type: Mod Feature
title: Drive a running game from outside through a file command bridge and an MCP server
description: A developer-only bridge polls a folder beside the mod log for JSON commands (pause, step, burst, camera, capture, shader reload, spawn, fly), answers each atomically in a second folder, and a stdlib Python MCP server turns that folder into tools that return captures inline to an agent
mod: ksarmory
category: testing-and-tooling
techniques: [file-ipc, mcp-server, reflection, shader-hot-reload, screenshot-capture]
hooks:
  - starmap StarMapAfterOnFrame
  - reflection KSA.Program._screenshotCapture
  - reflection KSA.FileReference.DoLoad
lifecycle: [frame:after-on-frame]
game_types:
  - KSA.ScreenshotCapture
  - KSA.Program
  - KSA.FileReference
  - KSA.ShaderReference
  - KSA.ModLibrary
  - KSA.Constants
tags: [bridge, mcp, automation, screenshots, hot-reload, visual-testing, agents]
---

To judge visual effects and engagements without a human at the keyboard, KSArmory's developer
installs run a command bridge: an external process drops `<id>.json` files into
`<KSA user dir>/Logs/bridge/in`, the mod executes them one at a time inside the game loop and
writes `<id>.json` replies into `.../bridge/out`. A 540-line, standard-library-only Python MCP
server (`tools/ksa-mcp/server.py`, registered in `.mcp.json`) wraps that folder as MCP tools and
returns screenshots inline, so a coding agent can pause the world, step it a fixed number of
simulated seconds, set off a burst, move the camera, capture frames, hot-reload the compute
shaders and read the results in one conversation.[^bridge][^mcp-server]

## How it works

| Piece | Mechanism |
|---|---|
| Transport | Files, not a socket: the mod's only network egress is the player's Send button (enforced by `check-network.sh`), and a folder works across the WSL/Windows boundary unchanged[^bridge] |
| Polling | `Bridge.Update` lists the inbox every 0.1 s of player time, takes the oldest file by ordinal name, deletes it and parses it with `BridgeCommand.TryParse` (a `Sim/` type, so parsing is unit-tested)[^bridge][^bridge-command] |
| Dispatch | A `switch` on the command name: `status`, `pause`, `resume`, `speed`, `set`/`get`/`tune` (live settings), `burst`, `dents`, `camera`, `cost`, `site`, `step`, `capture`, `load`, `system`, `optic`, `dispense`, `fly`, `watch`, `fire`, `spawn`, `ground`, `save`, `control`, `remove`, and a shader reload[^bridge] |
| Multi-frame commands | A command that needs frames (`step`, `capture`, `load`) installs a `Func<double, double, Reply?>` in `_running`; it is called every frame with player and simulated dt until it returns a reply, holding the queue so a sequence of files runs as a script[^bridge] |
| Replies | Written to `<id>.json.part` then `File.Move`d over the final name, so the reader never sees half a reply[^bridge] |
| Screenshots | `KsaWorld.TryRequestScreenshot` reads the private `Program._screenshotCapture` by reflection once and calls the public `ScreenshotCapture.Request(scale, flags)`; capture bundles copy the images out of `KSA.Constants.DocumentsFolderPath/exports/screenshots` with a JSON manifest of state[^ksaworld][^ksa-screenshot][^ksa-constants] |
| Shader hot reload | KSA's own `ShaderReloader.GetModPath` maps a path by searching for `"Content"`, which a mod folder under `Documents/My Games` does not contain; the bridge instead looks each of its shaders up with `ModLibrary.TryGet<ShaderReference>(id, ...)`, checks the file exists (a missing file makes the reload destroy the old module), invokes the internal `FileReference.DoLoad` by reflection, compares the `VkHandle` before and after, and calls `CloudPass.Release()` so pipelines rebuild[^bridge][^ksa-shaderreloader][^ksa-filereference][^ksa-shaderreference][^ksa-modlibrary] |
| Camera | The bridge implements the mod's `IViewPose` and can hold the main view at a pose (optionally orbiting on player time), handing the original view back afterwards[^bridge] |
| Panel hook | "Capture for Claude" in the panel only sets a static flag; the next `Update` starts the capture, because a capture must not start inside the UI pass[^bridge] |
| Agent side | `server.py` speaks JSON-RPC over stdio, finds the user directory via `tools/ksa-user-dir.sh`, writes commands, waits for replies, launches a game only if none is running, and imports `tools/vis/vis.py` for crops, same-instant diffs, noise maps and contact sheets; `cli <tool>` runs one tool from a shell[^mcp-server][^vis][^mcp-json] |

[docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/VISUAL-TESTING.md)
records why this was built (the cost of the screenshot loop) and what each tool is for; the
`verify-in-game` skill says how to use it as evidence before calling a change a fix.[^visual-testing][^verify-skill]

## Lifecycle and timing

- **Created at `startup:all-mods-loaded`** only when `Build.Developer` is true (a `developer` file
  beside the DLL), in `KSArmoryMod.OnFullyLoaded`.[^entry]
- **Runs at `frame:after-on-frame`.** `KSArmoryMod.OnAfterFrame` (the `[StarMapAfterOnFrame]`
  postfix on `Program.OnFrame`) calls `StepOnce` and then `_bridge?.Update(dtPlayer, simStep)`, so
  a command sees this frame's world and its simulated step, after `PostRender` and
  `FrameNumber++` (Program.cs L2332).[^entry][^ksa-program]
- **Why not the GUI hook.** `[StarMapAfterGui]` is inside `if (DrawUI)` (Program.cs L2265-L2269), and
  `ScreenshotCapture.OnPrepareFrame` sets `Program.DrawUI = false` while a hi-res capture runs
  (ScreenshotCapture.cs L228-L231). A bridge on the GUI hook would stop the moment it asked for a
  screenshot and would not run with the HUD hidden; the frame postfix runs on every frame that
  is not cut short by an early return, including the menu.[^ksa-screenshot][^bridge]
- **Camera driving** is the exception: `DriveCamera()` is called from `DriveCameras` inside
  `StepOnce`, alongside the mod's other camera owners, so the held pose is written in claim order.[^entry]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L80-L135) | Poll, parse, run one command at a time |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1124-L1170) | Shader hot reload through `FileReference.DoLoad` |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1241-L1300) | Capture series in simulated time |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1183-L1215) | `TryRequestScreenshot` |
| [src/KSArmory/Sim/BridgeCommand.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BridgeCommand.cs) | Command parsing, testable headlessly |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-mcp/server.py) | MCP server over the folder |

## Snippets

```csharp
// src/KSArmory/Ksa/Bridge.cs L1125-L1143
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
```

```csharp
// src/KSArmory/Ksa/Bridge.cs L276-L280
        // Written aside and moved, so the other end never reads half a reply.
        string final = Path.Combine(Outbox, command.Id + ".json");
        string partial = final + ".part";
        File.WriteAllText(partial, JsonSerializer.Serialize(body, JsonOptions));
        File.Move(partial, final, overwrite: true);
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L1183-L1206
    public static bool TryRequestScreenshot(int scale = 1, string flags = "")
    {
        try
        {
            if (!_lookedForShots)
            {
                _lookedForShots = true;
                _shots = typeof(Program)
                         .GetField("_screenshotCapture", BindingFlags.NonPublic | BindingFlags.Instance)
                         ?.GetValue(Program.Instance) as ScreenshotCapture;

                if (_shots is null && !_warnedAboutShots)
                {
                    _warnedAboutShots = true;
                    Log.Warn("screenshots: Program._screenshotCapture did not resolve; "
                             + "scripted captures will write nothing");
                }
            }

            if (_shots is null) return false;

            // A capture already running is dropped by the engine with its own warning, so asking
            // again while one is in flight costs nothing and loses only that frame.
            _shots.Request(scale, flags);
```

## Game APIs touched

| Member | Use |
|---|---|
| `Program._screenshotCapture` (private field, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L175)), `Program.Instance` | Reached by reflection to request captures |
| `ScreenshotCapture.Request(int, string)` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L92)) | Public request; the capture itself runs in later frames and hides the UI |
| `FileReference.DoLoad()` (internal virtual, [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L166)), `FileReference.ModPath` | Recompile a mod shader in place |
| `ShaderReference` ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs)) | The mod's `<Shader>` assets; `Shader.VkHandle` shows whether the module changed |
| `ShaderReloader.GetModPath` ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54)) | Why the stock hot reloader cannot see a mod's shaders |
| `ModLibrary.TryGet<T>(string, out T)` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L776)) | Asset lookup by Id |
| `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) | Where KSA writes screenshots |

## Pitfalls and version notes

- **Developer installs only.** The bridge is not constructed in a player install, so a stray
  folder of commands can do nothing there.[^entry]
- **Everything here is reflection or internal.** `_screenshotCapture` and `DoLoad` are not public
  and are not in the API-surface record; each lookup degrades to a logged "did not resolve"
  rather than an exception.[^ksaworld][^bridge]
- **A missing shader file destroys the module.** `DoLoad` on a path that does not exist leaves
  nothing compiled, so existence is checked before the call.[^bridge]
- **Captures hide the HUD.** Anything drawn with ImGui is absent from a capture, which is one
  reason the mod draws its flashes and rings into the scene image instead.
- **One command at a time.** A long `step` or `capture` blocks later commands by design; an agent
  that wants concurrency must not expect it.

## See also

- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Borrow the main view camera](./borrow-main-view-camera.md)
- [Shot-night statistics](./shot-night-statistics.md)
- [Rank several camera users with a pure ViewClaim ladder and drive the claimants in claim order](../../../howto/camera/main-view/borrow-the-main-camera-and-hand-it-back/borrow-the-main-camera-and-hand-it-back-5.md) - how-to article demonstrating this feature
- [Stdlib Python stdio MCP server wrapping the file bridge, with image tools](../../../howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-2.md) - how-to article demonstrating this feature
- [Reload a mod shader: a reload command in a developer file bridge that an agent can drive](../../../howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/reload-a-mod-shader-in-a-running-game-2.md) - how-to article demonstrating this feature
- [A file-drop bridge with in/ and out/ folders, atomic replies and multi-frame closures](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md) - how-to article demonstrating this feature
- [A lean bridge with shader hot reload, held camera shots and a UI flag instead of work in the UI pass](../../../howto/testing-and-tooling/in-game-testing/drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-3.md) - how-to article demonstrating this feature
- [Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-1.md) - how-to article demonstrating this feature
- [Hold the main view from the bridge as one camera claimant, optionally orbiting on player time](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-4.md) - how-to article demonstrating this feature
- [A capture button that only sets a flag, with player bundles of frames, state and log tail](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-5.md) - how-to article demonstrating this feature
- [Same-instant variant diffs, frame series and temporal noise maps with a vis.py toolbox](../../../howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-1.md) - how-to article demonstrating this feature
- [Return contact sheets, GIFs and temporal-noise maps inline through an MCP server](../../../howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-2.md) - how-to article demonstrating this feature

[^bridge]: src/KSArmory/Ksa/Bridge.cs
[^bridge-command]: src/KSArmory/Sim/BridgeCommand.cs
[^entry]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^mcp-server]: tools/ksa-mcp/server.py
[^mcp-json]: .mcp.json
[^vis]: tools/vis/vis.py
[^visual-testing]: docs/VISUAL-TESTING.md
[^verify-skill]: .claude/skills/verify-in-game/SKILL.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-screenshot]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-shaderreloader]: KSA.AssetReloader.ShaderReloader (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
