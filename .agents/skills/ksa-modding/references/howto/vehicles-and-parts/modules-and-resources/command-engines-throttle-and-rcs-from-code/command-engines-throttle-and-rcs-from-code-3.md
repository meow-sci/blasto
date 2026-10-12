---
type: How-To
title: Animate a grid of engines with buffered SetIsActive calls
description: Treat each engine pair as a pixel, ignite the vehicle once, and toggle only the cells that changed with EngineController.SetIsActive(null, bool) from a scroll or pattern loop
category: vehicles-and-parts
group: modules-and-resources
topic: command-engines-throttle-and-rcs-from-code
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/blinky-scroll-animation-and-patterns]
techniques: [input-event-buffer]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.EngineController, KSA.Vehicle, KSA.InputEvents, KSA.VehicleEngine]
tags: [engines, animation, display, pattern, set-is-active]
---
Outcome: a grid of rocket engines becomes a dot-matrix display: patterns (all, alternating rows, checkerboard), a
static image, or a scrolling marquee, where "on" means the engines of a pixel are active and firing. unscience's
Blinky builds the grid (see [Create and attach parts to a live vessel and feed runtime engines from tanks](../add-parts-to-a-live-vessel/overview.md)); this article is the command side, which is the same
`SetIsActive` as [article 1](command-engines-throttle-and-rcs-from-code-1.md) used at scale, with a diffing
discipline that keeps the queue small.

Prerequisite: engines to toggle. Demonstrated by [unscience/blinky-scroll-animation-and-patterns](../../../../mods/unscience/features/blinky-scroll-animation-and-patterns.md).

## Background: how the game does it

- `EngineController.SetIsActive(vehicle, state)` queues an `IActivateInputData` (`Operation = Engine`); the state
  changes when `ApplyInputEvents` runs `ApplyIActivateBatched` in the next `PrepareFrame` [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L77-L87)[^ksa-enginecontroller]
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1046-L1112)[^ksa-inputevents].
- With a **null** vehicle, `IActivateInputData.Apply` skips queuing the flight-computer reset for that vehicle
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L457-L495). Bulk toggles want that.
- An engine fires only while the vehicle is ignited and throttled up: `Vehicle.SetEnum(VehicleEngine.MainIgnite)` sets
  `_manualControlInputs.EngineOn` directly [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6407)[^ksa-vehicle].

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| pattern buttons, ignite, deactivate stray engines | the submod's ImGui content from `StarMapAfterGui` | `frame:draw-ui` |
| scroll step (`TickAll(dt)`) | the submod's `Update` from `StarMapBeforeGui` (or the hidden-HUD replay) | `frame:draw-ui` |
| controller state actually changes | `ApplyInputEvents` -> `ApplyIActivateBatched` in the next `PrepareFrame` | `frame:input` |

A pixel toggled from the UI is applied on the main thread at a safe point and takes effect in the next physics step,
one frame after the click or scroll tick. `SetEnum(MainIgnite)` is different: it writes the manual-input field
directly from the UI thread, the same field the game's own engine controls set. The scroll uses UI `dt`, so it runs in
real time, keeps running while paused and does not follow time warp. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Cache each cell's `EngineController[]` at scan time** so the per-frame path never searches the part tree.
2. **Ignite once** whenever any pixel can turn on:

```csharp
// blinky.lib/BlinkyGridManager.cs L259-262
    private static void EnsureVehicleIgnited(Vehicle vehicle)
    {
        vehicle.SetEnum(VehicleEngine.MainIgnite);
    }
```

3. **Apply a pattern** with a selector function over `(row, col)`; stop any scroll first and record lit cells:

```csharp
// blinky.lib/BlinkyGridManager.cs L213-232
    public static bool ApplyPattern(string vehicleId, string gridName, Func<(int row, int col), bool> selector)
    {
        var state = Get(vehicleId, gridName);
        if (state == null) return false;

        state.Scroll.Stop();
        state.ActivePixels.Clear();
        EnsureVehicleIgnited(state.Vehicle);

        foreach (var (key, controllers) in state.BlinkyGrid.Grid.Engines)
        {
            bool on = selector(key);
            for (int i = 0; i < controllers.Length; i++)
                controllers[i].SetIsActive(null, on);
            if (on)
                state.ActivePixels.Add(key);
        }

        return true;
    }
```

4. **Scroll by diffing.** Advance a float offset by `speed * dt`, compute the integer column, and return early when it has
   not changed, so engines are toggled only when the image moves one column:

```csharp
// blinky.lib/ScrollAnimation.cs L82-110
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

5. **Static display with diffing** (`DisplayStatic`): turn off only previously lit cells that are not in the new set and
   turn on only new cells, so fewer queued events and fewer engine start/stop transients.
6. **Stray engines.** Find engines on the controlled vessel that are not pixels and offer a deactivate (they would spin
   the craft).

## Complete example

- [blinky.lib/BlinkyGridManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkyGridManager.cs)[^unscience-blinkygridmanager], [blinky.lib/ScrollAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/ScrollAnimation.cs)[^unscience-scrollanimation], [blinky.lib/NonLcdEngineCache.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/NonLcdEngineCache.cs)[^unscience-nonlcdenginecache]

## Variations and gotchas

- **Buffer growth.** `IActivateInputBuffer` is a growable buffer (initial 500); toggling thousands of engines per frame is
  what the diffing avoids.
- **Pass `null`, not the vehicle,** to avoid a flight-computer reset per call.
- **Each toggle is one frame late.** A visual effect keyed to the engine state lags the command by a frame.
- **Saves.** The native save holds static engine states; the mod's save record stores only the mask, scroll pixels,
  speed and offset.

## See also

- [Overview](overview.md), [Article 1](command-engines-throttle-and-rcs-from-code-1.md), [Article 4](command-engines-throttle-and-rcs-from-code-4.md)
- [unscience/blinky-scroll-animation-and-patterns](../../../../mods/unscience/features/blinky-scroll-animation-and-patterns.md)
- [Create and attach parts to a live vessel and feed runtime engines from tanks](../add-parts-to-a-live-vessel/overview.md)

[^unscience-scrollanimation]: unscience: blinky.lib/ScrollAnimation.cs
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
[^unscience-blinkygridmanager]: unscience: blinky.lib/BlinkyGridManager.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^unscience-nonlcdenginecache]: unscience: blinky.lib/NonLcdEngineCache.cs
