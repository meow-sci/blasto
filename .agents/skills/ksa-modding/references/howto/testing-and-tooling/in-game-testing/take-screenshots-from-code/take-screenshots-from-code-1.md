---
type: How-To
title: Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest
description: Request a UI-free screenshot from the frame postfix, find the file KSA writes, move it into a per-command folder and write a JSON manifest of the instant it shows
category: testing-and-tooling
group: in-game-testing
topic: take-screenshots-from-code
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/visual-testing-captures
  - mods/ksacars/features/file-drop-command-bridge
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
techniques: [reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, reflection KSA.Program._screenshotCapture]
lifecycle: [frame:after-on-frame, frame:prepare, frame:post-render, thread:background]
game_types: [KSA.ScreenshotCapture, KSA.Program, KSA.Camera, KSA.Constants]
tags: [screenshots, capture, manifest, reflection, warm-up]
---

Outcome: from a bridge command (or any frame hook) you get `ksa_*.png` renamed into `out/<id>/00-<label>.png` with `00-<label>.json` beside it
recording wall time, pause state, simulation speed, field of view and where the flown craft falls on screen, without the HUD in the picture and
without the cloud and temporal-effect artefacts a one-frame capture shows under windows. A series can be spaced in simulated seconds or frames.
Prerequisites: StarMap and a frame hook, ideally the [bridge from this topic's sibling](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md).
Demonstrated by KSAGolf ([visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md)); KSACars, KSAStructures and KSArmory carry the same code.

## Background: how the game does it

The game's capture system is `ScreenshotCapture` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L19)). Its public `Request(int scale, string flags)` is what the stock `Screenshot` key
and the `screenshot` terminal action call; the instance lives in the private `Program._screenshotCapture` field ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L175)). The details that matter for a script:

- `Request` is a state-machine arm, not a capture: it returns at once and the capture happens over the next frames (`Idle` to `PendingHiRes` to `Warming` to `CaptureFrame`).
  It logs a warning and does nothing if one is already running or the editor is open ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L92-L103)).
- Flags: `ui`, `hud`, `warm=N` (0..600). Without `ui`, `OnPrepareFrame` forces `Program.DrawUI = false` until the picture is taken ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L226-L233)).
- The image is written on a background task: `OnPostRender` waits for the GPU (`Device.WaitIdle()`), copies the pixels, restores `DrawUI`, then `Task.Run(SaveToPng)`
  ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L393-L433)). The PNG appears *after* the frame, and the name is `ksa_<yyyyMMdd_HHmmss>_<w>x<h>.png` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L597)).
- The folder is `Constants.DocumentsFolderPath/exports/screenshots` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L80)).

## Lifecycle and timing

| Moment | Code | Why it matters |
|---|---|---|
| Request | Your closure, `StarMapAfterOnFrame` (`frame:after-on-frame`) | Never from the UI pass: `StarMapAfterGui` is inside `if (DrawUI)` and stops firing once the capture hides the UI ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)) |
| Warm-up and UI hide | `OnPrepareFrame` in `PrepareFrame` (`frame:prepare`), counts `warm` frames | Temporal effects blended over frames (clouds) need frames with the windows hidden to forget them |
| Copy | `OnRenderGameCapture` in `RenderGame` (`frame:render`) | Composite into a private image, then to a staging buffer |
| Read and encode | `OnPostRender` (`frame:post-render`), `SaveToPng` on `Task.Run` (`thread:background`) | The file shows up a little later; poll for it |

The mod's hooks that live inside the UI pass do not run for the warm-up frames. KSAGolf's simulation therefore steps from fallback hooks and closes the gap with its sim-step accounting;
the bridge itself is unaffected because it is on the always-called postfix. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Reach the capturer once.** Reflect the private field, cache it (including the failure), and warn once so a game update that renames the field turns captures off instead of throwing. Nothing but a picture is lost:

   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L582-L617
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
           }
           catch (Exception e)
           {
               if (!_warnedAboutShots)
               {
                   _warnedAboutShots = true;
                   Log.Warn($"screenshots: could not ask for one: {e.Message}");
               }
               return false;
           }
       }
   ```

2. **Ask with a warm-up.** The bridge's capture closure asks with `flags: "warm=24"`, remembers the time of the request, and starts its own countdown of the same length:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L716-L733
                   askedAt = DateTime.UtcNow.AddSeconds(-0.5);

                   // WITH THE UI HIDDEN FOR A MOMENT FIRST. The cloud's history is blended over frames,
                   // and under the game's windows it holds no cloud -- so a screenshot, which hides the
                   // UI for the one frame it takes, showed each window as a dark grainy rectangle on the
                   // cloud. KSA's own warm-up hides it for these frames first; the manifest is written on
                   // the frame the picture is actually taken, so it still describes that instant.
                   if (!KsaWorld.TryRequestScreenshot(flags: $"warm={WarmFrames}"))
                   {
                       return Failed("KSA would not take a screenshot");
                   }

                   manifest = null;
                   warming = WarmFrames;
                   waiting = true;
                   waited = 0.0;
                   return null;
               }
   ```

   Why 24: the mod's note says the cloud history keeps about an eighth of each frame, so after 24 frames under a twentieth of what the windows left is still in it ([src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L777-L779)).

3. **Wait for the file and move it aside.** Poll the screenshots folder each frame for a `ksa_*.png` newer than the request and non-empty, move it to the command's folder (names collide within a second, so move before the next request), and retry on `IOException` while the encoder still holds it. Give up after 10 s of player time:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L735-L770
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

               manifest ??= Manifest(label, index);
               manifest["file"] = target;
               File.WriteAllText(Path.Combine(folder, name + ".json"), JsonSerializer.Serialize(manifest, JsonOptions));
               taken.Add(manifest);

               index++;
               waiting = false;
               sinceSim = 0.0;
               sinceFrames = 0;

   ```

4. **Write the manifest on the frame the picture is taken**, so it describes that instant, not the request. `craft_screen` is the flown craft projected through the same matrix the frame was drawn with (fractions from the top left, `null` when behind the camera), which lets a Python tool crop around it:

   ```csharp
   // src/KSAGolf/Ksa/Bridge.cs L782-L813
       private static Dictionary<string, object?> Manifest(string label, int index)
       {
           Dictionary<string, object?> m = new()
           {
               ["label"] = label,
               ["index"] = index,
               ["wall"] = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
               ["paused"] = KsaWorld.IsPaused,
               ["speed"] = KsaWorld.SimulationSpeed,
               ["fov_deg"] = Math.Round(KsaWorld.MainViewFovDeg(), 2),
           };

           if (KsaWorld.ControlledVehicle is { } craft)
           {
               m["craft"] = KsaWorld.DisplayName(craft);
               m["craft_screen"] = Screen(KsaWorld.PositionEcl(craft));
           }

           return m;
       }

       // Where a point falls in the picture, as fractions from the top left, or null when it is behind
       // the camera. The same matrix the frame was drawn with, so a crop taken off it is exact.
       private static double[]? Screen(double3 pointEcl)
       {
           if (Program.GetMainCamera() is not { } camera) return null;

           double4 clip = camera.EgoToClipDouble(pointEcl - camera.PositionEcl);
           if (!(clip.W > 1e-6)) return null;

           return [Math.Round((clip.X / clip.W * 0.5) + 0.5, 4), Math.Round((clip.Y / clip.W * 0.5) + 0.5, 4)];
       }
   ```

5. **Series.** `frames` up to 120 with `every_s` (simulated seconds) or `every_frames`. When the world is paused and `every_s` is given, the closure unpauses for exactly that much simulated time, pauses again, waits three settle frames, then captures ([src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs#L657-L714)). The ages are exactly the ones asked for however long a command takes to arrive.

6. **Check.** Send `{"id":"1","cmd":"capture","label":"flash","frames":3,"every_s":0.5}` through the bridge. Expect `out/1/00-flash.png`, `01-flash.png`, `02-flash.png`, JSON manifests beside them, and a reply listing the frames. A missing reply after ten seconds is `screenshot 0 never arrived`: look in the log for `Screenshot: a capture is already in progress` or `unknown flag`.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs) | `TryRequestScreenshot` (reflection, cached) |
| [src/KSAGolf/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Bridge.cs) | `BeginCapture`, `Manifest`, `Screen` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs) | The same helper in KSACars (lines 391-441 per its feature page) |
| [tools/screenshot.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/screenshot.sh) | Fallback: grabs the whole Windows desktop through PowerShell |

## Variations and gotchas

- **Scale above 1.** `Request(4, "warm=24")` sets `Renderer.ExtentOverride` ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L42)), rebuilds the renderer for the capture ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L237-L239)) and schedules another rebuild after it ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L417-L420)); any mod render target sized from the swapchain is recreated twice
  ([Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)). The engine reduces the scale itself when the image would exceed device limits or free memory, and logs it.
- **`ui` includes ImGui, `hud` keeps the HUD.** Default (neither) is the scene alone, which is what you want for judging an effect. Anything drawn with ImGui is absent.
- **Names collide to the second.** Move each PNG before asking for the next.
- **`WaitIdle` in `OnPostRender`.** Every capture stalls the GPU once; do not capture in a perf measurement.
- **Desktop-grab fallback.** `tools/screenshot.sh` grabs the primary screen with PowerShell and refuses unless StarMap or KSA is the foreground process (`FORCE=1` overrides it; its error text says `--force` but the code reads the `FORCE` variable). It also captures the UI and whatever else is on screen; prefer `Request`.
- **Game updates.** Re-check that `Program._screenshotCapture` still exists and that `Request(int, string)` keeps its signature; the helper degrades to "captures off" with a logged warning.

## See also

- [Overview](overview.md), [article 2](take-screenshots-from-code-2.md), [article 5](take-screenshots-from-code-5.md)
- [Visual regression testing for a KSA mod](../visual-regression-for-mods/overview.md), [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md)
- [KSAGolf visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md), [KSACars file-drop command bridge](../../../../mods/ksacars/features/file-drop-command-bridge.md)
