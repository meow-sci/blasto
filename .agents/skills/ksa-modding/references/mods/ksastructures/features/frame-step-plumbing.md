---
type: Mod Feature
title: One step per frame across three hooks, on the simulation clock
description: Runs the mod's per-frame step from whichever of the GUI postfix, a prefix on Program.OnFrameCelestials or the frame postfix reaches it first, integrates the engine's own applied sim step exactly once, and notices save loads through a Program.OnGameLoaded postfix
mod: ksastructures
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix, frame-latch]
hooks:
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - starmap StarMapUnload
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-postfix KSA.Program.OnGameLoaded
lifecycle:
  - startup:immediate-load
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:celestials
  - frame:after-on-frame
  - startup:game-loaded
game_types:
  - KSA.Program
  - KSA.Universe
  - KSA.SimStep
  - KSA.UniverseTime
  - KSA.UncompressedSave
  - KSA.CelestialSystem
  - KSA.Vehicle
tags: [starmap, lifecycle, frame-order, hidden-ui, sim-step, timewarp, save-load, fault-handling]
---

KSAStructures runs one "step" per frame: lift motion, furniture, the rim fix, and driving the
borrowed cameras. Getting that step to run exactly once, before the render, on frames where the
UI is hidden, and against the world's own clock rather than the wall clock, takes more than one
StarMap hook. This is the plumbing every other feature of the mod sits on, and it is reusable as is.[^mod-entry][^mod-claude]

## How it works

1. **Where StarMap's hooks fire.** `StarMapBeforeGui` and `StarMapAfterGui` are a prefix on
   `Program.OnDrawUiFrame` and a postfix on `Program.OnDrawUiViewports`; `StarMapAfterOnFrame` is a
   postfix on `Program.OnFrame`.[^starmap-program] `Program.OnFrame` calls both UI methods inside
   `if (DrawUI)`; `DrawUI` flips on the `InputAction.ToggleUi` binding (F2 in the mod's notes) and
   `ScreenshotCapture` clears it while it captures, so the
   two GUI hooks simply do not run on a hidden-UI frame. `AfterOnFrame` always runs, but after
   `Render` and `PostRender`.[^ksa-program][^ksa-screenshot]
2. **Three callers, one owner.** `StepOnce` is called from `[StarMapAfterGui]` (preferred: before the
   render, after the viewport pass), from a Harmony **prefix** on the private
   `Program.OnFrameCelestials(double)` (the last point before `OnPreRender` that is not behind
   `DrawUI`), and from `[StarMapAfterOnFrame]`. `FrameLatch.Claim()` lets the first caller run; the
   frame postfix calls `EndFrame()` in a `finally`, because a latch that never clears would stop the
   mod for the session.[^mod-entry][^mod-latch][^mod-prerender]
3. **Prefix, not postfix.** `OnFrameCelestials` resolves the camera's `NearbyCelestial`, its
   altitude and terrain height, and calls the planet renderer's `OnFrame` for that camera.[^ksa-program]
   The step moves cameras, so it has to run before that, as it does on frames with the UI shown.[^mod-prerender]
   If the private method is ever renamed, the patch is skipped with one warning and the frame postfix
   takes over at the cost of a frame of latency.
4. **The simulation clock, consumed once.** StarMap's frame hooks pass player-time deltas, which
   keep running through a pause and ignore timewarp. `Universe.ApplyVehicleSolvers` sets
   `_lastSimStep` to the step it just applied, and `Universe.GetLastSimStep()` returns it.[^ksa-universe]
   `KsaWorld.ConsumeSimStep` hands that step's `DeltaTime` out once per `NextTime` through
   `StepGate<UniverseTime>`, and if a frame was missed entirely (screenshots skip the GUI pass) it
   integrates the span between the last consumed `NextTime` and this one, differenced in `Int128`
   nanoseconds.[^mod-ksaworld][^mod-stepgate]
5. **Vehicles from the system, not the frame list.** `Program.VehiclesInFrame` is refilled in
   `PrepareFrame` and cleared in `PostRender`, so it reads empty from the frame postfix.[^ksa-program]
   `KsaWorld.Vehicles` walks `Universe.CurrentSystem.All` once per step into a shared list
   invalidated at the top of each step.[^mod-ksaworld]
6. **"In flight" is the scene, not the craft.** `InFlightScene` is `Program.Editor is null &&
   Universe.CurrentSystem is not null`; the controlled vehicle can be null mid-flight after a craft
   is destroyed.[^mod-ksaworld]
7. **Seeing a save load.** StarMap has no load hook. `Program.OnGameLoaded()` is called from one
   place, `UncompressedSave.Load`, right after `Universe.DeserializeSave`.[^ksa-uncompressedsave] A
   postfix only sets a flag; the next step consumes it and resets the sim-step tracking.[^mod-reload]
   Described on its own in [noticing a save load](./save-load-detection.md).
8. **Faults degrade, not crash.** Frame work is wrapped in `try/catch`; ten faults call `StandDown`,
   which unpatches every Harmony id and hands every borrowed view back. Harmony patch bodies swallow
   exceptions because they run inside the engine's loop.[^mod-entry]
9. **Pinning private and public targets.** Never-called methods such as
   `WorldReloadHook.PinTheSignature() => Program.OnGameLoaded()` put patched members into the mod's
   metadata, so a signature change in KSA is a build error rather than a silent no-op.[^mod-reload]

## Lifecycle and timing

| StarMap or patch | Fires at (2026.10.10.5554) | The mod does |
|---|---|---|
| `[StarMapImmediateLoad]` | prefix on `Mod.PrepareSystems`, per mod, during `ModLibrary.PrepareAll`[^starmap-mod] | Write the shader include header, keep the `Mod` for texture paths, log versions |
| `[StarMapAllModsLoaded]` | postfix on `ModLibrary.LoadAll`[^starmap-modlibrary] | Place the office, dig, install every Harmony patch, build the UI and bridge |
| `[StarMapBeforeGui]` | prefix on `OnDrawUiFrame` (`frame:draw-ui`) | Menu bar entry |
| `[StarMapAfterGui]` | postfix on `OnDrawUiViewports` (`frame:draw-ui`) | `StepOnce`, then keys and windows |
| prefix `Program.OnFrameCelestials` | after `OnFrameController` and `LightSystem.OnFrame`, before `OnPreRender` (`frame:celestials`) | `StepOnce` on hidden-UI frames |
| `[StarMapAfterOnFrame]` | postfix on `Program.OnFrame` (`frame:after-on-frame`) | `StepOnce` fallback, bridge, `EndFrame` |
| postfix `Program.OnGameLoaded` | after `Universe.DeserializeSave` (`startup:game-loaded`) | Latch "a save was loaded" |
| `[StarMapUnload]` | mod unload | Stand down, restore the stock camera controller, flush the log |

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L14-L214) | Entry class with every StarMap hook |
| [src/KSAStructures/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/FrameLatch.cs#L15-L36) | First-caller-wins latch |
| [src/KSAStructures/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/PreRenderHook.cs#L37-L130) | Prefix on `Program.OnFrameCelestials` |
| [src/KSAStructures/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/WorldReloadHook.cs#L31-L123) | Postfix on `Program.OnGameLoaded` |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L40-L114) | `InFlightScene`, `ConsumeSimStep` |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L194-L264) | Shared vehicle census |
| [src/KSAStructures/Sim/StepGate.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/StepGate.cs#L17-L87) | Step dedupe and gap closing |
| [tests/KSAStructures.Tests/FrameLatchTests.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tests/KSAStructures.Tests/FrameLatchTests.cs) | Latch behaviour |
| [tests/KSAStructures.Tests/SkippedFrameTests.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tests/KSAStructures.Tests/SkippedFrameTests.cs) | Missed-frame integration |

## Snippets

The step, claimed once, on the simulation clock:

```csharp
// src/KSAStructures/Ksa/KSAStructuresMod.cs L106-L137
    private void StepOnce(double dtPlayer)
    {
        if (_disabled || _ui is null) return;
        if (!_frame.Claim()) return;

        KsaWorld.InvalidateCensus();

        if (WorldReloadHook.ConsumeReload())
        {
            Log.Info("a save was loaded - forgetting the previous world");
            KsaWorld.ResetSimStepTracking();
        }

        // The scene, not the craft: KSA clears the controlled vehicle when it is destroyed and the
        // flight carries straight on.
        if (KsaWorld.InFlightScene)
        {
            // Consumed, not peeked: the engine answers with the last step, so asking twice without
            // it having stepped returns the same one.
            double dtSim = KsaWorld.ConsumeSimStep();
            _lastSimStep = double.IsFinite(dtSim) && dtSim > 0.0 ? dtSim : 0.0;

            Office.Update(_lastSimStep);
        }

        // Outside the flight gate, so a view is still handed back on the way out. Driving a borrowed
        // view is not drawing: with the UI hidden the GUI pass does not run, and a view nothing
        // restates freezes at its last offset while the world carries on.
        _firstPerson?.Drive();
        _computer.Drive(dtPlayer);
        _bridge?.DriveCamera();
    }
```

The frame postfix that always releases the latch:

```csharp
// src/KSAStructures/Ksa/KSAStructuresMod.cs L78-L101
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        try
        {
            if (_disabled || _ui is null) return;

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

Stepping before the render when the UI is hidden:

```csharp
// src/KSAStructures/Ksa/PreRenderHook.cs L64-L78, L112-L129
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
    // ...
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

Consuming the engine's applied step exactly once:

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L77-L97
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

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `Program.OnFrame`, `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2335) | Frame order and the UI guard |
| `Program.OnFrameCelestials` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2733) | Hidden-UI pre-render point |
| `Program.OnGameLoaded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606) | Save-load signal |
| `Program.VehiclesInFrame`, `RefreshVehiclesInFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L474-L590) | Per-frame list, empty after `PostRender` |
| `Program.Editor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Scene test |
| `Universe.GetLastSimStep`, `ApplyVehicleSolvers`, `CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1950) | The applied step |
| `UncompressedSave.Load` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L60-L82) | Only caller of `OnGameLoaded` |
| StarMap `ProgramPatcher`, `ModLibraryPatches`, `ModPatches` | [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs) | Where the attributes fire |

## Pitfalls and version notes

- **The GUI hooks are skipped with the UI hidden**, including during KSA's screenshot capture.
  Anything that must keep running needs a second, ungated hook.
- **Do not integrate StarMap's `dtPlayer`** for anything that moves with the world; it runs during
  pause and ignores warp.
- **`GetLastSimStep` reports the last applied step, not "a step since you asked"**; reading it twice
  without consuming double-counts motion.
- **Cameras are driven on player time**, outside the flight gate, so borrowed views keep up while
  paused and are still handed back on the way out of flight.[^mod-entry]
- **`OnFrameCelestials` is private.** It is patched by name with `AccessTools.Method`; a rename
  degrades to the frame postfix with a logged warning.
- **Every hook the step can run from is after `frame:dispatch-solvers`.** The vehicle solver step for
  this frame is already running on worker threads, so the step must not write simulated state
  directly. The mod's features route around this: rider moves and wake-ups go through the
  `Vehicle.PrepareWorker` prefix of the next dispatch ([lift](./lift-moving-landmark.md)), craft
  spawns take the shapes lock with `ConstraintSim.UnlockShapesBlocking`, which waits for the worker,
  and placement goes through `TeleportToLocation`, which only queues an input event
  ([furniture](./office-furniture-craft.md)). The lift's `GroundOffset` write is read by a worker that
  may be mid-step, which the lift absorbs by drawing the car one step ahead.
- **`StarMapUnload` runs after every patch is already gone.** At exit `StarMapCore.DeInit` calls
  `Harmony.UnpatchAll()` with no id before it invokes `[StarMapUnload]`, so the `UnpatchAll(id)` calls
  in the mod's `StandDown` do nothing at shutdown; they matter for the in-session stand-down after ten
  faults.[^starmap-core][^mod-entry]

## See also

- [Settings menu](./settings-menu.md) - what runs from `StarMapBeforeGui`
- [Per-mod log file](./mod-log-file.md) - what `Unload` flushes
- [Lift as a moving landmark](./lift-moving-landmark.md) - the main consumer of the sim step
- [Headless tests and CI](./headless-tests-and-ci.md) - `FrameLatch` and `StepGate` tests
- [Noticing a save load](./save-load-detection.md) - the `Program.OnGameLoaded` latch the step consumes
- [Wrap every frame hook in try/catch and disable the mod after ten faults](../../../howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md) - how-to article demonstrating this feature
- [Run per-frame mod logic once per frame from whichever hook fires first (FrameLatch, pre-render prefix and sim-time steps)](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-4.md) - how-to article demonstrating this feature

[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-latch]: src/KSAStructures/Sim/FrameLatch.cs
[^mod-prerender]: src/KSAStructures/Ksa/PreRenderHook.cs
[^mod-reload]: src/KSAStructures/Ksa/WorldReloadHook.cs
[^mod-ksaworld]: src/KSAStructures/Ksa/KsaWorld.cs
[^mod-stepgate]: src/KSAStructures/Sim/StepGate.cs
[^mod-claude]: KSAStructures CLAUDE.md (frame plumbing section)
[^starmap-program]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-modlibrary]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-mod]: StarMap.Core/Patches/ModPatches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-screenshot]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
