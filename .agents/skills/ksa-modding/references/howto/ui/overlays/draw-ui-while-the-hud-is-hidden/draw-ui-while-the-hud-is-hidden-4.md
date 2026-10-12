---
type: How-To
title: Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)
description: Step a mod's simulation exactly once per frame from the GUI hook when the HUD is drawn, a prefix on OnFrameCelestials when it is hidden and the frame postfix as the fallback, on the engine's simulated time
category: ui
group: overlays
topic: draw-ui-while-the-hud-is-hidden
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/dual-hook-simulation-step
  - mods/ksagolf/features/dual-hook-frame-stepping
  - mods/gatos/features/frame-hooks-and-f2-fallback
  - mods/ksarmory/features/cursor-ray-world-click-tools
  - mods/ksastructures/features/frame-step-plumbing
  - mods/ksacars/features/frame-step-latch-and-car-registry
  - mods/ksacars/features/pose-subparts-at-runtime
techniques: [harmony-prefix, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, frame:apply-solvers]
game_types: [KSA.Program, KSA.Universe]
tags: [hud-hidden, f2, latch, frame-step, sim-time, dual-hook, pre-render, stepgate]
---
Some mods do more than draw: KSArmory advances rounds and fire control, KSAGolf a ball and borrowed cameras, KSAStructures lifts and furniture, gatOS telemetry and welds, KSACars wheel poses. That logic must run **exactly once per frame**, **before the render** so what it writes is drawn
this frame, **with the HUD hidden too**, and on the **engine's simulated time** rather than wall-clock time. No single StarMap hook does all of that, so five mods converged on the same plumbing: a one-claim `FrameLatch`, a step called from three
hooks, and a step gate on simulated time. This article is that plumbing. For draw-only needs, approaches 1 to 3 are simpler. Demonstrated by KSArmory, KSAGolf, gatOS, KSAStructures and KSACars. Prerequisites: StarMap and Harmony.

## Background: how the game does it

Where each hook lands in `Program.OnFrame` (build 2026.10.10.5554):

| Frame | Hook that steps | Where |
|---|---|---|
| HUD drawn (`DrawUI` true) | `[StarMapAfterGui]` | postfix on `OnDrawUiViewports`, inside `if (DrawUI)` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)[^ksa-program] |
| HUD hidden (F2, or a HUD-less screenshot) | Harmony **prefix** on the private `Program.OnFrameCelestials` | after `ImGui.Render`, before `OnPreRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2316-L2323)[^ksa-program] |
| HUD hidden and the prefix failed, or `PrepareFrame` returned `Exit` | `[StarMapAfterOnFrame]` | postfix on `OnFrame`, after `PostRender`; Harmony postfixes also run when `OnFrame` returns early |

- `OnFrameViewports` builds each viewport's camera matrices (`Camera.OnFrame`) before any of these, so a pose the step writes in either pre-render hook is rendered from the same matrices.
- `Program.OnFrame` calls `GizmosRenderer.ResetInstances()` before the UI pass and renders after it, so gizmos submitted from the GUI hook are drawn this frame and ones submitted from the frame postfix (after the render) are cleared before they are ever drawn.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260)[^ksa-program]
- `OnFrameCelestials` resolves the camera's nearby body and runs the planet renderer's `OnFrame`; stepping **after** it prepares terrain against a camera the step then moves (seen in play as the ground rendering as open water on F2 during a chase), so only a *prefix* makes the hidden-HUD path identical to the GUI path.
- `ScreenshotCapture` sets `DrawUI` false for capture frames; the F2 key flips it during `Glfw.PollEvents`, so the `if (DrawUI)` guard sees the new value in the same frame.
- Simulated time: `Universe.ApplyVehicleSolvers` sets `_lastSimStep = _nextSimStep` and then runs `CurrentSystem.UpdatePerFrameData()`, so `Universe.GetLastSimStep()` (a `SimStep` with `PreviousTime`, `NextTime`, `DeltaTime`) is the step that moved every body this frame.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1921-L1952)[^ksa-universe][^ksa-simstep]

## Lifecycle and timing

Which hook steps, and what is true there: the physics step applied this frame (`ApplyVehicleSolvers`) and the next already queued on workers hold in all three; the ImGui frame is open only in the GUI hook; gizmos survive to the render only in the first two; subpart transforms written now are drawn this frame in the first two and
next frame in the third. Hence the order of preference: GUI hook, then the prefix, then the postfix. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the latch.** A bool: `Claim()` returns true to the first caller after each `EndFrame()`. The failure to make impossible is the latch never clearing: running the step twice costs a duplicated frame, failing to release stops the mod for the session. So `EndFrame()` goes in a `finally` on the hook that cannot
   be skipped:

```csharp
// src/KSArmory/Sim/FrameLatch.cs L15-L36
internal sealed class FrameLatch
{
    private bool _taken;

    /// <summary>Whether the frame's work has already been claimed.</summary>
    public bool Claimed => _taken;

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
}
```

2. **Write `StepOnce`.** Claim or return; reset per-frame caches; do the work; service world clicks yourself only when the UI did not draw:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L324-L355
private void StepOnce(double dtPlayer)
{
    if (_disabled || _roster is null) return;
    if (!_frame.Claim()) return;

    KsaWorld.BeginFrame();
    KsaWorld.AuditBodiesOnce();

    // Before anything integrates: the roster still holds the previous world's systems, and one
    // more step of them is one more step of rounds that no longer have a world to fly in.
    if (WorldReloadHook.ConsumeReload()) ForgetTheWorld();

    // The *scene*, not the craft. Losing the vehicle being flown does not end the flight: KSA
    // clears Program.ControlledVehicle and carries straight on, so gating the whole simulation
    // on it freezes every round in the air the instant a launcher dies. They then neither land
    // nor expire - a salvo suspended mid-flight, which reads as rounds that despawned. Nothing
    // in here needs a controlled craft; it walks the roster.
    if (KsaWorld.InFlightScene)
    {
        using (_budget.Measure("sim", top: true)) StepSimulation(dtPlayer);
    }

    // Here rather than in the GUI hook, and outside the flight gate above so a view is still
    // handed back on the way out. Driving a camera is not drawing: hiding the UI takes the GUI
    // pass away entirely, and a mod that stops restating a view it has borrowed leaves the
    // player riding a frozen offset with no closing, no transition and no aim -- while the
    // world it is pointed at carries on.
    using (_budget.Measure("cameras")) DriveCameras(dtPlayer);

    // The UI pass reads clicks when it runs; with the UI hidden it does not run at all.
    if (!KsaWorld.UiDrawn && KsaWorld.InFlight) UpdateWorldClicks();
}
```

3. **Call it first from the GUI hook**, then draw against the state it produced (the round's offset and the anchor it is drawn against then share an epoch):

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L509-L520
[StarMapAfterGui]
public void OnAfterGui(double dt)
{
    if (_disabled || _ui is null || _roster is null) return;

    try
    {
        // First, so the drawing below reads the state this frame produced. Stepping here
        // rather than in the frame hook is what makes the round's offset and the anchor it is
        // drawn against share an epoch - see StepOnce, which is also why this is the preferred
        // hook rather than the only one.
        StepOnce(dt);
```

4. **Install the pre-render prefix by name** from `[StarMapAllModsLoaded]`, degrade if the method is gone and stand down if the prefix ever throws (it runs inside the engine's loop):

```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L56-L92
public static void Install(Action<double> step)
{
    _step = step;

    if (Installed) return;

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
    catch (Exception e)
    {
        Trouble = e.Message;
        Log.Warn($"could not hook the pre-render step; frames with no UI will be drawn one "
                 + $"frame late ({e.Message})");
    }
}
```
```csharp
// src/KSArmory/Ksa/PreRenderHook.cs L114-L131
private static void BeforeOnFrameCelestials(double deltaTime)
{
    try
    {
        _step?.Invoke(deltaTime);
    }
    catch (Exception e)
    {
        // Stand down rather than throw again next frame from inside the engine's loop. The
        // frame postfix has the mod's own fault handling, and it is one hook later -- so
        // giving up here is the same degradation a refused patch gives, arrived at differently.
        _step = null;

        if (_complained) return;
        _complained = true;
        Log.Error("the pre-render step failed; the frame postfix takes it from here", e);
    }
}
```

5. **Release the latch in the frame postfix**, which is also the last-resort stepper:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L268-L305
[StarMapAfterOnFrame]
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    try
    {
        if (_disabled || _roster is null) return;

        // Sim speed and pause state change what everything else in the log means, so record
        // them rather than inferring them later from frozen timestamps.
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

        // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
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
        // Unconditionally, and ahead of nothing: this is the only hook KSA always calls, so a
        // release skipped by an early return or an exception would stop the mod for the rest of
        // the session rather than for a frame.
        _frame.EndFrame();
    }
}
```

## Step the mod on simulated time

StarMap hands the frame hooks `dtPlayer`, which keeps running while paused and ignores timewarp. KSAGolf and KSAStructures instead consume the engine's last sim step, **once per `NextTime`**:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L80-L100
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

private static double Span(UniverseTime from, UniverseTime to)
    => (double)(to.Nanoseconds - from.Nanoseconds) / 1e9;
```

- `StepGate<T>` hands a step out once (deduplicated on `NextTime`, so repeated frames on one step integrate it exactly once), and when the hook missed a frame entirely (a screenshot skips the GUI pass) returns the span since the last consumed step instead of only the last step:

```csharp
// src/KSAGolf/Sim/StepGate.cs L59-L77
public double Consume(T nextTime, double delta, Func<T, T, double>? spanSeconds)
{
    if (_hasIntegrated && nextTime.Equals(_integratedThrough)) return 0.0;

    double taken = delta;

    // A span shorter than the step by more than rounding means the clock disagrees with the
    // step it just handed over -- a save loaded mid-session jumps it backwards -- and the step
    // is then the thing that moved the world.
    if (_hasIntegrated && spanSeconds is not null)
    {
        double span = spanSeconds(_integratedThrough, nextTime);
        if (double.IsFinite(span) && span > taken - RoundingSeconds) taken = span;
    }

    _integratedThrough = nextTime;
    _hasIntegrated = true;
    return taken;
}
```

- The span is differenced in `Int128` nanoseconds because absolute universe time is too large to subtract as `double`; it wins within 1 ns of rounding, matching the nanosecond clock celestials are placed on. A step longer than the caller can honestly integrate (a huge warp step) is abandoned by a separate classifier rather than integrated.
- A paused game reports zero. Vehicles for the step come from `Universe.CurrentSystem.All`, not from `Program.VehiclesInFrame`, which is refilled in `PrepareFrame` and cleared in `PostRender` so it reads empty from the frame postfix.

## Variations across the five mods

| Mod | Hook set | Notes |
|---|---|---|
| KSArmory | GUI hook, `OnFrameCelestials` prefix, frame postfix | Click tools keep working with the HUD hidden: `UpdateWorldClicks` is called from `StepOnce` when `Program.DrawUI` is false |
| KSAGolf | same three | Fault counting: ten exceptions disable the mod and unpatch everything |
| KSAStructures | same three | `StepGate` plus `Int128` span for missed frames |
| KSACars | GUI hook and frame postfix (no prefix) | `Buggies.Pose` writes subpart transforms every step; on a hidden-HUD frame the wheels are posed a frame late |
| gatOS | `StarMapBeforeGui`, `StarMapAfterGui`, `StarMapAfterOnFrame` plus a `GameViewport.OnFrame` prefix on the main viewport | Two boolean latches (`_mainViewportPreparedThisFrame`, `_uiHooksRanThisFrame`) instead of a single one; the game-free half of the mod cannot use `Program.FrameNumber` since it increments before the postfix |

When posing parts every frame, write `Part.PositionParentAsmb`, `Asmb2ParentAsmb` and optionally `Scale`, then call `ResetCachedPosMatrixValues()`, or the new value is stored and ignored:

```csharp
// src/KSACars/Ksa/Buggies.cs L867-L877
private static void Place(Part? body, double3 position, doubleQuat rotation, double3? scale)
{
    if (body is null || !Vec.IsFinite(position)) return;

    body.PositionParentAsmb = position;
    body.Asmb2ParentAsmb = rotation;
    if (scale is { } s) body.Scale = s;

    // Part caches its matrices; without this the new value is stored and ignored.
    body.ResetCachedPosMatrixValues();
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FrameLatch.cs) | The latch |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L268-L355) | `StepOnce`, `OnAfterFrame` |
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) | By-name prefix on `Program.OnFrameCelestials`, degrade path |
| [src/KSAGolf/Sim/StepGate.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StepGate.cs) | Once-per-step gate with span closing |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L59-L110) | The two-hook variant |

## Gotchas

- **A latch that never clears** stops the mod; always release in a `finally` on the hook that always runs (`OnFrame` returning early on `Exit` still runs Harmony postfixes).
- **Prefix, not postfix, on `OnFrameCelestials`**, for the reason in Background; the two paths must be identical.
- **`OnFrameCelestials` is private**, found by name; the failure mode of a rename is a one-frame-late step, which the mod logs once.
- **Gizmos from the frame postfix are lost** (cleared before the next render); submit them from the GUI hook only.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-ui-while-the-hud-is-hidden](overview.md)
- [Approach 3: replay StarMap GUI work from a prefix on OnDrawUiConsole](draw-ui-while-the-hud-is-hidden-3.md)
- [Game loop and mod lifecycle: harmony patches on Program and Universe](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md)
- [Click tools with the HUD hidden: cursor to world ray, approach 1](../../input/cursor-to-world-ray/cursor-to-world-ray-1.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- [Defining articulated subparts and animating or hiding them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md)
- Mod features: [Dual-hook simulation step](../../../../mods/ksarmory/features/dual-hook-simulation-step.md),
  [Dual-hook frame stepping](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md),
  [Frame hooks and F2 fallback](../../../../mods/gatos/features/frame-hooks-and-f2-fallback.md),
  [Frame step plumbing](../../../../mods/ksastructures/features/frame-step-plumbing.md),
  [Frame step latch and car registry](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md),
  [Pose subparts at runtime](../../../../mods/ksacars/features/pose-subparts-at-runtime.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-simstep]: KSA.SimStep (decompiled, build 2026.10.10.5554)
