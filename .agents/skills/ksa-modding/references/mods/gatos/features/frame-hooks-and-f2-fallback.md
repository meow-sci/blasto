---
type: Mod Feature
title: Frame hooks with an F2-proof fallback
description: Three StarMap hooks plus a main-viewport Harmony prefix give gatOS one game-thread work slot per frame that keeps running when the player hides the HUD
mod: gatos
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.GameViewport.OnFrame
  - harmony-postfix KSA.GameViewport.OnFrame
lifecycle: [frame:viewports, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program, KSA.GameViewport, KSA.ViewportRegistry]
tags: [lifecycle, starmap, harmony, frame-loop, drawui, game-thread]
---

gatOS has to touch game state (sample telemetry, drain control commands, tick audio, paint,
welds, IVA physics) exactly once per rendered frame, on the main thread, whether or not the
player has hidden the HUD. It does this with three StarMap hooks and one Harmony prefix, split
into two driver methods and coordinated by two boolean latches. Any mod whose per-frame work
must survive the HUD toggle can copy the pattern.

## How it works

| Slot | Attached by | Work |
|---|---|---|
| Main viewport prefix | Harmony prefix on `GameViewport.OnFrame(double)`, guarded by `ReferenceEquals(__instance, Program.MainViewport)` | `SampleTelemetry`, `TickSchedules`, `DrainCommands`, then `DriveCamera`; sets `_mainViewportPreparedThisFrame` |
| `OnBeforeUi` | `[StarMapBeforeGui]` | sets `_uiHooksRanThisFrame`, runs `DrivePerFrame` |
| `OnAfterUi` | `[StarMapAfterGui]` | runs `DrivePostSolver` (welds, IVA physics), then draws the gatOS ImGui windows |
| `OnAfterFrame` | `[StarMapAfterOnFrame]` | if the GUI hooks did not run this frame, runs `DrivePerFrame` and `DrivePostSolver` itself |

`DrivePerFrame` first checks `_mainViewportPreparedThisFrame`. When the viewport prefix already
sampled and drained, it only clears the latch; otherwise (camera feature disabled or hook failed
to install) it runs the sample/schedule/drain trio itself. Either way it then ticks audio, paint
and the thug-life anchors ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L450-L467)). `DrivePostSolver` runs the two
drivers that need settled solver output ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L474-L478)).

The F2 problem: StarMap implements `StarMapBeforeGui` as a prefix on `Program.OnDrawUiFrame` and
`StarMapAfterGui` as a postfix on `Program.OnDrawUiViewports`
([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L36)). Both calls sit inside
`if (DrawUI)` in `Program.OnFrame`, and the `ToggleUi` input action flips `DrawUI`.[^ksa-program]
Before the fix, hiding the HUD silently stopped the sampler, the command drain and every driver.
`StarMapAfterOnFrame` is a postfix on `Program.OnFrame` itself, so it always fires; gatOS uses it
as a stand-in that runs only when `_uiHooksRanThisFrame` is false ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L427-L443)).
It deliberately does not call `DrawGameUi()`: with `DrawUI` false there is no ImGui frame worth drawing.

Why a boolean latch rather than a frame counter: the game-free half of the partial `Mod` class
must compile without KSA assemblies (see [Compile-gated game seam](compile-gated-game-seam.md)),
and `Program.FrameNumber` is incremented inside `OnFrame` before the postfix fires, so a
frame-number equality test could not tell "already ran" from "new frame".

## Lifecycle and timing

Order inside `Program.OnFrame` in build 2026.10.10.5554 ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2275)):

1. `PrepareFrame`: waits for `JobSystems.VehicleSolver`, runs `Universe.ApplyOrbitSolvers` /
   `ApplyVehicleSolvers` / `ApplyClothSolvers`, polls input, then dispatches the next step with
   `Universe.ExecuteNextClothSolvers` / `ExecuteNextVehicleSolvers` / `ExecuteNextOrbitSolvers`
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236)).
2. `PrepareImGui`, `OnFrameEditor`, then `OnFrameViewports`, which calls `viewport.OnFrame` for
   every registered viewport whether or not the HUD is visible ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2624)).
   `GameViewport.OnFrame` runs the active controller, then `Camera.OnFrame`
   ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)). gatOS's prefix lands here: `frame:viewports`.
3. `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports; }`: `StarMapBeforeGui` and `StarMapAfterGui` (`frame:draw-ui`).
4. `ImGui.Render`, gauges, controller, lights, celestials, pre-render, render, post-render.
5. `StarMapAfterOnFrame` postfix (`frame:after-on-frame`).

What this means for state access:

- Every slot runs on the main thread after `ApplyVehicleSolvers` has copied the finished physics
  step into the `Vehicle` objects (`_vehicleUpdateTask.ApplyResultsToMainThread()` in
  [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)), so vehicle reads are a consistent post-step view.
- The next vehicle step is already running on worker threads (dispatched in step 1). Workers
  operate on copies prepared in `Vehicle.PrepareWorker`, so a main-thread write made in these
  slots is picked up at the next dispatch, one frame later, unless it is a field the apply step
  overwrites. That is why flight-computer setpoints use a separate solver-phase prefix (see
  [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)).
- gatOS's own comment on `OnAfterUi` says this hook is "where the vehicle-solver workers have
  finished" ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L371-L377)). That does not hold: nothing joins the step dispatched
  in this frame's `PrepareFrame` before the next frame's `VehicleSolver.Wait()`, so it may still be
  running during both GUI hooks. The `DrivePostSolver` drivers therefore call `JobSystems.VehicleSolver.Wait()`
  themselves, which blocks the main thread until the step finishes whenever welds or IVA physics are
  active. The wait does not change what main-thread `Vehicle` fields return (the finished step is only
  copied in by the next `ApplyVehicleSolvers`); what it buys is that `Vehicle.Teleport` may detach a
  vessel from its physics bubble (see [Welds that pin one vessel to another](welds-vessel-anchoring.md)).
- On hidden-HUD frames `OnAfterFrame` runs `DrivePerFrame` after `Render` and `PostRender`, so what
  its ticks change for rendering (paint state, clutter texture slots, sticker decal matrices,
  thug-life anchor re-resolution) is first seen by the next frame, and the audio tick runs after
  that frame's `GameAudio.UpdateAudio`. The sample, schedule tick and Frame-phase drain are
  unaffected while the viewport prefix is installed, because `OnFrameViewports` is outside the
  `DrawUI` block.
- The viewport prefix exists so camera commands and schedule cues drained this frame affect this
  frame's view matrix; see [Programmable camera director](programmable-camera-director.md).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L347-L352) | `[StarMapBeforeGui]` hook, sets the GUI latch |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L384-L406) | `[StarMapAfterGui]` hook, post-solver drivers then UI |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L427-L443) | `[StarMapAfterOnFrame]` F2 stand-in |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L450-L478) | `DrivePerFrame` and `DrivePostSolver` split |
| [gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs#L36-L80) | main-viewport prefix/postfix with identity guard |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L574-L583) | `PrepareMainViewportFrame`: sample, schedules, drain, camera |

## Snippets

```csharp
// gatOS.GameMod/Mod.cs L427-L467
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        if (!ReferenceEquals(_instance, this))
            return;

        var ranInGui = _uiHooksRanThisFrame;
        _uiHooksRanThisFrame = false;
        if (!ranInGui)
        {
            // UI hidden: stand in for both GUI hooks, in their original order. Deliberately *not*
            // DrawGameUi() — with DrawUI false there is no ImGui frame to draw into.
            DrivePerFrame(dtPlayer);
            DrivePostSolver(dtPlayer);
        }

    }

    // ...
    private void DrivePerFrame(double dt)
    {
        if (_mainViewportPreparedThisFrame)
        {
            _mainViewportPreparedThisFrame = false;
        }
        else
        {
            // No camera hook (camera feature disabled, installation failure, or no live viewport):
            // preserve the original StarMap/F2 path so telemetry and control never stop altogether.
            SampleTelemetry(dt);
            TickSchedules(dt);
            DrainCommands();
        }
        DriveAudio(); // right after the drain: prune finished channels, enforce end=, publish status
        DrivePaint(); // apply/prune live part and EVA bindings after paint commands from this drain
        UpdateThugLife(); // validate/re-resolve thug-life anchors on the game thread, before the scene renders
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs L52-L65
    private static void Prefix(GameViewport __instance, double dt)
    {
        if (_faulted || !ReferenceEquals(__instance, Program.MainViewport))
            return;
        try
        {
            Mod.PrepareMainViewportFrame(dt);
        }
        catch (Exception ex)
        {
            _faulted = true;
            ModLog.Log.Error($"gatOS camera viewport prefix disabled after an error: {ex.Message}");
        }
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2275) | StarMap postfix target (`StarMapAfterOnFrame`) |
| `Program.OnDrawUiFrame` / `OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3180) | StarMap prefix/postfix targets, skipped when `DrawUI` is false |
| `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526-L539) | HUD visibility flag toggled by `InputAction.ToggleUi` |
| `GameViewport.OnFrame(double)` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124) | Harmony prefix/postfix target, runs controller then camera |
| `Program.MainViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | identity guard so secondary and portrait viewports do not drive gatOS |

## Pitfalls and version notes

- `OnFrameViewports` iterates every registered viewport (main, part thumbnail, secondary and
  crew-portrait ones). Without the `ReferenceEquals(__instance, Program.MainViewport)` guard the
  prefix would run the sampler several times per frame.
- The fallback order matters: if the viewport prefix ran, `DrivePerFrame` must not sample or drain
  again, or schedule clocks would double-step. The second latch handles that.
- gatOS's anchor notes record the hook target as verified at 2026.9.7.5402, when `KSA.Viewport`
  was deleted and the hook moved to `GameViewport.OnFrame`; in 5554 the method and its single
  `double` overload are unchanged.
- Each hook body is wrapped in try/catch and a fault latches the subsystem off (`_faulted`,
  `_samplerDead`, `_commandsDead`) so one bad frame never throws into the game loop.

## See also

- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [Solver-phase drain for flight computer setpoints](solver-phase-command-drain.md)
- [Programmable camera director](programmable-camera-director.md)
- [Compile-gated game seam](compile-gated-game-seam.md)
- [gatOS overview](../overview.md)
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
