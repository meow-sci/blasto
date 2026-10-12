---
type: How-To
title: Wrap every frame hook in try/catch and disable the mod after ten faults
description: Feed hook exceptions into a shared fault counter, log each one, and past a limit switch the mod off for the session by removing its patches and dropping its state
category: getting-started
group: lifecycle
topic: contain-exceptions-in-starmap-hooks
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/starmap-entry-and-fault-containment
  - mods/ksacars/features/mod-log-file
  - mods/ksastructures/features/frame-step-plumbing
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame, event:shutdown]
game_types: [KSA.Program, KSA.App]
tags: [starmap, exceptions, fault-containment, fault-counter, stand-down, hooks]
---

StarMap invokes your frame hooks with `MethodInfo.Invoke` and no handler, and KSA's frame loop has no handler
either, so an exception in a per-frame hook unwinds out of `Program.Main` and ends the process. KSACars and
KSArmory (same author, same pattern) wrap each hook in `try`/`catch`, count faults in one counter, and at ten
faults disable the mod and remove its patches instead of logging an exception every frame. A variant that counts
only consecutive failures is [Consecutive-failure budget that restores the stock HUD](./contain-exceptions-in-starmap-hooks-2.md). Wrapping the bodies of Harmony patches is
the separate topic `isolate-patch-failures-and-fault-proof-patch-bodies`.

## Background: how the game does it

The chain from the window to your code has no catch anywhere:

- `App.Run` calls `OnFrame(totalSeconds, dtPlayer)` in a bare `while` loop;[current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L39-L70)
- `Program.OnFrame` calls `PrepareFrame`, the draw hooks and render in sequence, again without a handler (the
  `DrawUI` guard only skips the GUI hooks);[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2290)
- StarMap's patches loop over your registered methods calling `method.Invoke`:[StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L12-L48)

```csharp
// StarMap.Core/Patches/ProgramPatcher.cs L38-L48
        [HarmonyPatch(OnFrameMethodName)]
        [HarmonyPostfix]
        public static void AfterOnFrame(double currentPlayerTime, double dtPlayer)
        {
            var methods = StarMapCore.Instance?.Loader.ModRegistry.Get<StarMapAfterOnFrameAttribute>() ?? [];

            foreach (var (_, @object, method) in methods)
            {
                method.Invoke(@object, new object[] { currentPlayerTime, dtPlayer });
            }
        }
```

`OnDrawUiViewports` (the `StarMapAfterGui` postfix target) also has no catch. Load-time hooks
(`OnImmediateLoad`, `OnFullyLoaded`) are not wrapped by anyone either; an exception there propagates into
`ModLibrary.PrepareAll` or the `LoadAll` postfix.

## Lifecycle and timing

| Hook | When | Guard |
|---|---|---|
| `StarMapAfterGui` | `frame:draw-ui`, inside `if (DrawUI)`; skipped when the HUD is hidden | catch, count as `gui` |
| `StarMapAfterOnFrame` | `frame:after-on-frame`, every `OnFrame` return path | catch, count as `frame`; `finally` releases per-frame latches |
| `StarMapBeforeGui` | `frame:draw-ui` | KSArmory swallows without counting (cosmetic menu entry) |
| `StarMapUnload` | `event:shutdown`, after the game exited | clean-up only |

`StarMapAfterOnFrame` is the only hook guaranteed every frame, so work that must always run (releasing a frame
latch) lives in its `finally`. Frame order in [StarMap lifecycle attributes](../game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Add the counter and the switch** to the `[StarMapMod]` class. A disabled mod costs one branch per hook:

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L18-L28
       private const int FaultLimit = 10;
   // ...
       private int _faults;
       private bool _disabled;
   ```

2. **Wrap every frame hook; early-return when disabled.** KSACars' frame hook:[src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L59-L82)

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L59-L82
       [StarMapAfterOnFrame]
       public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
       {
           try
           {
               if (_disabled) return;

               // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
               StepOnce();

               // After the step, so a command sees this frame's world.
               _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? KsaWorld.SimStepSeconds : 0.0);
           }
           catch (Exception e)
           {
               Fault("frame", e);
           }
           finally
           {
               // Unconditionally: this is the only hook KSA always calls, so a release skipped by an early
               // return or an exception would stop the mod for the rest of the session.
               _frame.EndFrame();
           }
       }
   ```

3. **Count, log, then stand down.** `Fault` increments, logs with the exception (`n/10`), and at the limit sets
   `_disabled`, silences sound, clears state and removes every hook the mod installed:[src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L154-L176)

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L154-L176
       private void Fault(string where, Exception e)
       {
           _faults++;
           Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

           if (_faults < FaultLimit) return;

           _disabled = true;
           _buggySound.StopAll();
           _rocketSound.StopAll();
           _buggies.Clear();
           PhysicsHook.Remove();
           SeatedCrewHook.Remove();
           LightsHook.Remove();
           LensColourHook.Remove();
           FlamesHook.Remove();
           CrashHook.Remove();
           BoostHook.Remove();
           HudHook.Remove();
           RailsHook.Remove();
           Log.Error("too many faults - cars disabled for this session");
       }
   ```

   KSArmory's `Fault` is the same shape with different clean-up (rosters, effects, five patches).[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1453-L1476)
4. **Give patches their own try/catch.** Code that runs inside an engine method (Harmony prefixes and postfixes)
   is not on the hook call stack, so the hook's `Fault` never sees it; each patch body logs once and stands down on
   its own. (See `isolate-patch-failures-and-fault-proof-patch-bodies`.)
5. **Keep load-time installers fail-soft.** Each installer (`PhysicsHook.Install()` and the others) should catch
   its own failure; the wrapper above does not cover `OnFullyLoaded` or `OnImmediateLoad`.
6. **Do not rely on `StarMapUnload` for patch removal.** It runs after StarMap's id-less `UnpatchAll()`
   ([StarMapUnload plus ProcessExit for exit paths that skip it](../clean-up-on-game-exit/clean-up-on-game-exit-1.md)), so removal logic is for the fault path, which runs mid-session.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | The compact version: two guarded frame hooks, `Fault`, `StepOnce` latch |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | The extended version: six hooks, `FrameBudget` self-measurement, `OverrunLog` |

## Variations and gotchas

- **The counter never resets** (ten per session, not ten in a row), so a rare fault eventually disables a long
  session; [Consecutive-failure budget that restores the stock HUD](./contain-exceptions-in-starmap-hooks-2.md) counts consecutive failures instead.
- **Uncounted paths.** KSArmory's `OnBeforeGui` swallows without counting, so a menu entry that throws every frame
  never trips the limit. A fault inside `StepOnce` reached from a pre-render prefix is caught by that hook, not
  counted.
- **Factor the clean-up into one `StandDown`.** KSAStructures (same author) puts every release and `Remove()`
  call in a `StandDown()` method that both `Fault` (at its tenth fault) and `StarMapUnload` call, so the two
  paths cannot drift apart; its frame step is guarded by the same `try`/`catch`/`Fault` shape.[src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L190-L213)
- **Remove everything you installed.** KSArmory's `CloudPassHook.Remove` exists but has no caller, so one patch
  survives a stand-down; audit that `Fault` removes every installer.
- **Dispatch order.** All hooks of all mods share the same call; one faulting mod's `finally` still runs, but an
  uncaught exception in any mod's hook ends the frame for everyone after it.
- **Log the first with detail.** Log rate-limiting is in [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md).

## See also

- [Consecutive-failure budget that restores the stock HUD](./contain-exceptions-in-starmap-hooks-2.md), [Keep mod exceptions out of KSA's frame loop](./overview.md)
- [StarMap entry class with per-hook fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md), [Per-mod log file, build stamp and fault limiter](../../../../mods/ksacars/features/mod-log-file.md),
  [One step per frame across three hooks, on the simulation clock](../../../../mods/ksastructures/features/frame-step-plumbing.md)
- [Choose and use the right StarMap lifecycle hook](../choose-a-starmap-startup-hook/overview.md), [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md), [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md)
