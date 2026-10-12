---
type: How-To
title: Count consecutive failures and restore the stock HUD when the mod disables itself
description: Disable a mod only after ten failures in a row, log the first failure of each streak, and undo visible side effects such as hidden gauge canvases
category: getting-started
group: lifecycle
topic: contain-exceptions-in-starmap-hooks
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/error-budget-self-disable]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Program, KSA.GaugeCanvas]
tags: [starmap, exceptions, error-budget, consecutive-failures, hud, self-disable]
---

KSA-Public-Telemetry warns in its README that it "will break very regularly" while KSA is pre-alpha. Its entry
class is built so a game update costs one log line and, if it keeps failing, a clean shutdown of the overlay with
the stock HUD back on screen. Unlike the cumulative counter of [Per-hook try/catch feeding a shared fault counter, unpatch at ten](./contain-exceptions-in-starmap-hooks-1.md), this
budget counts **consecutive** failures only, so an occasional fault never retires the mod. The mod also *hides*
parts of the game UI, which makes the clean-up part of the contract.

## Background: how the game does it

Exceptions from StarMap hooks are not handled by StarMap or KSA (see
[Per-hook try/catch feeding a shared fault counter, unpatch at ten](./contain-exceptions-in-starmap-hooks-1.md)), so containment is the mod's job. The side effect to undo here is the
mod's suppression of `GaugeCanvas` instances: `GaugeCanvas.AllCanvases` lists them, and `SetEnabled` merely flips a
flag that the canvases read the next time they draw, so restoring from inside a failing frame is safe.[current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L664-L667)

## Lifecycle and timing

| Hook | Lifecycle | Role |
|---|---|---|
| `StarMapAllModsLoaded` | `startup:all-mods-loaded` | startup; failures call `Fail("starting up", ex)` but leave `_config`/`_renderer` null, making every later hook inert |
| `StarMapAfterOnFrame` | `frame:after-on-frame` | sampling; success resets the streak |
| `StarMapAfterGui` and a `DrawFps` postfix | `frame:draw-ui` | drawing; success resets the streak |

The counter is shared between sampling and drawing, so a failure in one and a success in the other within the same
frame resets the streak: only a hook that fails every time trips the budget. On frames where `PrepareFrame`
returns `Exit` only the sampling hook runs.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2268) See [StarMap lifecycle attributes](../game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Guard every hook, report success.** Startup, sampling and drawing each run in `try`/`catch` that calls
   `Fail(stage, ex)`; sampling and drawing also call `Succeeded()` on the happy path:[TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L147)

   ```csharp
   // TelemetryOverlayMod.cs L65-L82
       [StarMapAfterOnFrame]
       public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
       {
           if (_disabled || _config is null)
           {
               return;
           }

           try
           {
               TelemetrySampler.Sample(_snapshot, _config, dtPlayer);
               HiddenUiPatch.RecordDelta(dtPlayer);
               Succeeded();
           }
           catch (Exception ex)
           {
               Fail("sampling", ex);
           }
       }
   ```

2. **Log the first failure of a streak with the exception, count the rest quietly, and trip at the limit:**[TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L204-L236)

   ```csharp
   // TelemetryOverlayMod.cs L204-L236
       private const int FailureTolerance = 10;
       private void Succeeded()
       {
           _consecutiveFailures = 0;
       }

       private void Fail(string stage, Exception ex)
       {
           _consecutiveFailures++;

           if (_consecutiveFailures == 1)
           {
               Console.WriteLine(LogPrefix + $"error while {stage}: {ex}");
           }

           if (_consecutiveFailures < FailureTolerance)
           {
               return;
           }

           _disabled = true;
           Console.WriteLine(
               LogPrefix + $"disabled after {FailureTolerance} consecutive errors while {stage}.");

           try
           {
               _renderer?.RestoreFlightUi();
           }
           catch (Exception restoreEx)
           {
               Console.WriteLine(LogPrefix + $"could not restore the flight HUD: {restoreEx.Message}");
           }
       }
   ```

3. **Undo visible side effects.** The mod keeps the list of canvases it disabled and re-enables those that still
   exist:[Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs#L73-L95)

   ```csharp
   // Rendering/FlightUiController.cs L73-L95
       public void Restore()
       {
           _hidden = false;
           _seenCanvasCount = -1;

           if (_suppressed.Count == 0)
           {
               return;
           }

           IReadOnlyList<GaugeCanvas> canvases = GaugeCanvas.AllCanvases;

           for (int i = 0; i < _suppressed.Count; i++)
           {
               GaugeCanvas canvas = _suppressed[i];

               if (canvases.Contains(canvas))
               {
                   canvas.SetEnabled(true);
               }
           }

           _suppressed.Clear();
       }
   ```

   Wrap the restore itself in a `try`/`catch` (as above) so a failing clean-up cannot raise from the failure
   handler.
4. **Check `_disabled` first in every hook**, so the mod is inert for the rest of the session.
5. **Let subsystems degrade on their own.** The hidden-UI patch, fonts and config each catch their own failures
   and fall back (overlay hides with F2, game font, default config), so they never feed the counter.
6. **Log through the console.** All messages use `Console.WriteLine` with a `[KSATelemetryOverlay]` prefix;
   a per-mod file logger is covered in [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md).

## Complete example

[TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs) (entry class and the budget) and [Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs) (suppression and restore).

## Variations and gotchas

- **Alternating failures never trip.** A draw that always throws while sampling always succeeds keeps resetting the
  counter, and the draw then logs its exception once per frame (every new streak). Keep separate counters per stage
  to avoid that.
- **No re-enable** short of restarting the game.
- **Restore from a failing frame is safe** because `SetEnabled` only flips a flag read at the next draw; if your
  side effect is not a flag, defer its undo to a later hook.
- **Choose the policy:** consecutive (this article) tolerates rare faults but can hide a fault that always
  alternates with a success; cumulative ([Per-hook try/catch feeding a shared fault counter, unpatch at ten](./contain-exceptions-in-starmap-hooks-1.md)) retires the mod eventually but
  surfaces every fault.
- **StarMapUnload and `Environment.Exit`:** clean-up on the exit path is a separate topic,
  [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md).

## See also

- [Per-hook try/catch feeding a shared fault counter, unpatch at ten](./contain-exceptions-in-starmap-hooks-1.md), [Keep mod exceptions out of KSA's frame loop](./overview.md)
- [Error budget and self-disable](../../../../mods/ksa-public-telemetry/features/error-budget-self-disable.md)
- `isolate-patch-failures-and-fault-proof-patch-bodies`, [HashSet-keyed WarnOnce/ErrorOnce with the exception type](../write-a-per-mod-log-file/write-a-per-mod-log-file-7.md)
