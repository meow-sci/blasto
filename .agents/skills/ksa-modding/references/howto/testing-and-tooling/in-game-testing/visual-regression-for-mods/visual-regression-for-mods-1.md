---
type: How-To
title: Same-instant variant diffs, frame series and temporal noise maps with a vis.py toolbox
description: Pause the world, capture a base and each variant of one setting at the same instant, diff them with manifest-driven crops, and compute a per-pixel standard deviation over a series to find renderer noise
category: testing-and-tooling
group: in-game-testing
topic: visual-regression-for-mods
approach: 1
difficulty: intermediate
prerequisites: [starmap, python, pillow, numpy]
demonstrated_by: [mods/ksagolf/features/visual-testing-captures, mods/ksarmory/features/file-command-bridge-and-mcp]
techniques: [image-diff, reflection]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, frame:post-render, thread:background]
game_types: [KSA.ScreenshotCapture, KSA.Camera]
tags: [visual-testing, diff, temporal-noise, python, manifest]
---

Outcome: for a visual change you are making (a shader tunable, a lighting flag, a camera pose) you get the same instant photographed with and without it, the amplified difference with statistics,
a series sheet or GIF and a temporal-noise map, all cropped around the thing you care about from the capture's manifest, not from coordinates typed by hand. Demonstrated by KSAGolf
([visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md)) and KSArmory ([file command bridge](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)).
Prerequisites: a bridge with `pause`, `set`, `get`, `capture` and `step` ([article 1 of the bridge topic](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md)),
the manifests from [Take clean screenshots from code](../take-screenshots-from-code/take-screenshots-from-code-1.md), and Python 3 with Pillow and numpy.

## Background: how the game does it

The method rests on what a paused KSA still does: simulation speed 0 freezes time (`Universe.IsPaused()` is `_simulationSpeed == 0.0`, [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)), but frames keep rendering and `ScreenshotCapture` keeps
working, so you can change a setting and photograph again with the weather, sun and camera identical. Anything that needs motion (flicker caused by camera movement, particle systems) cannot be seen this way; the repo guidance is to measure flicker with the world
running slowly rather than paused ([docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/VISUAL-TESTING.md)). A series is spaced in *simulated* seconds, so slowing the world makes any capture rate free.

## Lifecycle and timing

All game-side work is the bridge's: `pause`, `set`, `capture` and `resume` run one at a time from `StarMapAfterOnFrame` (`frame:after-on-frame`); the PNG is encoded off thread after the capture frame (`frame:post-render`, `thread:background`) and the bridge
polls for it. The Python side is a separate process and costs the game nothing. After each `set`, a capture's warm-up frames (24 in the mods) let temporal history settle before the picture is taken. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the toolbox next to the mod** (`tools/vis/vis.py`). Load images, read the sidecar manifest, and crop around the subject. KSAGolf crops a square around `craft_screen`; KSArmory crops a box around the burst cloud from `cloud_box`, `burst_screen`, `cap_screen` and `top_screen`:

   ```python
   # tools/vis/vis.py L24-L44
   def manifest_for(png: str | Path) -> dict:
       side = Path(png).with_suffix(".json")
       return json.loads(side.read_text()) if side.exists() else {}


   def subject_box(manifest: dict, size: tuple[int, int], fraction: float = 0.4) -> tuple[int, int, int, int] | None:
       """A square round the craft being flown, a fraction of the picture's width across."""
       w, h = size
       point = manifest.get("craft_screen")
       if not point:
           return None

       cx, cy = point[0] * w, point[1] * h
       half = fraction * w * 0.5
       box = (int(cx - half), int(cy - half), int(cx + half), int(cy + half))
       return (max(box[0], 0), max(box[1], 0), min(box[2], w), min(box[3], h))


   def crop(img: Image.Image, manifest: dict) -> Image.Image:
       box = subject_box(manifest, img.size)
       return img.crop(box) if box and box[2] > box[0] and box[3] > box[1] else img
   ```

   Adapted idea: the crop function is the only mod-specific piece; give each mod's manifest the screen points its tests care about ([tools/vis/vis.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/vis/vis.py#L29-L58)).

2. **Diff two pictures of the same instant.** The amplified per-pixel maximum channel delta, with counts a script can threshold:

   ```python
   # tools/vis/vis.py L59-L70
   def diff(a: Image.Image, b: Image.Image, gain: float = 4.0) -> tuple[Image.Image, dict]:
       """What changed between two pictures of the same instant, amplified, and how much."""
       x = np.asarray(a, dtype=np.int16)
       y = np.asarray(b, dtype=np.int16)
       d = np.abs(x - y).max(axis=2)
       stats = {
           "mean": round(float(d.mean()), 3),
           "px_over_8": int((d > 8).sum()),
           "px_over_24": int((d > 24).sum()),
           "max": int(d.max()),
       }
       return Image.fromarray(np.clip(d * gain, 0, 255).astype(np.uint8)).convert("RGB"), stats
   ```

3. **Find renderer noise with a temporal map.** Capture a series of a paused world and take the per-pixel standard deviation of luminance. With nothing moving, anything bright is flicker, crawl or ghosting from the renderer:

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

4. **Measure speckle in a region** with the high-pass energy a dither leaves (a smooth cloud has little of it):

   ```python
   # tools/vis/vis.py L87-L93
   def grain(img: Image.Image, box: tuple[int, int, int, int] | None = None) -> float:
       """High-pass energy: the speckle a dither leaves, which a smooth cloud has little of."""
       g = np.asarray((img.crop(box) if box else img).convert("L"), dtype=np.float32)
       if g.shape[0] < 3 or g.shape[1] < 3:
           return 0.0
       blur = (g[:-2, 1:-1] + g[2:, 1:-1] + g[1:-1, :-2] + g[1:-1, 2:] + g[1:-1, 1:-1]) / 5.0
       return round(float(np.abs(g[1:-1, 1:-1] - blur).mean()), 3)
   ```

5. **Do variants in one paused instant.** The server's `capture` pauses (remembering whether it was already paused), captures a base, and for each variant reads the old values, `set`s the new ones, captures, restores in a `finally`, and resumes only if it paused. Then it diffs every variant against the base:

   ```python
   # tools/ksa-mcp/server.py L147-L183
   def capture(label: str = "shot", frames: int = 1, every_s: float | None = None,
               every_frames: int | None = None, variants: list[dict] | None = None,
               crop: bool = False, width: int = 960) -> list[dict]:
       """Pictures and what they show. Variants are taken in the same paused instant as the base, so a
       diff between them is the change and nothing else."""
       content: list[dict] = []

       if variants:
           was_paused = send("status").get("paused")
           send("pause")
           try:
               base = send("capture", timeout=60, label=f"{label}-base", frames=1)["frames"][0]
               shots = [("base", base)]
               for k, variant in enumerate(variants):
                   old = {name: send("get", name=name)[name] for name in variant}
                   for name, value in variant.items():
                       send("set", name=name, value=value)
                   try:
                       shot = send("capture", timeout=60, label=f"{label}-v{k}", frames=1)["frames"][0]
                   finally:
                       for name, value in old.items():
                           send("set", name=name, value=_typed(value))
                   shots.append((json.dumps(variant), shot))
           finally:
               if not was_paused:
                   send("resume")

           base_img = vis.load(wsl(base["file"]))
           for name, shot in shots:
               img = vis.load(wsl(shot["file"]))
               content.append(_text(f"{name}: {shot['file']}\n{_brief(shot)}"))
               content.append(_image(vis.crop(img, shot) if crop else img, width))
               if name != "base":
                   d, stats = vis.diff(base_img, img)
                   content.append(_text(f"diff against base: {stats}"))
                   content.append(_image(vis.crop(d, shot) if crop else d, width))
           return content
   ```

   Series for temporal analysis come from `capture {"frames": 8, "every_frames": 1}` on a paused world; spacing by `every_s` unpauses the world for exactly that much simulated time between pictures ([take screenshots, article 1](../take-screenshots-from-code/take-screenshots-from-code-1.md)).

6. **Run it from a shell.** The server doubles as a CLI, writing the images an MCP client would be shown into `tools/ksa-mcp/last/` (emptied by every call, so copy a capture out before the next call):

   ```bash
   python3 tools/ksa-mcp/server.py cli pause
   python3 tools/ksa-mcp/server.py cli capture '{"label":"ab","variants":[{"<Config field>":0}]}'
   python3 tools/ksa-mcp/server.py cli capture '{"label":"flash","frames":6,"every_s":0.25}'
   ```

   (From `docs/VISUAL-TESTING.md`; the field name is a placeholder for one of your own settings.) Expect for a variant: its picture, `diff against base: {'mean': ..., 'px_over_8': ..., 'px_over_24': ..., 'max': ...}` and the amplified diff; for a series: a sheet, a GIF path and `temporal {...}`.

## Complete example

| File | Role |
|---|---|
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/vis/vis.py) | `crop`, `diff`, `temporal`, `grain`, `sheet`, `gif`, `jpeg_b64` |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-mcp/server.py) | `capture` with variants and series; CLI mode |
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/vis/vis.py) | Same toolbox with a burst-centred crop |
| [docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/VISUAL-TESTING.md) | What each tool is for and what the first night found |

## Variations and gotchas

- **A diff between two runs is mostly not the change.** Clouds move and the sun sets between flights; the KSArmory doc records needing paused stills and normalising against regions the effect cannot reach. Do variants in one paused instant.
- **Paused series are blind to motion-dependent effects.** KSArmory could not reproduce the player-reported flicker in a paused series, nor in a held-camera one; the fix was a button that saves frames from the moment it is seen ([take screenshots, article 5](../take-screenshots-from-code/take-screenshots-from-code-5.md)).
- **Every manifest field you want to crop on must be written at capture time.** Crops guessed from pixel coordinates break the first time the camera moves.
- **The server is stdlib for MCP but not for images.** `server.py` imports `vis`, which imports numpy and Pillow; without them the server does not start.
- **No baselines.** These tools compare pictures with each other. A regression gate would need stored baseline manifests and thresholds on `px_over_24`; that is an extension, not something the mods ship.
- **Captures cost a `WaitIdle`;** do not capture while measuring frame time.

## See also

- [Overview](overview.md), [article 2: contact sheets and GIFs over MCP](visual-regression-for-mods-2.md)
- [Take clean screenshots from code](../take-screenshots-from-code/overview.md), [Fly scripted scenarios unattended](../run-unattended-scenarios-and-report-a-verdict/overview.md)
- [KSAGolf visual testing captures](../../../../mods/ksagolf/features/visual-testing-captures.md)
