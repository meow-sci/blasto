---
type: How-To
title: Hold the speed down with a feedback controller over SetSimulationSpeed, with player yield and restore
description: Clamp the simulation speed only while a feature needs short steps, settle across the two-frame actuator delay, yield to the player and restore the speed when the need passes
category: flight-and-orbits
group: flight-state
topic: set-simulation-speed-from-a-mod
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/timewarp-policy-during-engagements]
techniques: [control-loop, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Universe, KSA.SimSpeed, KSA.InputEvents, KSA.Program]
tags: [time-warp, control-loop, yield, restore, sim-step]
---

Outcome: the world runs at the player's warp except while the mod needs fine steps (projectiles in flight), when the speed is
held to the highest value whose step the model can follow, and restored afterwards. KSArmory implements it as a control loop.[Hold timewarp down while rounds or burns are in flight](../../../../mods/ksarmory/features/timewarp-policy-during-engagements.md)
Prerequisite: StarMap.

## Background: how the game does it

The actuator is `Universe.SetSimulationSpeed`; it is shared with the player's warp keys (queued through `InputEvents.SimSpeedBuffer`
and applied in `ApplyInputEvents`), the stock auto-warp (`ProcessAutoWarp`) and the menu pause (`UpdateMenuPause`). It answers two frames
late: a speed written in frame k's hook is first used by `GetJobSimStep` in frame k+1, and the step it sizes is applied in k+2.[^ksa-program]
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2200-L2220) [^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) Player warp keys queued in frame k+1 win over the
mod's write from frame k because the buffer drains before the job step is sized.[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1124)

## Lifecycle and timing

| Frame | What happens to a speed written in frame k's hook |
|---|---|
| k (hook) | `SetSimulationSpeed` assigns `_simulationSpeed`; `SimulationSpeed` reads it back at once |
| k+1 `PrepareFrame` | the step dispatched in k (old speed) is applied; menu pause and `ProcessAutoWarp` may overwrite; `GetJobSimStep` sizes the next step with the new speed |
| k+1 (hook) | `dtSim` is still the old-speed step; the controller sees the read-back and starts settling |
| k+2 `PrepareFrame` | the first new-speed step is applied |
| k+2 (hook) | settle steps let it pass; judge again from k+3 |

Writing the speed from any main-thread hook is safe; it only affects steps not yet sized. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Decide from the applied step**, not the requested speed: `dtSim` is the step from [Show mission elapsed time from actual liftoff and pick the right clock under time warp](../compute-mission-elapsed-time-and-detect-liftoff/overview.md).
   Nothing to do when `dtSim <= faithfulStep`.
2. **Ignore hitches**: when `dtSim / speed` exceeds a second the rate you would infer is the hitch (the frame a speed change lands on), not the machine.
3. **Compute the target self-calibrating**, with no frame rate: `dtSim / speed` is the frame time, so the speed landing the next step at a margin of the limit is
   `speed * (faithful * margin) / dtSim`, floored at real time. Remember the speed to restore the first time:

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

    // Never below real time. A round that would rather the world ran slower than the player's
    // own clock does not get it: the mod is a guest, and a game that crawls is a worse thing to
    // hand somebody than a round integrated on a longer step. Where the two conflict, the round
    // takes the coarser step and the accuracy that comes with it.
    //
    // Deliberately NOT rate-limited on top of this. Capping how much one decision may take
    // costs the contract the tests below pin -- that the speed asked for lands inside the limit
    // -- and an interceptor converging over several decisions spends every one of them stepping
    // past its own fuse. The hitch guard above is the targeted fix; a cap would be a second,
    // blunter one that weakens a guarantee to cover the same case.
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

4. **Write through one guarded call** that rejects non-finite or non-positive values and floors at 0.001 (`SimSpeed` names anything under 1e-4 "paused"):

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L241-L247
public static bool SetSimulationSpeed(double speed)
{
    if (!double.IsFinite(speed) || speed <= 0.0) return false;

    Universe.SetSimulationSpeed(new SimSpeed(Math.Max(speed, SlowestSimSpeed)));
    return true;
}
```

5. **Settle and verify.** After a request wait for the read-back within 5% (abandon after 4 frames), then skip one step before judging again.
6. **Yield to others.** Do nothing while KSA auto-warp runs (and drop any pending request); if the speed is raised by someone else more than twice, yield for the rest of the engagement.
7. **Restore** the held speed only if the world is still at the speed you asked for (within 5%); restore when the policy is switched off or the engagement ends. Do not rely on `[StarMapUnload]` for this: under StarMap it runs after the game has exited, too late to restore a speed.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/WarpPolicy.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/WarpPolicy.cs) | decision logic, constants, `NoteOurOwnRequest`, `Release`, `Clear` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1384-L1451) | `ApplyWarpPolicy`: gather inputs, act on the decision |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L147-L273) | `IsPaused`, `SimulationSpeed`, `IsAutoWarpActive`, `SetSimulationSpeed`, `SetPaused` |

## Variations and gotchas

- **Clamp versus hold.** Clamping the model's step instead leaves it behind the world (the author measured a 124 km closest approach at 600x against 15-20 m unwarped); holding the world down is the fix when correctness needs it.
- The floor at 1x means the mod never slows the game below real time; at that point the round takes the coarser step.
- Own requests must be noted (`NoteOurOwnRequest`) so a scenario runner changing the speed is not counted as an override.
- `Universe.IsPaused()` tests zero; a menu pause writes the speed to zero in `PrepareFrame`, so skip the policy while speed is zero.

## See also

- [Overview](overview.md); [direct write](set-simulation-speed-from-a-mod-1.md); [AutoWarpBuffer](set-simulation-speed-from-a-mod-2.md)
- [Hold timewarp down while rounds or burns are in flight](../../../../mods/ksarmory/features/timewarp-policy-during-engagements.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
