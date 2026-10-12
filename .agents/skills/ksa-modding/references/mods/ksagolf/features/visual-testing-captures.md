---
type: Mod Feature
title: Visual testing captures
description: Scripted screenshots through KSA's own ScreenshotCapture reached by reflection, with a UI-free warm-up, per-picture JSON manifests carrying the craft's projected screen position, simulated-time series, same-instant variant diffs and a temporal noise map
mod: ksagolf
category: testing-and-tooling
techniques: [reflection, starmap-lifecycle, file-ipc]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.Program._screenshotCapture]
lifecycle: [frame:after-on-frame, frame:prepare, frame:post-render, thread:background]
game_types: [KSA.ScreenshotCapture, KSA.Program, KSA.Camera, KSA.Constants]
tags: [screenshots, visual-testing, manifest, image-diff, temporal-noise, camera, agent-tooling]
---

KSAGolf's way of letting a developer (or an agent) see what the mod draws without sitting at the
game. The bridge's `capture` command asks KSA's own screenshot system for a clean frame, waits for
the PNG, renames it into a per-command folder, and writes a JSON manifest beside it recording the
wall time, pause state, simulation speed, field of view and where the flown craft falls on screen.
Series can be spaced in simulated seconds or rendered frames; the MCP server adds same-instant
variant diffs, contact sheets, animations and a per-pixel temporal standard-deviation map. A
"Capture for Claude" button gives players the same bundle plus the log tail.

Nothing is uploaded: everything lands in `<user dir>/Logs/bridge/out/`.[^golf-visual-testing]

## How it works

| Step | Mechanism |
|---|---|
| Reach the capturer | `Program._screenshotCapture` is a private instance field; `KsaWorld.TryRequestScreenshot` reads it once by reflection and calls the public `ScreenshotCapture.Request(scale, flags)`, the same call the game's `screenshot` console command makes[^golf-ksaworld][^ksa-program] |
| Clean frame | `flags = "warm=24"`: KSA hides the UI (`Program.DrawUI = false`) and waits 24 frames before capturing, so temporally blended effects (clouds) have no window-shaped holes; `ui` and `hud` flags are opt-in[^ksa-screenshotcapture] |
| Manifest | Written when the mod's own 24-frame countdown ends, so it describes the captured instant; `craft_screen` is the flown craft projected with `camera.EgoToClipDouble(posEcl - camera.PositionEcl)` into 0..1 screen fractions[^golf-bridge][^ksa-camera] |
| Pick up | Poll `Constants.DocumentsFolderPath/exports/screenshots` for the newest `ksa_*.png` written after the request, `File.Move` it to `out/<id>/<NN>-<label>.png`, retry on `IOException` while it is still being written[^ksa-constants] |
| Series | `frames` up to 120, `every_s` simulated seconds or `every_frames`; when paused with `every_s`, the world is unpaused for exactly that much simulated time, paused, allowed three frames to settle, then captured |
| Composed shots | The `frame` command holds the main view on a `SceneCamera` (offset east/north/up of the flown craft or in its own body frame, aimed at a craft or a body, at a chosen FOV) driven each step through the mod's camera controller[^golf-scenecamera] |
| Player capture | Panel button (developer installs): `note.json` with status and every `Config` field, `log-tail.txt` (last 300 lines, opened with `FileShare.ReadWrite`), then eight consecutive frames[^golf-bridge][^golf-ui] |
| Judge | `tools/vis/vis.py`: crop around `craft_screen`, `diff` (amplified per-pixel max channel delta with stats), `temporal` (per-pixel standard deviation over a series), `grain` (high-pass energy), `sheet`, `gif`[^golf-vis] |
| Variants | MCP-side: pause, capture a base, `set` a `Config` field, capture, restore, diff against base; same paused instant means the diff is the change and nothing else[^golf-mcp-server] |
| Fallback | `tools/screenshot.sh` grabs the whole Windows primary screen via PowerShell, but refuses unless StarMap or KSA is the foreground process[^golf-screenshot-sh] |

## Lifecycle and timing

| Phase | What happens |
|---|---|
| `frame:after-on-frame` | The bridge's capture delegate runs in the `StarMapAfterOnFrame` postfix and calls `Request` |
| `frame:prepare` (next frame) | `ScreenshotCapture.OnPrepareFrame` (near the top of `PrepareFrame`, after any renderer rebuild) hides the UI and counts the warm-up down[^ksa-screenshotcapture][^ksa-program] |
| `frame:render` | `OnRenderGameCapture` / `OnRenderGameSwapchainGrab` record the copy |
| `frame:post-render` | `OnPostRender` reads the staging buffer, restores `DrawUI`, and starts `SaveToPng` on `Task.Run` (`thread:background`) |
| Next postfixes | The mod polls for the file each frame (10 s timeout) |

Because the capture hides the UI, the mod's `StarMapBeforeGui`/`StarMapAfterGui` hooks do not run
during those frames; the mod's simulation keeps stepping through its fallback hooks (see
[Dual-hook frame stepping](dual-hook-frame-stepping.md)) and its sim-step accounting closes the gap.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L582-L617) | `TryRequestScreenshot` via reflection |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L657-L814) | `BeginCapture`, manifests, screen projection |
| [src/KSAGolf/Ksa/SceneCamera.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/SceneCamera.cs) | Held camera pose for composed shots |
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/vis/vis.py) | Crops, diffs, temporal map, sheets |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-mcp/server.py#L147-L210) | Series and variants returned inline |
| [tools/screenshot.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/screenshot.sh) | Foreground-guarded desktop grab |

## Snippets

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L582-L606
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
            return true;
```

```csharp
// src/KSAGolf/Ksa/Bridge.cs L735-L759
            if (manifest is null && --warming <= 0) manifest = Manifest(label, index);

            waited += dtPlayer;
            if (waited > 10.0) return Failed($"screenshot {index} never arrived");

            string? file = Directory.Exists(shots)
                               ? new DirectoryInfo(shots).GetFiles("ksa_*.png")
                                                         .Where(f => f.LastWriteTimeUtc >= askedAt && f.Length > 0)
                                                         .OrderByDescending(f => f.LastWriteTimeUtc)
                                                         .FirstOrDefault()?.FullName
                               : null;
            if (file is null) return null;

            string name = $"{index:D2}-{label}";
            string target = Path.Combine(folder, name + ".png");

            try
            {
                File.Move(file, target, overwrite: true);
            }
            catch (IOException)
            {
                // Still being written; the next frame will find it finished.
                return null;
            }
```

```csharp
// src/KSAGolf/Ksa/Bridge.cs L805-L813
    private static double[]? Screen(double3 pointEcl)
    {
        if (Program.GetMainCamera() is not { } camera) return null;

        double4 clip = camera.EgoToClipDouble(pointEcl - camera.PositionEcl);
        if (!(clip.W > 1e-6)) return null;

        return [Math.Round((clip.X / clip.W * 0.5) + 0.5, 4), Math.Round((clip.Y / clip.W * 0.5) + 0.5, 4)];
    }
```

```python
# tools/vis/vis.py L73-L84
def temporal(images: list[Image.Image], gain: float = 8.0) -> tuple[Image.Image, dict]:
    """How much each pixel moves across a series. With the world paused, anything bright here is
    the renderer's own noise: flicker, crawl, a history that will not settle."""
    stack = np.stack([np.asarray(i.convert("L"), dtype=np.float32) for i in images])
    sd = stack.std(axis=0)
    stats = {
        "frames": len(images),
        "mean_sd": round(float(sd.mean()), 3),
        "p99_sd": round(float(np.percentile(sd, 99)), 3),
        "px_sd_over_4": int((sd > 4).sum()),
    }
    return Image.fromarray(np.clip(sd * gain, 0, 255).astype(np.uint8)).convert("RGB"), stats
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Program._screenshotCapture` (private field) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | The engine's capturer instance |
| `ScreenshotCapture.Request(int, string)`, `OnPrepareFrame`, `OnPostRender` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs) | Flags `ui`, `hud`, `warm=N` (clamped 0..600); file `ksa_<yyyyMMdd_HHmmss>_<w>x<h>.png` |
| `Camera.EgoToClipDouble`, `Camera.PositionEcl`, `Program.GetMainCamera` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs) | Projecting the craft into the picture |
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs) | Where `exports/screenshots` lives |

## Pitfalls and version notes

- KSA names screenshots to the second, so two captures in one second would share a file name; the
  mod moves each file away before requesting the next.[^ksa-screenshotcapture]
- `Request` refuses (with a log warning) while a capture is in flight or in the editor, and returns
  nothing to say so; the mod's 10 s timeout is what notices.[^ksa-screenshotcapture]
- The PNG is written on a background task after the frame, so a reader polling the folder can see
  a partial file; the mod filters `Length > 0` and retries the move on `IOException`.
- A paused series cannot show flicker that needs motion; the repo's guidance is to measure flicker
  at a crawl (slow simulation speed), not paused.[^golf-visual-testing]
- There is no headless rendering: every picture needs a running game, and C# changes still need a
  relaunch.[^golf-visual-testing]

## See also

- [File command bridge and MCP server](file-command-bridge.md)
- [Borrowing the main view and handing it back](borrowed-chase-camera.md)
- [Level-horizon camera controller swapped into the main viewport](level-horizon-camera-controller.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-1.md) - how-to article demonstrating this feature
- [Compose a shot with a held SceneCamera, a 24-frame countdown and a manifest of the craft's screen position](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-2.md) - how-to article demonstrating this feature
- [A capture button that only sets a flag, with player bundles of frames, state and log tail](../../../howto/testing-and-tooling/in-game-testing/take-screenshots-from-code/take-screenshots-from-code-5.md) - how-to article demonstrating this feature
- [Same-instant variant diffs, frame series and temporal noise maps with a vis.py toolbox](../../../howto/testing-and-tooling/in-game-testing/visual-regression-for-mods/visual-regression-for-mods-1.md) - how-to article demonstrating this feature

[^golf-bridge]: src/KSAGolf/Ksa/Bridge.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-scenecamera]: src/KSAGolf/Ksa/SceneCamera.cs
[^golf-vis]: tools/vis/vis.py
[^golf-mcp-server]: tools/ksa-mcp/server.py
[^golf-screenshot-sh]: tools/screenshot.sh
[^golf-visual-testing]: docs/VISUAL-TESTING.md
[^golf-ui]: src/KSAGolf/Ksa/Ui/Ui.cs
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
