---
type: Mod Feature
title: Error budget and self-disable
description: Wraps every per-frame entry point in try/catch, logs only the first exception of a streak, and after ten consecutive failures disables the whole mod and restores the stock flight HUD so a game update cannot leave the player with a broken screen
mod: ksa-public-telemetry
category: getting-started
techniques: [starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterOnFrame
  - starmap StarMapAfterGui
  - harmony-postfix KSA.Program.DrawFps
lifecycle: [startup:all-mods-loaded, frame:after-on-frame, frame:draw-ui]
game_types: [KSA.GaugeCanvas]
tags: [robustness, error-handling, logging, starmap]
---

The README warns that the mod "will break very regularly" while KSA is in pre-alpha.[^kpt-readme] The
entry class is built for that: a missing member or changed behaviour after a game update produces one
log line and, if it keeps happening, a clean shutdown of the overlay with the stock HUD back on screen,
instead of an exception every frame.

## How it works

1. **Every hook is guarded.** Startup, sampling and drawing each run inside a `try`/`catch` that calls
   `Fail(stage, ex)` on error; sampling and drawing also call `Succeeded()` on success. A startup
   failure leaves `_config` or `_renderer` null, and every later hook returns early on that, so the mod
   goes inert without touching the counter again ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L39-L147)).
2. **Log once per streak.** `Fail` increments a consecutive-failure counter and prints the full exception
   only when the counter is 1; any success resets it ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L204-L236)).
3. **Disable after ten.** At ten consecutive failures `_disabled` is set, a final log line names the
   stage, and `RestoreFlightUi()` re-enables the `GaugeCanvas`es the mod had hidden. Every hook checks
   `_disabled` first, so the mod goes inert for the rest of the session.
4. **Subsystems degrade on their own.** The hidden-UI patch, the fonts and the config each catch their
   own failures and fall back (overlay hides with F2, game font, default config), so they never feed
   the counter ([HiddenUiPatch.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/HiddenUiPatch.cs#L19-L38), [Rendering/OverlayFonts.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayFonts.cs#L54-L61),
   [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L76-L82)).
5. **Prefixed console logging.** All messages go through `Console.WriteLine` with a
   `[KSATelemetryOverlay]` prefix ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L13)).

## Lifecycle and timing

- The counter is shared between `frame:after-on-frame` (sampling) and `frame:draw-ui` (drawing, from
  `[StarMapAfterGui]` or the `DrawFps` postfix), so a failure in one and a success in the other within
  the same frame resets the streak; only a hook that fails every time trips the budget. On frames where
  `PrepareFrame` returns `Exit` only the sampling hook runs.
- Restoring the HUD from inside a failing frame is safe: `GaugeCanvas.SetEnabled` only flips a flag that
  the canvases read the next time they draw ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L664-L667)).[^ksa-gaugecanvas]

## Key code

| Path | Role |
|---|---|
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L204-L236) | Failure counter, log-once, disable and restore |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L65-L83) | Guarded sampling hook |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `GaugeCanvas.SetEnabled` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L664-L667) | Hand the HUD back on shutdown |

## Pitfalls and version notes

- **Alternating failures never trip.** As noted above, a draw that always throws while sampling always
  succeeds keeps resetting the counter; the draw then logs its exception once per frame (every new
  streak). Separate counters per stage would avoid that.
- **No re-enable** short of restarting the game.

## See also

- [Hide and restore the stock flight HUD](hide-and-restore-stock-flight-hud.md)
- [Keep drawing while the game UI is hidden](draw-while-ui-hidden-drawfps-postfix.md)
- [Count consecutive failures and restore the stock HUD when the mod disables itself](../../../howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-2.md) - how-to article demonstrating this feature
- [Log with HashSet-keyed WarnOnce and ErrorOnce that include the exception type](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-7.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
