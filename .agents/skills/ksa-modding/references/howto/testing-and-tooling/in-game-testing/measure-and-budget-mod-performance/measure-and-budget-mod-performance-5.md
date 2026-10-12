---
type: How-To
title: A 45 ms wall-clock throttle with a state-version check for hover picks
description: Cache an expensive per-frame pick and re-run it on a wall-clock interval, when the tool state changes or when a modifier flips, so cost does not scale with refresh rate
category: testing-and-tooling
group: in-game-testing
topic: measure-and-budget-mod-performance
approach: 5
difficulty: intermediate
prerequisites: [starmap, imgui]
demonstrated_by: [mods/ksa-measuretools/features/overlay-drawlist-rendering]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program]
tags: [performance, throttle, wall-clock, cache, hover-pick]
---

Outcome: a hover preview that does a mesh raycast at about 22 Hz at any refresh rate, reuses the cached result in between, and re-picks immediately when something that changes the answer changes. Demonstrated by KSA-MeasureTools ([overlay draw-list rendering](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md)).
It is the throttle half of "measure or throttle": when a computation is the cost, do it less often rather than faster. Prerequisites: StarMap and a per-frame ImGui overlay.

## Background: how the game does it

The UI pass runs once per rendered frame (`StarMapAfterGui`, `frame:draw-ui`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)), so anything inside it scales with the refresh rate: the mod's comment notes the part-mesh raycast costs milliseconds on the main thread, and a frame-count throttle (every Nth frame) would give 48 picks per second at 144 Hz instead of about 22. Player time (`dtPlayer`) also keeps running through a pause, so
the interval is measured with `Stopwatch.GetTimestamp`/`GetElapsedTime`, which is wall clock. See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Lifecycle and timing

Entirely in `frame:draw-ui` on the main thread: the throttle check, the pick and the drawing of the cached result happen in the overlay's `Draw`. The placement click always picks fresh, so the stale preview never affects accuracy. The cache is reset from `[StarMapUnload]`, which must not touch ImGui ([MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L50-L58)).

## Steps

1. **Pick the interval from the cost and the tolerance.** 45 ms: stale by a tenth of a second at worst is imperceptible for a hover preview. State the reason in a comment so nobody "fixes" it to per-frame:

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L34-L48

       // The hover preview re-picks on a wall-clock interval: the part mesh raycast
       // in MapPicker.PickPart costs milliseconds on the main thread, a ~50 ms
       // stale preview is imperceptible, and a frame-count throttle would scale the
       // pick rate with the refresh rate (48/s at 144 Hz instead of ~22/s). The
       // placement click always picks fresh, so accuracy is unaffected.
       private const double PreviewPickIntervalMs = 45.0;
       // Zero elapses immediately, so the very first armed frame picks.
       private static long _previewLastPickTimestamp;
       private static Anchor? _previewCache;
       // Circle mode previews an anchor pair: the ring point in _previewCache, the
       // fitted center here. Null in every other mode.
       private static Anchor? _previewCacheSecondary;
       private static int _previewStateVersion = -1;
       private static bool _previewEclipticFree;
   ```

2. **Cache the result and a version of the state that produced it.** The tool state exposes a counter bumped by every setter that changes the answer (mode, tool active, pending points):

   ```csharp
   // MeasureTools/Features/Measure/MeasureState.cs L36-L40
       // Bumped on every placement-state change so cached pick results (the overlay's
       // throttled hover preview) are invalidated immediately instead of one interval
       // late, e.g. when the free-plane basis moves to a freshly placed point.
       public static int StateVersion { get; private set; }

   ```
   ```csharp
   // MeasureTools/Features/Measure/MeasureState.cs L84-L103
               return;
           ToolActive = active;
           // Pausing drops a half-finished placement; settled measurements stay.
           if (!active)
               Pending.Clear();
           StateVersion++;
           if (DebugConfig.Measure)
               DefaultCategory.Log.Debug(active
                   ? "[MeasureTools] Measuring resumed."
                   : "[MeasureTools] Measuring paused, clicks pass through to the game.");
       }

       public static void SetMode(MeasureMode mode)
       {
           if (Mode == mode)
               return;
           Mode = mode;
           Pending.Clear();
           StateVersion++;
           if (DebugConfig.Measure)
   ```

3. **Re-pick when any of three things holds:** the interval elapsed, the state version moved, or a modifier flipped (Ctrl switches between snapping and a free ecliptic point, so the preview must flip with the key). Otherwise reuse the cache:

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L402-L436

           float2 mouseViewport = Cursor.GetPosition(viewport);
           // A cursor outside the viewport cannot place a point, and a cursor that
           // left the OS window reports far outside it; either way picking over
           // that position is wasted work.
           if (mouseViewport.X < 0f || mouseViewport.Y < 0f
               || mouseViewport.X > viewport.Size.X || mouseViewport.Y > viewport.Size.Y)
           {
               Reset();
               return;
           }
           // Ctrl previews (and places) a free point on the ecliptic plane even where
           // snapping would win; a modifier change re-picks immediately so the preview
           // flips with the key.
           bool eclipticFree = ImGui.GetIO().KeyCtrl;
           long now = Stopwatch.GetTimestamp();
           if (Stopwatch.GetElapsedTime(_previewLastPickTimestamp, now).TotalMilliseconds >= PreviewPickIntervalMs
               || _previewStateVersion != MeasureState.StateVersion
               || _previewEclipticFree != eclipticFree)
           {
               if (MeasureState.Mode == MeasureMode.Circle)
               {
                   MapPicker.PickCircle(viewport, mouseViewport, out Anchor? circleCenter, out Anchor? circleRim, reuseStockHover: true);
                   _previewCache = circleRim;
                   _previewCacheSecondary = circleCenter;
               }
               else
               {
                   _previewCache = MapPicker.Pick(viewport, mouseViewport, eclipticFree, reuseStockHover: true);
                   _previewCacheSecondary = null;
               }
               _previewLastPickTimestamp = now;
               _previewStateVersion = MeasureState.StateVersion;
               _previewEclipticFree = eclipticFree;
           }
   ```

4. **Skip the work when it cannot matter.** A cursor outside the viewport cannot place a point, so reset the cache and return before picking.

5. **Make the first frame pick.** `_previewLastPickTimestamp` starts at 0, so the elapsed time is huge and the first armed frame picks.

6. **Verify.** In a Debug build, wrap the pick in a [`PerfTracker.Scope`](measure-and-budget-mod-performance-1.md); at 144 Hz expect about 22 calls per second while the cursor moves over the map and near zero while it rests.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs) | Throttle, cache, preview draw |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs) | `StateVersion` |

## Variations and gotchas

- **Wall clock, not frames, not player time.** A frame-count throttle scales with FPS; sim time stops when paused.
- **Include every input of the answer in the invalidation test**, or the preview shows a stale answer after a state change (the version counter exists so the setters, not the draw code, declare what matters).
- **Do not throttle the committing action.** A click must pick fresh.
- **Expensive solves and time warp.** For solver-sized work that must not defeat time warp or stall a frame, use identity-keyed caches with real and simulated limits: [Run an expensive solve without stalling the frame](../../../flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md).

## See also

- [Overview](overview.md), [article 1](measure-and-budget-mod-performance-1.md)
- [Pick objects under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md)
- [KSA-MeasureTools overlay draw-list rendering](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md)
