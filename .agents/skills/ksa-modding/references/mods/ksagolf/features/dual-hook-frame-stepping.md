---
type: Mod Feature
title: Dual-hook frame stepping on simulated time
description: Running a mod's per-frame simulation exactly once per frame from whichever of three hooks reaches it first (the GUI postfix, a Harmony prefix on Program.OnFrameCelestials, or the frame postfix), latched and always released, and integrated on the engine's applied sim step rather than player time
mod: ksagolf
category: patching
techniques: [starmap-lifecycle, harmony-prefix, reflection]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - starmap StarMapAllModsLoaded
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe, KSA.SimStep, KSA.UniverseTime]
tags: [frame-loop, starmap, harmony, prefix, simulation-time, timewarp, pause, f2, fault-tolerance]
---

KSAGolf moves things itself (a golf ball, borrowed cameras, a kitten drawn at address), so it needs
one simulation step per frame, at a moment where it can still affect this frame's render, measured
in the simulated time the engine actually applied. StarMap's per-frame hooks do not give that on
their own: the two GUI hooks disappear when the player hides the HUD with F2 (or a screenshot hides
it), and the frame hook runs after the render. KSAGolf therefore offers the same `StepOnce` to three
hooks, lets the first one that fires each frame claim it through a latch, and releases the latch in
a `finally` in the one hook that always runs.

The same class wraps all frame work in fault counting: ten exceptions disable the mod for the
session and unpatch everything, instead of spamming the log or taking the game down.

## How it works

| Hook | Lands at | Runs `StepOnce` when |
|---|---|---|
| `[StarMapAfterGui] OnAfterGui` | Postfix on `Program.OnDrawUiViewports`, inside `if (DrawUI)`[^starmap-programpatcher][^ksa-program] | Every frame with the HUD visible; preferred, because stepping here pairs a drawn offset with an anchor sampled this frame |
| Harmony prefix on private `Program.OnFrameCelestials(double)` | Last main-thread phase before `OnPreRender` that is not behind the `DrawUI` guard[^ksa-program] | HUD hidden: still before the render |
| `[StarMapAfterOnFrame] OnAfterFrame` | Postfix on `Program.OnFrame`, after `PostRender`; Harmony postfixes also run when `OnFrame` returns early[^starmap-programpatcher] | Only if neither of the above claimed the frame (the prefix failed to install, or an early-exit frame: a window close or font rebuild makes `PrepareFrame` return `Exit`, skipping UI, `OnFrameCelestials` and render); always calls `FrameLatch.EndFrame()` in `finally` |

`StepOnce` itself: claim the latch or return; `KsaWorld.BeginFrame()` clears last frame's draw
anchor; consume a pending save-load signal (see [Save-load detection](save-load-detection.md));
in the flight scene, take `KsaWorld.ConsumeSimStep()`; step the putting session with that sim step
(zero when paused) and the player delta; step the course builder; drive any camera the bridge is
holding.[^golf-mod]

**Simulated time, not player time.** StarMap hands `dtPlayer`, which keeps running while paused
and ignores timewarp. `ConsumeSimStep` reads `Universe.GetLastSimStep()` (a `SimStep` with
`PreviousTime`, `NextTime`, `DeltaTime`), which `Universe.ApplyVehicleSolvers` sets during
`PrepareFrame` immediately before `CurrentSystem.UpdatePerFrameData()` moves every celestial and
vehicle to the new epoch.[^ksa-universe][^ksa-simstep] Two Sim-side helpers make it safe:

- `StepGate<T>` hands each step out once (deduplicated on `NextTime`) and, when the hook missed a
  frame entirely, returns the span since the last consumed step instead of only the last step, so a
  skipped frame is integrated rather than lost. The span wins within 1 ns of rounding, matching the
  nanosecond clock celestials are placed on.[^golf-stepgate]
- `SimClock.Classify` turns a step into `Idle` (paused, zero, non-finite), `Run`, or `Skipped`
  (longer than 0.32 s by default, so the caller abandons rather than integrates a huge warp step);
  `SimClock.Viewing` gives player time only while the world runs, for camera eases.[^golf-simclock]

**Degrading patch.** The prefix target is private, so it is looked up by name with
`AccessTools.Method(typeof(Program), "OnFrameCelestials", [typeof(double)])`. If it is missing the
mod logs once and the frame postfix keeps stepping (one frame late with the HUD hidden). If the
prefix throws, it nulls its delegate and stands down for the session.[^golf-prerenderhook]

## Lifecycle and timing

Frame order in `Program.OnFrame` (build 2026.10.10.5554):[^ksa-program]

1. `PrepareFrame`: `ScreenshotCapture.OnPrepareFrame`, wait for solver jobs, `ApplyOrbitSolvers`,
   `ApplyVehicleSolvers` (sets the last sim step and updates per-frame positions), input,
   `ExecuteNext*Solvers` (the next physics step starts on workers).
2. `GizmosRenderer.ResetInstances`, `PrepareImGui`, `OnFrameEditor`, `OnFrameViewports`.
3. `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }`: **`StarMapBeforeGui` / `StarMapAfterGui`**.
4. `ImGui.Render`, gauges, `OnFrameController`, lights.
5. **`OnFrameCelestials`** (prefix here), then `OnPreRender`, `Render`, `PostRender`.
6. Postfix: **`StarMapAfterOnFrame`**.

So every step candidate runs on the main thread after this frame's physics results are applied and
while the next physics step is already running on workers (`frame:apply-solvers` is where the engine
produces the step the mod consumes; no mod code runs there). The mod reads settled vehicle and
celestial positions and writes only its own state (and camera state), never simulated vehicle
state. Stepping before `OnFrameCelestials` matters: that method resolves the camera's nearby body
for the frame, so moving a camera after it renders the planet against the wrong camera (the mod's
docs describe the surface rendering as open water).[^golf-prerenderhook]

`ScreenshotCapture` sets `Program.DrawUI = false` for its warm-up frames, which is the most common
reason the GUI hooks do not fire in KSAGolf's own tooling.[^ksa-screenshotcapture]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L74-L134) | Hooks, `StepOnce`, fault handling |
| [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs) | Degrading prefix on `Program.OnFrameCelestials` |
| [src/KSAGolf/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/FrameLatch.cs) | First-caller-wins latch |
| [src/KSAGolf/Sim/StepGate.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StepGate.cs) | Once-only step with skipped-frame span |
| [src/KSAGolf/Sim/SimClock.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/SimClock.cs) | Step classification and viewing clock |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L80-L97) | `ConsumeSimStep` |

## Snippets

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L74-L108
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        try
        {
            if (_disabled || _ui is null) return;

            if (KsaWorld.InFlight)
            {
                double speed = KsaWorld.SimulationSpeed;
                if (Math.Abs(speed - _lastSimSpeed) > 1e-9)
                {
                    Log.Info($"simulation speed {_lastSimSpeed:F2}x -> {speed:F2}x"
                             + (KsaWorld.IsPaused ? " (paused)" : ""));
                    _lastSimSpeed = speed;
                }
            }

            // A no-op on every frame the GUI pass ran. See StepOnce.
            StepOnce(dtPlayer);

            // After the step, so a command sees this frame's world and its simulated step.
            _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? _lastSimStep : 0.0);
        }
        catch (Exception e)
        {
            Fault("frame", e);
        }
        finally
        {
            // Unconditionally: this is the only hook KSA always calls, so a release skipped here stops
            // the mod for the session rather than for a frame.
            _frame.EndFrame();
        }
    }
```

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L113-L134
    private void StepOnce(double dtPlayer)
    {
        if (_disabled || _ui is null) return;
        if (!_frame.Claim()) return;

        KsaWorld.BeginFrame();

        if (WorldReloadHook.ConsumeReload()) ForgetTheWorld();

        // The scene, not the craft: KSA clears the controlled vehicle when it is destroyed and the
        // flight carries straight on.
        if (KsaWorld.InFlightScene)
        {
            _lastSimStep = KsaWorld.ConsumeSimStep();
        }

        _golf?.Step(KsaWorld.InFlightScene && !KsaWorld.IsPaused ? _lastSimStep : 0.0, dtPlayer);
        _builder?.Step();

        // Driving a borrowed view is not drawing, so it runs with the step and not with the panel.
        _bridge?.DriveCamera();
    }
```

```csharp
// src/KSAGolf/Ksa/PreRenderHook.cs L61-L84
        try
        {
            // Private, so by name: there is no way to write this that a compiler would check, which
            // is exactly why the failure has to degrade rather than break.
            MethodInfo? target = AccessTools.Method(typeof(Program), "OnFrameCelestials",
                                                    [typeof(double)]);

            if (target is null)
            {
                Trouble = "KSA has no Program.OnFrameCelestials(double) to hook";
                Log.Warn($"stepping after the render on frames with no UI: {Trouble}");
                return;
            }

            MethodInfo prefix = typeof(PreRenderHook).GetMethod(
                nameof(BeforeOnFrameCelestials), BindingFlags.NonPublic | BindingFlags.Static)!;

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Installed = true;
            Trouble = "";
            Log.Info("hidden-UI frames step before the render, via Program.OnFrameCelestials");
        }
```

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L80-L97
    public static double ConsumeSimStep()
    {
        SimStep step = Universe.GetLastSimStep();

        if (_consumedThrough is { } from && !step.NextTime.Equals(from))
        {
            LastLongerOfStepAndSpan = Math.Max(step.DeltaTime, Span(from, step.NextTime));
        }

        _consumedThrough = step.NextTime;

        // The span, not just the step: a frame in which this mod's hook never ran still advanced
        // the world, and GetLastSimStep reports only the most recent one. Taking a screenshot is
        // exactly that case -- ScreenshotCapture sets Program.DrawUI false and KSA guards the call
        // this mod postfixes with it, so the hook is not called at all. Differenced in Int128
        // nanoseconds because absolute universe time is far too large to subtract as double.
        return _stepGate.Consume(step.NextTime, step.DeltaTime, Span);
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Program.OnFrame`, `OnDrawUiFrame`, `OnDrawUiViewports`, `OnFrameCelestials` (private), `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Hook points and the F2 guard |
| `Universe.GetLastSimStep`, `ApplyVehicleSolvers`, `IsPaused`, `SimulationSpeed`, `GetElapsedTime` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | The applied step and its epoch |
| `SimStep.PreviousTime`, `NextTime`, `DeltaTime` | [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs) | Step boundaries (`UniverseTime`, Int128 nanoseconds) |

## Pitfalls and version notes

- The release of the latch must be unconditional and in the hook that is never skipped; a latch
  left set stops the mod for the rest of the session with no error.[^golf-framelatch]
- `Universe.IsPaused()` is `SimulationSpeed == 0`, a statement about the setting; the engine still
  applies one real step on the frame the speed drops to zero, so gate integration on the consumed
  step, not on the flag.[^golf-claude-md]
- `Universe.GetLastSimStep()` returns the same step until the next `ApplyVehicleSolvers`; reading it
  twice in one frame and integrating both double-counts motion.[^ksa-universe]
- `OnFrameCelestials` is private; a rename in a future build makes the prefix not apply, which the
  mod logs and survives. It still exists with the same signature in build 2026.10.10.5554.
- Line numbers quoted in the mod's own docs (`Program.cs:2012` and similar) are from build
  2026.9.22.5482 and do not match the current decomp.[^golf-frame-order]

## See also

- [Save-load detection](save-load-detection.md)
- [Frame and epoch draw anchor](frame-epoch-draw-anchor.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Borrowing the main view and handing it back](borrowed-chase-camera.md)
- [Manual AccessTools lookup with a degrade path when the method is gone](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-2.md) - how-to article demonstrating this feature
- [Static patch classes that look up the target, log once and stand down](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-3.md) - how-to article demonstrating this feature
- [Fault counter that pulls every patch after a limit](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-5.md) - how-to article demonstrating this feature
- [Test ball and contact physics with an epoch bookkeeping layer and missed-frame invariance](../../../howto/testing-and-tooling/offline-tests/run-mod-simulation-in-a-headless-rig/run-mod-simulation-in-a-headless-rig-3.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature

[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-prerenderhook]: src/KSAGolf/Ksa/PreRenderHook.cs
[^golf-framelatch]: src/KSAGolf/Sim/FrameLatch.cs
[^golf-stepgate]: src/KSAGolf/Sim/StepGate.cs
[^golf-simclock]: src/KSAGolf/Sim/SimClock.cs
[^golf-frame-order]: docs/KSA-FRAME-ORDER.md
[^golf-claude-md]: CLAUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-simstep]: KSA.SimStep (decompiled, build 2026.10.10.5554)
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
