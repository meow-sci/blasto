---
type: Mod Feature
title: Hold timewarp down while rounds or burns are in flight
description: A closed-loop policy that reads the step KSA applied, writes Universe.SetSimulationSpeed down to the speed whose step a round can integrate, waits out the two-frame actuator delay, yields to the player or auto-warp, and gives the speed back when the air is clear
mod: ksarmory
category: flight-and-orbits
techniques: [starmap-lifecycle, control-loop, sim-clock]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - starmap StarMapUnload
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, event:starmap-unload]
game_types: [KSA.Universe, KSA.SimSpeed, KSA.Program, KSA.InputEvents]
tags: [timewarp, simulation-speed, control-loop, auto-warp, integration-step, accuracy]
---

KSArmory integrates its rounds on the simulated step, and past 0.32 s a round moves further per step
than its fuse radius and can pass through a target without measuring a close approach. Clamping the
round's step instead leaves it behind the world (the author measured a 124 km closest approach at
600x against 15-20 m unwarped). So while anything is in the air, or a ballistic computer is burning,
the mod holds the world's simulation speed down to the speed whose step the fussiest round can
take, and restores the player's speed when the sky is clear. It is written as a control loop
against an actuator that answers two frames late and is shared with the player and KSA's
auto-warp.[^ksarmory-warppolicy]

## How it works

Each step, `ApplyWarpPolicy(dtSim)` gathers:[^ksarmory-mod]

- `anyInFlight` and the hold-to step `WeaponSystems.WarpTargetStep()`: the smallest
  `FaithfulStepSeconds` any round needs now (a coasting warhead in vacuum tolerates more than the
  same warhead entering the atmosphere).[^ksarmory-weaponsystems]
- `IcbmComputers.FaithfulStep(out anyBurning)`: `IcbmProgram.MaxFaithfulStep` (0.3 s) while a computer
  needs short steps and is not waiting on KSA's auto-warp.[^ksarmory-icbmcomputers]
- `KsaWorld.SimulationSpeed`, `KsaWorld.IsAutoWarpActive`, and `Config.LimitWarpInFlight` (default on).[^ksarmory-config]

`WarpPolicy.Decide` (no KSA types) returns `None`, `Slow(speed)`, `Restore(speed)`, `Yield` or
`Abandon`:

| Guard, in order | Result |
|---|---|
| Setting off, or nothing in the air | `Release`: `Restore` to the held speed only if the world is still at the speed the mod asked for (within 5%), else nothing |
| Non-finite step, or speed <= 0 (paused, menu pause) | nothing |
| KSA auto-warp running | nothing, and any pending request is dropped |
| Already yielded this salvo | nothing |
| Holding, waiting for the write to read back | read back within 5%: start `SettleSteps = 1`; not after `FramesAwaitingWrite = 4` frames: `Abandon` |
| Holding, settling | count down, nothing |
| Holding, speed raised by someone else more than `OverridesBeforeYielding = 2` times | `Yield` |
| `dtSim <= faithful` | nothing |
| `dtSim / speed > 1 s` (a hitch, e.g. the frame a speed change lands on) | nothing |
| Otherwise | `target = speed x (0.6 x faithful) / dtSim`, floored at 1.0x; if below the current speed: remember the speed to restore (first time) and `Slow(target)` |

The formula needs no frame rate: `dtSim / speed` is the frame time, so the target lands the next
step at 60% of the limit. The floor at real time means the mod never slows the game below 1x; at
that point the round takes the coarser step.

`ApplyWarpPolicy` acts on the decision: `Slow`/`Restore` call `KsaWorld.SetSimulationSpeed`
(which rejects non-finite or non-positive values and clamps to at least 0.001x, because `SimSpeed`
names anything under 1e-4 "paused"); `Yield` logs a warning; `Abandon` makes every system,
including loose ones, abandon its rounds and stands every burning ballistic computer down.[^ksarmory-ksaworld][^ksa-simspeed]
`NoteOurOwnRequest(speed)` lets the mod's own scenario runner change the speed without being
counted as an override.[^ksarmory-scenariorunner] `[StarMapUnload]` restores a held speed.

## Lifecycle and timing

`ApplyWarpPolicy` runs inside `StepSimulation`, right after `ConsumeSimStep`, wherever the frame's
step runs: the `[StarMapAfterGui]` postfix (Program.cs L2268), the `OnFrameCelestials` prefix (L2320)
or the `OnFrame` postfix. The write and its effect, build 2026.10.10.5554:[^ksa-program][^ksa-universe]

| Frame | What happens to a speed written in frame k's hook |
|---|---|
| k (hook) | `Universe.SetSimulationSpeed(SimSpeed)` assigns `_simulationSpeed` directly (Universe.cs L700-L703); `Universe.SimulationSpeed` reads it back at once (L122-L131) |
| k+1 `PrepareFrame` | `ApplyVehicleSolvers` (L2167) applies the step dispatched in frame k at the **old** speed; `UpdateMenuPause` (L2213, Program.cs L2580-L2597) and `ProcessAutoWarp` (L2215; Universe.cs L2208-L2244) may overwrite the speed; `GetJobSimStep` (L2216) sizes the next step with the new speed |
| k+1 (hook) | `dtSim` is still the old-speed step; the policy sees the read-back and starts settling |
| k+2 `PrepareFrame` | the first new-speed step is applied (`_lastSimStep = _nextSimStep`, Universe.cs L1948) |
| k+2 (hook) | `SettleSteps` lets this step pass; the loop judges again from k+3 |

Player warp keys and the console `simspeed` command go through `InputEvents.SimSpeedBuffer`, applied
in `ApplyInputEvents` (Program.cs L2181; InputEvents.cs L1124) before `GetJobSimStep`, so a player
change in frame k+1 wins over the mod's write from frame k.[^ksa-inputevents] Writing the speed from
any main-thread hook is safe; it only affects steps not yet sized.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/WarpPolicy.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WarpPolicy.cs) | Decision logic, constants, `NoteOurOwnRequest`, `Release`, `Clear` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1384-L1451) | `ApplyWarpPolicy`: gather inputs, act on the decision |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L988-L997) | Restore on unload |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L147-L273) | `IsPaused`, `SimulationSpeed`, `SlowestSimSpeed`, `IsAutoWarpActive`, `TryAutoWarpTo`, `StopAutoWarp`, `SetSimulationSpeed`, `SetPaused` |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L95-L160) | `FaithfulStep` (clamp) vs `WarpTargetStep` (hold) |

## Snippets

```csharp
// src/KSArmory/Sim/WarpPolicy.cs L208-L247
        if (dtSim <= faithfulStep) return WarpDecision.Nothing;

        // Not on a hitch. The frame a speed change lands on is worth seconds of wall clock, and the
        // rate inferred from it is the hitch rather than the machine -- see MaxPlausibleFrameSeconds.
        if (dtSim / currentSpeed > MaxPlausibleFrameSeconds) return WarpDecision.Nothing;

        // Self-calibrating: the frame time is dtSim/currentSpeed, so the speed that lands on the
        // target step needs no knowledge of the frame rate. That also makes a slow frame and a
        // high warp the same problem, which to a round they are.
        double target = currentSpeed * (faithfulStep * Margin) / dtSim;

        // ...
        target = Math.Max(target, RealTime);

        if (!double.IsFinite(target) || target <= 0.0 || target >= currentSpeed)
        {
            return WarpDecision.Nothing;
        }

        bool first = !Holding;
        if (first) _restoreTo = currentSpeed;

        _requested = target;
        _awaitingWrite = true;
        _framesAwaiting = 0;

        return new WarpDecision(WarpAction.Slow, target,
                                first
                                    ? $"holding {currentSpeed:F0}x down to {target:F1}x while rounds fly"
                                    : $"{currentSpeed:F1}x still overruns; asking for {target:F1}x");
    }
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L241-L247
    public static bool SetSimulationSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed <= 0.0) return false;

        Universe.SetSimulationSpeed(new SimSpeed(Math.Max(speed, SlowestSimSpeed)));
        return true;
    }
```

```csharp
// current/decomp/KSA/Universe.cs L700-L703
	public static void SetSimulationSpeed(SimSpeed speed)
	{
		_simulationSpeed = speed.Value;
	}
```

```csharp
// current/decomp/KSA/Universe.cs L2233-L2237
		double num9 = (num8 - elapsedTime).Seconds();
		double val2 = Math.Max(num9 / dtPlayer, 1.0);
		double num10 = Math.Min(val2, num);
		SetSimulationSpeed(num10, alert: false);
		autoWarp.PlayerTimeLeft -= dtPlayer;
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Universe.SetSimulationSpeed(SimSpeed)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703) | The write: a plain field assignment, no alert, no limit |
| `Universe.SimulationSpeed`, `IsPaused()`, `IsAutoWarpActive` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L118-L131) | Read-back, pause test (L1799-L1802), auto-warp test |
| `Universe.ProcessAutoWarp`, `AutoWarpTo`, `AutoWarpStop` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2208-L2280) | Auto-warp drives the same field every frame |
| `Universe.GetJobSimStep`, `GetLastSimStep` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) | Why the effect is two frames late |
| `SimSpeed(double)` | [current/decomp/KSA/SimSpeed.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimSpeed.cs#L25-L42) | Names `< 1e-4` "paused"; stores the value unchanged |
| `Program.UpdateMenuPause` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2580-L2597) | Menus set speed 0 and restore the captured speed on close |

## Pitfalls and version notes

- **Never infer the frame rate from the frame a speed change lands on**: it can be seconds long, and
  dividing by it once pinned the world at 1x for a whole flight (hence the 1 s hitch guard).
- **Do not judge a reduction on the step that measured the interval before it**; without
  `SettleSteps` the loop cut 30x to 9.9x and then to 3.2x every salvo.
- **An auto-warp write and a refusal look the same.** The `WarpPolicy` comments say KSA rejects a speed
  change outright during auto-warp. In 5554 the overload the mod calls never refuses; `ProcessAutoWarp`
  overwrites `_simulationSpeed` every frame in `PrepareFrame` (Universe.cs L2236), and only the console
  command refuses during auto-warp (L713-L716). The guard (`autoWarpRunning` -> do nothing) is right either way.
- **`KsaWorld.SetSimulationSpeed`'s comment** says `SetSimulationSpeed` rejects speeds above
  `SimSpeed.MaxSpeed`; in 5554 only the console path checks `MaxSpeed` (Universe.cs L719-L734).
- **The policy is not reset on a save load.** `ForgetTheWorld` does not clear `_warp`, and
  `DeserializeSave` sets the speed to 1.0 (Universe.cs L2477). If the hold had reached the 1.0x floor,
  the next `Release` reads the world as "still at our speed" and restores the pre-hold warp onto the
  newly loaded world. (Derived from the code; not flown.)
- **Menu pause** sets 0x each frame and restores the speed captured when the menu opened
  (Program.cs L2580-L2597), which during a hold is the held speed; the policy ignores paused frames.
- `docs/KSA-FRAME-ORDER.md` §2's two-frame delay holds on 5554 (L2216, L1948).[^ksarmory-frame-order]

## See also

- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [Unattended scenario runner](./unattended-scenario-runner.md)
- [Detect a save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Hold the speed down with a feedback controller over SetSimulationSpeed, with player yield and restore](../../../howto/flight-and-orbits/flight-state/set-simulation-speed-from-a-mod/set-simulation-speed-from-a-mod-3.md) - how-to article demonstrating this feature

[^ksarmory-warppolicy]: src/KSArmory/Sim/WarpPolicy.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-icbmcomputers]: src/KSArmory/Ksa/IcbmComputers.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-scenariorunner]: src/KSArmory/Ksa/ScenarioRunner.cs
[^ksarmory-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-simspeed]: KSA.SimSpeed (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-inputevents]: KSA.InputEvents (decompiled)
