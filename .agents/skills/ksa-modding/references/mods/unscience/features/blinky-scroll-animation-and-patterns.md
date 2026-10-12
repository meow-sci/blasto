---
type: Mod Feature
title: Drive a pixel display by switching engine controllers on and off
description: Blinky lights grid pixels with buffered EngineController.SetIsActive calls, offering pattern presets, a diffing static display and a column-stepped scrolling marquee, while the shared PartRenderFilter hides the tiny engine meshes
mod: unscience
submod: blinky
category: vehicles-and-parts
techniques: [engine-activation, input-event-buffer]
hooks:
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: BlinkyGridManager.TickAll)"
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: pattern buttons)"
  - harmony-prefix KSA.PartTreeRenderData.Compose / ComposeDynamic / ComposeGlass (shared PartRenderFilter, owner blinky)
  - harmony-postfix KSA.PartTreeRenderData.Compose / ComposeDynamic / ComposeGlass (shared PartRenderFilter, owner blinky)
lifecycle: [frame:draw-ui, frame:input, frame:render, event:load]
game_types: [KSA.EngineController, KSA.InputEvents, KSA.Vehicle, KSA.VehicleEngine, KSA.Part]
tags: [engines, animation, display, input-buffer, patterns, render-filter]
---

Once a grid exists, Blinky treats every cell as one bit: on means both engines of the pixel pair are
active and firing, off means both are inactive. On top of that it offers five pattern buttons (Off,
All, alternating rows, alternating columns, checkerboard), a static display that only toggles the
pixels that changed, and a scrolling marquee that slides a sparse bitmap across the grid. Because the
pixel engines are scaled to 1%, what the player sees is the exhaust plumes; the meshes themselves are
hidden by default through the shared [part render filter](./lib-part-render-filter.md).[^blinky-readme]

Mechanism in one line: map each `(row, col)` to its cached `EngineController[]` and call
`SetIsActive(null, on)` on them, which the game queues and applies at the next frame's input phase.

## How it works

**Pixel control.** `PixelGrid` caches each cell's engine controllers at scan time, so the per-frame
path never searches the part tree. `BlinkyGridManager.SetPixel` and the pattern/scroll loops call
`EngineController.SetIsActive(null, on)` on every controller of a cell. Every entry point that can
turn a pixel on first calls `vehicle.SetEnum(VehicleEngine.MainIgnite)`, because an engine only fires
while the vessel is ignited and throttled up (see [feed wiring](./blinky-engine-pixel-feed-wiring.md)).[^blinky-grid-manager]

**Patterns.** `ApplyPattern(vehicleId, gridName, Func<(int row, int col), bool>)` stops any scroll,
evaluates the selector for every cell, and records the lit cells in `ActivePixels`. The presets in
`PixelPatterns` are one-line predicates: `(row + col) % 2 == 0` for the checkerboard, and so
on.[^pixel-patterns]

**Static display with diffing.** `DisplayStatic(vehicleId, gridName, (x, y)[] pixels, reset)` swaps
`(x, y)` to `(row, col)`. With `reset`, it turns off only the previously lit cells that are not in the
new set and turns on only the new cells that were not lit, then replaces `ActivePixels`. Without
`reset` it adds pixels. Fewer `SetIsActive` calls means fewer queued input events and fewer engine
start/stop transients.

**Scroll.** `ScrollAnimation` keeps a `HashSet<(x, y)>` of lit source pixels, the image width and
height, and a float offset advanced by `speed * dt` (pixels per second). The loop length is
`imageWidth + gridCols / 2`, which leaves a half-grid gap before the text repeats. Each frame it
computes the integer column `scrollCol = (int)offset` and returns early if it has not changed, so
engines are only toggled when the image moves one column. For each cell it samples
`(scrollCol + col, row)` in the source set. `StartBuiltIn` uses `BuiltInScrollPixels.Pixels`, a
hard-coded sparse bitmap of about 1,300 points.[^scroll-animation][^builtin-pixels]

In this checkout the Unscience window exposes only the pattern buttons; `StartScroll`,
`StartBuiltInScroll` and `DisplayStatic` are public library API (the README refers to an external RPC
API that is not in the repo) and are used by save replay.[^blinky-submod]

**Stray engines.** `NonLcdEngineCache` lazily lists every engine controller on the controlled vessel
that is not a pixel; the window warns when any of them is active (they would spin the craft) and
offers a Deactivate button that calls `SetIsActive(null, false)` on them.[^non-lcd-cache]

**Mesh hiding.** `BlinkyPatches.Apply` registers the owner `blinky` with `PartRenderFilter`, with a
predicate that hides a full part when "Render engine meshes" is off and the part is a pixel. Pixel
membership is `Id.StartsWith("pixel_")` or presence in a `HashSet<Part>` rebuilt on every
register/unregister, because native saves drop the `pixel_*` names.[^blinky-patches][^blinky-persistence]

**Saves.** The save participant stores the active mask, scroll pixels, speed and offset. Replay
restarts the scroll and calls `RestoreOffset`, which resets the column cache and applies the saved
frame immediately; static engine states come from the native save.[^blinky-persistence]

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Pattern buttons, Ignite, Deactivate stray engines | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| `BlinkyGridManager.TickAll(dt)` (scroll step) | `BlinkySubmod.Update` from `[StarMapBeforeGui]`, or the hidden-HUD replay | `frame:draw-ui` |
| Controller state actually changes | `InputEvents.ApplyInputEvents` -> `ApplyIActivateBatched`, in the next `PrepareFrame` (`Program.cs` L2181) | `frame:input` |
| Mesh compaction | `PartTreeRenderData.Compose*` prefix/postfix in `UpdateRenderingResources` | `frame:render` |

`EngineController.SetIsActive` does not touch the controller: it adds an `IActivateInputData` with
`Operation = ActivateOp.Engine` to `InputEvents.IActivateInputBuffer` (a growable buffer), and
`IActivateInputData.Apply` later writes `EngineController.IsActive`.[^ksa-enginecontroller][^ksa-inputevents]
`ApplyInputEvents` runs in `PrepareFrame` after the solvers are joined and before
`ExecuteNextVehicleSolvers` (L2220), so a pixel toggled from the UI is applied on the main thread at a
safe point and takes effect in the next physics step, one frame after the click or scroll
tick.[^ksa-program] Passing `null` for the vehicle skips the flight-computer reset that `Apply` queues
for a non-null vehicle. `SetEnum(VehicleEngine.MainIgnite)` is different: it writes
`_manualControlInputs.EngineOn` directly from the UI thread, the same field the game's own engine
controls set.[^ksa-vehicle]

The scroll uses the UI `dt`, so it advances in real time, keeps running while the game is paused,
and does not speed up with time warp.

## Key code

| Path | Role |
|---|---|
| [blinky.lib/BlinkyGridManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyGridManager.cs#L104-L272) | Scroll start/stop, static display, off, patterns, tick, pixel set, ignition |
| [blinky.lib/ScrollAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/ScrollAnimation.cs) | Column-stepped marquee |
| [blinky.lib/PixelPatterns.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/PixelPatterns.cs) | Pattern predicates |
| [blinky.lib/BuiltInScrollPixels.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BuiltInScrollPixels.cs) | Built-in sparse bitmap |
| [blinky.lib/NonLcdEngineCache.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/NonLcdEngineCache.cs) | Non-pixel engine detection and shutdown |
| [blinky.lib/BlinkyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyPatches.cs) | `PartRenderFilter` owner and predicate |
| [blinky.lib/BlinkySubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkySubmod.Persistence.cs#L127-L137) | Pixel membership set |

## Snippets

The scroll step only touches engines when the image moves a whole column:

```csharp
// blinky.lib/ScrollAnimation.cs L82-L110
    public void Update(double dt)
    {
        if (!IsActive || _grid == null) return;

        _scrollOffset += ScrollSpeed * (float)dt;

        // Wrap around when we've scrolled the full cycle
        if (_scrollOffset >= _totalScroll)
            _scrollOffset -= _totalScroll;

        int scrollCol = (int)_scrollOffset;

        // Skip update if we haven't moved to a new integer column
        if (scrollCol == _lastScrollCol) return;
        _lastScrollCol = scrollCol;

        foreach (var (key, engines) in _grid.Engines)
        {
            int srcX = scrollCol + key.col;
            int srcY = key.row;

            bool on = srcX >= 0 && srcX < _imageWidth
                   && srcY >= 0 && srcY < _imageHeight
                   && _pixelSet.Contains((srcX, srcY));

            for (int i = 0; i < engines.Length; i++)
                engines[i].SetIsActive(null, on);
        }
    }
```

Static display that only toggles the difference:

```csharp
// blinky.lib/BlinkyGridManager.cs L160-L182
        var grid = state.BlinkyGrid.Grid;
        var newPixels = new HashSet<(int row, int col)>();
        foreach (var (x, y) in pixels)
            newPixels.Add((y, x)); // (x=col, y=row) → (row, col) for grid lookup

        if (reset)
        {
            foreach (var pos in state.ActivePixels)
            {
                if (!newPixels.Contains(pos))
                    SetPixel(grid, pos.row, pos.col, false);
            }

            foreach (var pos in newPixels)
            {
                if (!state.ActivePixels.Contains(pos))
                    SetPixel(grid, pos.row, pos.col, true);
            }

            state.ActivePixels.Clear();
            foreach (var pos in newPixels)
                state.ActivePixels.Add(pos);
        }
```

Why toggling from the UI is safe: the game buffers the activation:

```csharp
// current/decomp/KSA/EngineController.cs L77-L87
	public void SetIsActive(Vehicle? vehicle, bool activationState)
	{
		InputEvents.IActivateInputData data = new InputEvents.IActivateInputData
		{
			Operation = InputEvents.ActivateOp.Engine,
			Vehicle = vehicle,
			EngineController = this,
			ActivationState = activationState
		};
		InputEvents.IActivateInputBuffer.Add(data);
	}
```

The render-filter registration:

```csharp
// blinky.lib/BlinkyPatches.cs L18-L32
    public static void Apply(Harmony harmony)
    {
        PartRenderFilter.Register(harmony, FilterOwner, ShouldHide);
        Console.WriteLine("blinky.lib: patches applied");
    }
// ...
    // Pixel parts: Id starts with "pixel_" or the part is registered in a live grid.
    private static bool ShouldHide(Part fullPart) =>
        !BlinkyPatchState.RenderPixelParts && BlinkyGridManager.IsPixelPart(fullPart);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `EngineController.SetIsActive(Vehicle?, bool)` / `IsActive` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L44-L87) | Queues pixel on/off; `IsActive` read for the stray-engine warning |
| `InputEvents.IActivateInputBuffer` / `IActivateInputData.Apply` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L434-L470) | Where the activation is applied |
| `InputEvents.ApplyInputEvents` -> `ApplyIActivateBatched` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1044-L1118) | Sorted, batched apply in `PrepareFrame` |
| `Program.PrepareFrame` input and dispatch order | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2179-L2221) | Apply at L2181, dispatch at L2220 |
| `Vehicle.SetEnum(Enum?)` -> `SetAction(VehicleEngine)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6409-L6425) | Sets `EngineOn` before pixels are lit |
| `PartTreeRenderData.Compose*` | see [part render filter](./lib-part-render-filter.md) | Mesh hiding |

## Pitfalls and version notes

- One frame of latency: a toggle is visible after the next `PrepareFrame` applies it and the next
  physics step runs; engine start/shutdown transients then add their own ramp.
- Each toggle is two or more queued events per cell (both engines, every controller). Large grids
  that redraw every column produce thousands of events per step; the diffing static display and the
  column-change check exist to keep that down.
- The marquee speed is in grid columns per real second, not simulation time.
- Mesh hiding does not apply in raytraced IVA views, and hidden pixel parts still exist for physics
  (mass, colliders); see the [part render filter](./lib-part-render-filter.md) for details.
- `TurnOff` and pattern changes stop the scroll but `ScrollAnimation.Stop` itself never turns pixels
  off; callers must call `TurnOff` for that.

## See also

- [Runtime part grid builder](./blinky-runtime-part-grid-builder.md)
- [Engine pixel feed wiring](./blinky-engine-pixel-feed-wiring.md)
- [Part render filter](./lib-part-render-filter.md)
- [It's So Shiny light-part grids](./its-so-shiny-light-part-grids.md) (same patterns and scroll with lights)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md) (keeps the scroll ticking with the HUD hidden)
- [Blinky sub-mod](../submods/blinky.md)
- [Mod overview](../overview.md)
- [Animate a grid of engines with buffered SetIsActive calls](../../../howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-3.md) - how-to article demonstrating this feature

[^scroll-animation]: blinky.lib/ScrollAnimation.cs
[^blinky-grid-manager]: blinky.lib/BlinkyGridManager.cs
[^pixel-patterns]: blinky.lib/PixelPatterns.cs
[^builtin-pixels]: blinky.lib/BuiltInScrollPixels.cs
[^non-lcd-cache]: blinky.lib/NonLcdEngineCache.cs
[^blinky-patches]: blinky.lib/BlinkyPatches.cs
[^blinky-persistence]: blinky.lib/BlinkySubmod.Persistence.cs
[^blinky-submod]: blinky.lib/BlinkySubmod.cs
[^blinky-readme]: blinky/README.md
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
