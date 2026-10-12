---
type: Game Feature
title: Visual regression testing for a KSA mod
description: Capture frames at the same simulated instant, then diff variants, build contact sheets and animations and compute a temporal standard-deviation map to separate a real change from render noise
category: testing-and-tooling
group: in-game-testing
topic: visual-regression-for-mods
demonstrated_by:
  - mods/ksagolf/features/visual-testing-captures
  - mods/ksacars/features/ksa-mcp-server
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
lifecycle: [frame:after-on-frame, frame:post-render, thread:background]
game_types: [KSA.ScreenshotCapture, KSA.Camera]
tags: [visual-testing, screenshots, diff, temporal-noise, mcp, python]
---

KSA has no renderer in its GPU-less test runs (see [HeadlessHarness](../../offline-tests/headless-test-harness-for-game-mods/overview.md)), so a mod's visual behaviour (a cloud shader, a lit building interior, a held camera) is judged from pictures of a running game. A single screenshot answers little,
because the world moves between two runs: clouds drift, the sun sets, the camera is not where it was. The analyzed mods therefore treat a screenshot as a measurement with a manifest and add three
techniques on top of [taking the picture](../take-screenshots-from-code/overview.md): same-instant variants (pause, change one setting, capture, restore, diff), series spaced in simulated seconds (a sheet, a GIF) and a per-pixel temporal map that shows what moves
when nothing in the world should. There is no golden-image test suite in these repos: the verdicts are statistics and pictures a human or agent reads, not pass/fail thresholds.

## Engine background

Two engine facts make the method work, both verified in the decompiled build.

- **A paused world still renders.** `Universe.IsPaused()` tests the simulation speed against exactly zero ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)); the renderer keeps producing frames, and
  `ScreenshotCapture` is a render-side system ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L19-L36)). Pausing freezes weather, sun and orbits, so two captures in one paused instant differ only by what you changed
  and by renderer noise (temporal anti-aliasing history, dithering).
- **A capture hides the UI and can warm up.** With `warm=N` the engine runs N frames with `Program.DrawUI = false` before the capture frame ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L210-L231)), so history-blended effects settle without window-shaped holes. Details in [Take clean screenshots from code](../take-screenshots-from-code/overview.md).

The projection that makes a crop exact is the camera's own matrix: `Camera.EgoToClipDouble` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L277-L280)), used when the manifest is written.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: same-instant variant diffs and noise maps with a vis.py toolbox](visual-regression-for-mods-1.md) | ksagolf ([tools/vis/vis.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/vis/vis.py)), ksarmory ([tools/vis/vis.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/vis/vis.py)) | `image-diff` (Pillow and numpy) | `frame:after-on-frame`, `frame:post-render`, `thread:background` | Cheap and local, answers "what did this setting change" exactly; judged by eye or statistics, no baselines |
| [2: contact sheets, GIFs and noise maps returned inline over MCP](visual-regression-for-mods-2.md) | ksacars ([tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-mcp/server.py)), ksagolf ([tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-mcp/server.py)) | `mcp-server` | `frame:after-on-frame`, `frame:post-render`, `thread:background` | An agent sees the result in the conversation; adds a long-lived server and an MCP client |

## See also

- [Take clean screenshots from code](../take-screenshots-from-code/overview.md), [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/overview.md), [Fly scripted scenarios unattended](../run-unattended-scenarios-and-report-a-verdict/overview.md)
- Letting AI agents control the game over MCP has no written topic yet; article 2 here covers the visual half.
- Mod features: [KSAGolf visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md), [KSACars KSA MCP server](../../../../mods/ksacars/features/ksa-mcp-server.md), [KSAStructures dev file bridge](../../../../mods/ksastructures/features/dev-file-bridge-and-mcp.md), [KSArmory file command bridge](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
- [Let AI agents drive the game through an MCP server](../../../external-io/control/let-ai-agents-control-the-game-over-mcp/overview.md) - related topic

