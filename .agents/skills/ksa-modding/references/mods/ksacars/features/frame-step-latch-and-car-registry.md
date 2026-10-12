---
type: Mod Feature
title: Once-per-frame car step from two StarMap hooks, with a registry of the mod's craft
description: Run per-frame work once from whichever of StarMapAfterGui or StarMapAfterOnFrame fires first, release the latch in a finally on the hook KSA always calls, and keep a per-frame registry of the craft carrying the mod's part
mod: ksacars
category: getting-started
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe, KSA.CelestialSystem, KSA.Vehicle, KSA.Part]
tags: [starmap, frame-latch, hidden-ui, registry, census, vehicles, fault-tolerance]
---

KSACars has one piece of per-frame work, `KSACarsMod.StepOnce`: find the cars, pose their wheels,
update their engine and rocket sounds. It wants to run it from `[StarMapAfterGui]`, before the
render, so the wheels are drawn where this frame put them. That hook is skipped whenever the HUD is
hidden, so the mod also calls the step from `[StarMapAfterOnFrame]`, and a two-method latch makes
sure only the first caller each frame does the work.[^ksacars-mod][^ksacars-framelatch] The step
starts by refreshing a registry of the craft that carry one of the mod's parts, which every Harmony
patch in the mod then looks up by `Vehicle`.[^ksacars-buggies]

## How it works

**The latch.** `FrameLatch` is a bool: `Claim()` returns true to the first caller after each
`EndFrame()` and false to everyone else. `StepOnce` returns at once unless it wins the claim.
`OnAfterFrame` calls `StepOnce` (a no-op on any frame the GUI pass already claimed), then the
bridge, and calls `_frame.EndFrame()` in a `finally`. The repo's reasoning, in the type's own
doc comment: running the step twice costs a duplicated frame, but a latch that never clears stops
the mod for the rest of the session, so the release belongs on the hook that cannot be skipped and
ahead of every early return.[^ksacars-framelatch][^ksacars-framelatch-tests]

**Which hook wins.**

| Frame | `[StarMapAfterGui]` | `[StarMapAfterOnFrame]` | Effect |
|---|---|---|---|
| HUD shown | claims, runs the step, then draws the panel and the craft mover | step is a no-op; releases the latch | wheels posed before the render |
| HUD hidden (`Program.DrawUI` false) | not called | claims, runs the step, releases | wheels posed after the render, so a frame late |
| `PrepareFrame` or `Render` returned `Exit` | not called (early exit) or called (render exit) | still called, releases | the latch never sticks |

**The registry.** `StepOnce` first calls `KsaWorld.InvalidateCensus()`, then
`Buggies.Sync(KsaWorld.Vehicles)`.[^ksacars-mod]

1. `KsaWorld.Vehicles` is built at most once per pass: every non-disposed `Vehicle` in
   `Universe.CurrentSystem.All`, falling back to `Program.VehiclesInFrame` only when that comes
   back empty.[^ksacars-ksaworld][^ksa-celestialsystem]
2. `Buggies.Sync` adds each live vehicle not yet known whose part list contains a part whose
   `Id` equals a `BuggyProfile.PartId` (`Find`), resolves its subparts once (`Resolve`), and
   restores the headlamp setting from the part's saved light switch.
3. It then drops every entry whose vehicle is disposed or no longer holds that part (undocked,
   destroyed, staged away) and tells `RailsHook` to forget it.
4. The entries live in a `Dictionary<Vehicle, Entry>` named `Active`. The patches on hot paths
   return at once when it is empty, and otherwise either look the vehicle up (`Buggies.Physics`
   from `Vehicle.PrepareWorker`, `Buggies.Flames` from `AddVolumetricExhaustInstances`, and
   `Buggies.Of` from the key, crash and HUD patches) or walk its few entries comparing the part tree
   or part (`LightLamps` from `PartTree.UpdateRenderData`, `LensColour` from
   `PartTreeRenderData.WriteState`), so a craft that is not a car costs a lookup or a short loop.[^ksacars-buggies]

## Lifecycle and timing

`StarMapAfterGui` is StarMap's postfix on `Program.OnDrawUiViewports` and `StarMapAfterOnFrame` its
postfix on `Program.OnFrame`.[^starmap-programpatcher] In `Program.OnFrame`, `OnDrawUiFrame` and
`OnDrawUiViewports` are called only inside `if (DrawUI)`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2273)), and the `ToggleUi` key and
screenshot capture (`ScreenshotCapture.OnPrepareFrame` sets `Program.DrawUI = false` while it
warms up) both clear it.[^ksa-program][^ksa-screenshotcapture] A Harmony postfix runs on every return of the
method it patches, so `StarMapAfterOnFrame` also runs on the frames where `PrepareFrame` returns
`Exit` (window close, font rebuild), where `OnFrame` returns before the gauges because `IsLoaded`
is false, and where `Render` returns `Exit`.[^ksa-program]

Both hooks run on the main thread after `PrepareFrame` has called `ExecuteNextVehicleSolvers`, so
the next physics step is already in flight during the whole car step. That is why nothing in
`StepOnce` writes vehicle state: the springs and pushes are written from the
[`Vehicle.PrepareWorker` prefix](./prepareworker-impulse-injection.md), which reads the registry
built here in the previous frame. A car that appears this frame (spawned, loaded, undocked) is
driven from the next frame's prefix on.

**Why `CurrentSystem.All`.** The mod's comment says `Program.VehiclesInFrame` "reads back empty"
from a StarMap hook. The code is more specific: `PrepareFrame` refills it from
`Universe.CurrentSystem.All` (`RefreshVehiclesInFrame`,
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L582-L589)), and `PostRender` clears it
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2368)), as does a failed `Render`. It is
therefore full in `StarMapAfterGui` and empty in `StarMapAfterOnFrame`. Reading
`CurrentSystem.All` directly gives the same set from either hook.[^ksa-program][^ksacars-ksaworld]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L59-L130) | `OnAfterFrame` (release in `finally`), `OnAfterGui`, `StepOnce` |
| [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs#L15-L36) | The latch, KSA-free and unit tested |
| [tests/KSACars.Tests/FrameLatchTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/FrameLatchTests.cs) | The first hook owns the frame, a frame whose GUI hook never ran is still stepped, release always reopens it |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L288-L352) | `IsAlive`, the census from `CurrentSystem.All`, the `VehiclesInFrame` fallback |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L73-L136) | `Active`, `Sync`: add, resolve, drop, then scoops and clutter |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L457-L476) | `Find`: the car's part by `BuggyProfile.PartId` |

## Snippets

```csharp
// src/KSACars/Ksa/KSACarsMod.cs L112-L130
    private void StepOnce()
    {
        if (!_frame.Claim()) return;

        if (!KsaWorld.InFlightScene)
        {
            _buggySound.StopAll();
        _rocketSound.StopAll();
            return;
        }

        // Every frame rather than on the clock: the springs step inside the engine's own physics
        // window, so this only has to find the cars and show where their wheels ended up.
        KsaWorld.InvalidateCensus();
        _buggies.Sync(KsaWorld.Vehicles);
        _buggies.Pose();
        _buggySound.Update(_buggies, 1f);
        _rocketSound.Update(_buggies);
    }
```

```csharp
// src/KSACars/Sim/FrameLatch.cs L22-L35
    /// <summary>
    /// Whether this caller owns the frame's work. True to the first asker after each
    /// <see cref="EndFrame"/>, and false to everyone after it.
    /// </summary>
    public bool Claim()
    {
        if (_taken) return false;

        _taken = true;
        return true;
    }

    /// <summary>End the frame, whatever happened during it.</summary>
    public void EndFrame() => _taken = false;
```

```csharp
// src/KSACars/Ksa/KsaWorld.cs L315-L332
    // Universe.CurrentSystem.All rather than Program.VehiclesInFrame: the latter is a per-frame scratch
    // buffer refilled at a point in the tick a StarMap hook does not line up with, and reads back empty
    // from there. The system's collection is valid from any hook.
    private static void CollectVehicles(List<Vehicle> into)
    {
        into.Clear();

        try
        {
            if (Universe.CurrentSystem is { } system)
            {
                ReadOnlySpan<Astronomical> all = system.All.AsSpan();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is Vehicle { IsDisposed: false } v) into.Add(v);
                }
            }
        }
        // ...
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Program.OnFrame`, `DrawUI`, `OnDrawUiViewports` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334)) | Where the two hooks land; the `DrawUI` gate |
| `Program.RefreshVehiclesInFrame`, `VehiclesInFrame`, `PostRender` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L582-L589)) | Filled in `PrepareFrame`, cleared in `PostRender` |
| `Universe.CurrentSystem`, `CelestialSystem.All` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66)) | Every astronomical, vehicles included, valid from any hook |
| `Vehicle.IsDisposed`, `Vehicle.Parts.Parts`, `Part.Id` | Liveness and the part match |
| `Program.ControlledVehicle`, `Program.Editor` | `KsaWorld.InFlight` / `InFlightScene` gates |

## Pitfalls and version notes

- **Release the latch on the hook that always runs**, in a `finally`, before any early return: an
  exception or a `_disabled` return on that hook must not leave the latch taken.[^ksacars-mod]
- **The fallback is a frame late.** From `StarMapAfterOnFrame` the poses are drawn by the next
  frame's render, and gizmos submitted there are reset (`GizmosRenderer.ResetInstances`) before
  they are drawn, which is why the craft mover only runs from the GUI hook (see
  [craft mover](./craft-mover-mouse-pick-place.md)).
- **`VehiclesInFrame` depends on the hook.** It is full during the UI pass and empty after
  `PostRender`; `CurrentSystem.All` is the stable choice.
- **A dictionary keyed by `Vehicle` is main-thread only.** Every reader here (the `PrepareWorker`
  prefix, the render-data postfixes) runs on the main thread; the one worker-side patch,
  `KittenWantsWake`, uses its own `ConcurrentDictionary` (see
  [keep a craft off rails](./keep-thrusting-craft-off-rails.md)).
- **Do not write simulated state here.** The step is in flight; see
  [PrepareWorker impulse injection](./prepareworker-impulse-injection.md) for the window that works.

## See also

- [Pose subparts at runtime](./pose-subparts-at-runtime.md) (the main consumer of the step)
- [Per-mod log file, build stamp and fault limiter](./mod-log-file.md) (the fault counter around both hooks)
- [Inject wheel forces from a Vehicle.PrepareWorker prefix](./prepareworker-impulse-injection.md)
- [File-drop command bridge](./file-drop-command-bridge.md) (runs after the step in the frame hook)
- [KSACars overview](../overview.md)
- [Rebuild the vehicle set each frame from Universe.CurrentSystem.All and sync a registry](../../../howto/getting-started/build/build-a-mod-without-game-references/build-a-mod-without-game-references-4.md) - how-to article demonstrating this feature
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature
- [Skip detection: rebuild a derived registry from the live world every frame](../../../howto/persistence/saves/detect-save-load/detect-save-load-6.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature
- [Sync a Vehicle-keyed roster of craft that carry your part, once per frame from two hooks](../../../howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-1.md) - how-to article demonstrating this feature

[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-framelatch]: src/KSACars/Sim/FrameLatch.cs
[^ksacars-framelatch-tests]: tests/KSACars.Tests/FrameLatchTests.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled)
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled)
