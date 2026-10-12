---
type: How-To
title: Fault counter that pulls every patch after a limit
description: Count exceptions in the frame wrappers, and after ten failures disable the mod for the session, release everything it holds and remove every patch class so every later hook returns at its first line
category: patching
group: safety
topic: isolate-patch-failures-and-fault-proof-patch-bodies
approach: 5
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/dual-hook-frame-stepping, mods/ksagolf/features/degrading-harmony-patches]
techniques: [harmony-prefix, fault-counter]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame, starmap StarMapUnload]
lifecycle: [frame:draw-ui, frame:after-on-frame, event:starmap-unload]
game_types: [KSA.Program]
tags: [harmony, fault-limit, disable, remove, safety, frame-hook]
---

A patch that throws every frame breaks the method it decorates, and a log that records the same exception sixty times a
second helps nobody. KSAGolf wraps all frame work in a fault counter: each failure is logged with the count, and at ten the mod
sets a disabled flag, releases the objects it holds in the game and calls `Remove()` on every patch class. Every later hook
returns at its first line, so the game carries on as if the mod were not installed.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by KSAGolf in [Dual-hook frame stepping on simulated time](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md) and
[Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md).

## Background: which hooks can throw

KSAGolf's per-frame work arrives through two StarMap hooks and one Harmony prefix. `[StarMapAfterGui]` is a postfix on
`Program.OnDrawUiViewports`, inside `if (DrawUI)`; `[StarMapAfterOnFrame]` is a postfix on `Program.OnFrame`; the
`OnFrameCelestials` prefix is a fallback for hidden-HUD frames ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2269)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2319-L2323)).
An exception in any of them is an exception in the engine's frame loop.

## Lifecycle and timing

The counter runs inside whichever of `[StarMapAfterGui]` (`frame:draw-ui`) or `[StarMapAfterOnFrame]`
(`frame:after-on-frame`) caught the tenth exception. The removal runs on the main thread from a StarMap hook; at that point
the `ConstraintSim` prefixes may be executing on vehicle workers, because the next physics step was dispatched earlier in the same
frame. The mod does not wait for the solver before unpatching, and whether Harmony tolerates unpatching a method that is mid-call
was not checked here.

`Remove()` from `[StarMapUnload]` is a no-op in practice: StarMap calls it at process exit, right after an id-less
`UnpatchAll()`. The fault-limit path is the only one where `Remove()` takes patches out of a running game
([UnpatchAll(ownId) from a main-thread hook after nulling delegates](../remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Wrap every hook body in `try`/`catch` and route the catch to one `Fault` method.** The unconditional `finally` in the one
   hook that always runs releases the per-frame latch, so a skipped release cannot stop the mod for the session:

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

Source: [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L74-L108)[^ksagolf-ksagolfmod]

2. **Do the same in the GUI hook.** Its `catch` also calls `Fault`:

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L185-L189
    catch (Exception e)
    {
        Fault("gui", e);
    }
}
```

Source: [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L185-L189)

3. **Count, log, and at the limit disable and unpatch.** Release what the mod borrowed from the game first, then remove every patch
   class; later hooks check `_disabled` at their first line:

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L222-L240
private void Fault(string where, Exception e)
{
    _faults++;
    Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

    if (_faults < FaultLimit) return;

    _disabled = true;
    _mover.Release();
    _builder?.Release();
    _golf?.Release();
    ViewDrawHook.Remove();
    FirstPersonHook.Remove();
    PadSurface.Remove();
    CourseCollider.Remove();
    PreRenderHook.Remove();
    WorldReloadHook.Remove();
    Log.Error("too many faults - KSAGolf disabled for this session");
}
```

Source: [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L222-L240)

4. **Make `Remove()` safe to call at any time.** Each patch class nulls its delegate first so a stale patch cannot reach a dead
   mod, then unpatches by its own id and resets its state:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L103-L110
public static void Remove()
{
    try { _harmony?.UnpatchAll(HarmonyId); } catch { /* Unloading anyway. */ }
    _harmony = null;
    Installed = false;
    _wanted = null;
    _ready = null;
}
```

Source: [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L103-L110)[^ksagolf-coursecollider]

5. **Make the same code serve unload.** `Unload()` repeats the releases and removals (and also restores the stock camera
   controller), so a clean exit and a fault-limit exit leave the same state ([src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L191-L214)).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs) | Hooks, `Fault`, `Unload` |
| [src/KSAGolf/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/FrameLatch.cs)[^ksagolf-framelatch] | The latch released in `finally` |
| [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs)[^ksagolf-prerenderhook] | A patch class with `Remove()` that nulls its delegate |

## Variations and gotchas

- **A release that is skipped stops the mod.** The latch release must be unconditional and in the hook that is never skipped.
- **Cumulative, not consecutive.** `_faults` is incremented on every failure and never reset, so ten failures anywhere in a
  session end it; if you want only consecutive failures, reset the counter in the success path.
- **Restore what you borrowed first.** `Fault` releases the mover, builder and putting session before unpatching, so the game
  gets its camera and state back.
- **Disabled is for the session.** Nothing re-enables the mod; the log's last line says so.

## See also

- [CreateClassProcessor per patch class, required versus optional](isolate-patch-failures-and-fault-proof-patch-bodies-1.md), [Consolidated patcher with TryApply and ordered shared seams](isolate-patch-failures-and-fault-proof-patch-bodies-2.md), [Static patch classes that look up the target, log once and stand down](isolate-patch-failures-and-fault-proof-patch-bodies-3.md), [Reflection-only mod with parameter-name and field-type pre-checks](isolate-patch-failures-and-fault-proof-patch-bodies-4.md)
- [Isolating patch failures and keeping patch bodies from throwing](overview.md)
- [Dual-hook frame stepping on simulated time](../../../../mods/ksagolf/features/dual-hook-frame-stepping.md), [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md)
- Related topic: contain-exceptions-in-starmap-hooks

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksagolf-ksagolfmod]: KSAGolf/src/KSAGolf/Ksa/KSAGolfMod.cs
[^ksagolf-coursecollider]: KSAGolf/src/KSAGolf/Ksa/CourseCollider.cs
[^ksagolf-framelatch]: KSAGolf/src/KSAGolf/Sim/FrameLatch.cs
[^ksagolf-prerenderhook]: KSAGolf/src/KSAGolf/Ksa/PreRenderHook.cs
