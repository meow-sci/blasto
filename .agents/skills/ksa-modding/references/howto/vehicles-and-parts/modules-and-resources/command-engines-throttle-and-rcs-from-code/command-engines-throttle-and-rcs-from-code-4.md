---
type: How-To
title: Choose which RCS jets fire from a live wrench table
description: Measure each thruster's force and torque from live nozzle performance, then pulse jets by control-axis groups or a fuel-optimal LP from a postfix on FlightComputer.ComputeControl
category: vehicles-and-parts
group: modules-and-resources
topic: command-engines-throttle-and-rcs-from-code
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation]
techniques: [custom-physics, harmony-postfix]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [frame:apply-solvers, physics:vehicle-solver]
game_types:
  - KSA.ThrusterController
  - KSA.ThrusterControllerState
  - KSA.FlightComputer
  - KSA.RocketCore
  - KSA.RocketNozzle
  - KSA.Vehicle
tags: [rcs, allocation, thrusters, lp, wrench, pulses]
---
Outcome: to translate a vehicle along an arbitrary burn direction with RCS, something must decide which thrusters to pulse
and for how long. KSA-AdvancedFlightComputer (AFC) offers two allocators: **Groups** fires the six signed control-axis
groups and lets stock attitude control counter residual torque, and **LP** solves a fuel-optimal jet-selection problem
over individual thruster wrenches and falls back to Groups when infeasible. Both run on data AFC measures itself,
because the stock thruster cache keeps only what the stock control map uses.

Prerequisites: StarMap, Harmony, and comfort with the worker-thread rules of [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md).
Demonstrated by [ksa-advancedflightcomputer/rcs-thruster-allocation](../../../../mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation.md). Direct manual flags are
[article 2](command-engines-throttle-and-rcs-from-code-2.md).

## Background: how the game does it

- `FlightComputer.ComputeControl` runs on the vehicle worker; its RCS part is `ComputeRcsControl`, which reads the
  manual flags, builds rotational and linear impulse commands and calls `SelectJetsToFire` [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742-L819)[^ksa-flightcomputer]
  [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1585-L1614).
- The result per thruster is `ThrusterControllerState.CommandPulseTime` in the worker's new-state list
  [current/decomp/KSA/ThrusterControllerState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterControllerState.cs#L24)[^ksa-thrustercontrollerstate], reachable through `outputs.Thrusters.GetModulesAndNewStates(...)`.
- `ThrusterController.RecomputeDynamicData` computes stock authority; AFC mirrors it but keeps the full nozzle wrench and
  live pressure. Per nozzle: `RocketCore.ComputeConditions(1f)` (pulses run at full throttle),
  `RocketNozzle.ComputePerformance(conditions, ambientPressure)`, thrust along `-ExhaustDirectionAsmb`, torque
  `cross(nozzle position - centre of mass, force)`.
- `Vehicle.Ctrl2Body` can rotate the control axes ("Control From Here"), and the worker does not copy it back, so read it
  on the main thread.

## Lifecycle and timing

| Work | Where | Frame |
|---|---|---|
| wrench table, LP solve, cost estimates | main thread, the mod's post-solver vehicle tick, at most once a second per vehicle | `frame:apply-solvers` |
| shape and write pulses | postfix on `FlightComputer.ComputeControl`, from immutable published arrays: no allocation, no locks | `physics:vehicle-solver` |

The simplex and the part walk are too heavy for the worker, so the main thread publishes arrays and the worker only reads
them. If the thruster list length changed since publishing, the worker falls back to Groups. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) and [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Build the wrench table** (main thread). Walk `fc.VehicleConfig.Thrusters` in index order; for each active thruster
   with propellant sum its cores and nozzles:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs L31-64
    public void Build(Vehicle vehicle, FlightComputer fc, in RcsCtrlFrame ctrl)
    {
        List<ThrusterController> thrusters = fc.VehicleConfig.Thrusters;
        Count = thrusters.Count;
        if (ForceCtrl.Length < Count)
        {
            ForceCtrl = new float3[Count];
            TorqueCtrl = new float3[Count];
            MassFlow = new double[Count];
            Usable = new bool[Count];
            Modules = new ThrusterController?[Count];
        }
        UsableCount = 0;
        Ctrl2Body = ctrl.Ctrl2Body;

        ReadOnlySpan<RocketCoreState> coreStates = vehicle.Parts.RocketCores.States;
        float3 com = vehicle.TotalMassPropsAsmb.Offset;
        float ambientPressure = vehicle.PhysicsEnvironment.AtmosphericPressure;

        for (int i = 0; i < Count; i++)
        {
            ThrusterController thruster = thrusters[i];
            ComputeLive(thruster, coreStates, com, ambientPressure, in ctrl,
                out float3 force, out float3 torque, out float massFlow);

            ForceCtrl[i] = force;
            TorqueCtrl[i] = torque;
            MassFlow[i] = massFlow;
            Modules[i] = thruster;
            Usable[i] = massFlow > 0f && !force.IsExactlyZero();
            if (Usable[i])
                UsableCount++;
        }
    }
```

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs L92-115
    internal static void ComputeLiveCoreAsmb(
        RocketCore core, float3 com, float ambientPressure,
        out float3 force, out float3 torque, out float massFlow)
    {
        float3 forceAsmb = float3.Zero;
        float3 torqueAsmb = float3.Zero;
        massFlow = 0f;
        // Full-throttle probe because thruster pulses always command throttle 1.
        RocketCoreConditions combustion = core.ComputeConditions(1f);
        foreach (RocketNozzle nozzle in core.Rocket.Nozzles)
        {
            float4x4 matrix = float4x4.Pack(nozzle.Parent.MatrixAsmb2VehicleAsmb);
            floatQuat rotation = floatQuat.Pack(nozzle.Parent.Asmb2VehicleAsmb);
            NozzlePerformance perf = nozzle.ComputePerformance(in combustion, ambientPressure);
            float3 nozzleForce = perf.GetTotalThrust()
                * (-nozzle.ExhaustDirectionAsmb).Transform(rotation);
            float3 offset = nozzle.LocationAsmb.Transform(matrix) - com;
            forceAsmb += nozzleForce;
            torqueAsmb += float3.Cross(offset, nozzleForce);
            massFlow += perf.MassFlowRate;
        }
        force = forceAsmb;
        torque = torqueAsmb;
    }
```

   Rebuild when the thruster list or control frame changes, comparing by module reference (staging can replace the list
   without changing its count).
2. **Groups allocator.** Convert remaining delta-v into a control-frame impulse; per axis, return zero when the needed
   impulse is below the minimum correcting impulse (firing would overshoot more than it corrects), else cap it at
   `force * MaxPulseSec` (0.1 s, one control period). Each thruster takes the largest pulse any axis asks of it:

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs L73-118
    private static bool FireGroups(
        FlightComputer fc, ref FlightComputerOutput outputs, ref VehicleCommandSink.Receipt receipt,
        RcsWorkerCommand cmd, float3 impulse)
    {
        impulse.X = ShapeAxis(impulse.X, cmd.AxisForcePos.X, cmd.AxisForceNeg.X,
            cmd.AxisMinCorrectingImpulsePos.X, cmd.AxisMinCorrectingImpulseNeg.X, cmd.MaxPulseSec);
        impulse.Y = ShapeAxis(impulse.Y, cmd.AxisForcePos.Y, cmd.AxisForceNeg.Y,
            cmd.AxisMinCorrectingImpulsePos.Y, cmd.AxisMinCorrectingImpulseNeg.Y, cmd.MaxPulseSec);
        impulse.Z = ShapeAxis(impulse.Z, cmd.AxisForcePos.Z, cmd.AxisForceNeg.Z,
            cmd.AxisMinCorrectingImpulsePos.Z, cmd.AxisMinCorrectingImpulseNeg.Z, cmd.MaxPulseSec);
        if (impulse.IsExactlyZero())
        {
            receipt.Wake(RcsExecutor.MaxPulseSec);
            return false;
        }

        // A thruster serving several groups takes their longest pulse, not their sum. Group forces support one sided layouts.
        float minCommanded = float.PositiveInfinity;
        var enumerator = outputs.Thrusters
            .GetModulesAndNewStates(fc.VehicleConfig.Thrusters.AsSpan()).GetEnumerator();
        while (enumerator.MoveNext())
        {
            var current = enumerator.Current;
            ref ThrusterControllerState state = ref current.State;
            if (!state.IsPropellantAvailable)
                continue;

            float pulse = 0f;
            pulse = MaxAxisPulse(pulse, impulse.X, state.IntendedForce.X, cmd.AxisForcePos.X, cmd.AxisForceNeg.X);
            pulse = MaxAxisPulse(pulse, impulse.Y, state.IntendedForce.Y, cmd.AxisForcePos.Y, cmd.AxisForceNeg.Y);
            pulse = MaxAxisPulse(pulse, impulse.Z, state.IntendedForce.Z, cmd.AxisForcePos.Z, cmd.AxisForceNeg.Z);
            if (pulse <= 0f)
                continue;

            if (pulse > state.CommandPulseTime)
                state.CommandPulseTime = pulse;
            minCommanded = Math.Min(minCommanded, pulse);
            receipt.Command();
        }
        if (float.IsFinite(minCommanded))
        {
            receipt.Wake(Math.Min(RcsExecutor.MaxPulseSec, minCommanded));
            return true;
        }
        return false;
    }
```

3. **LP allocator** (optional). Build, on the main thread, a six-row problem: three rows force the net force to be the
   burn direction scaled, three rows bound torque, with priced torque slack allowed only on axes that have rotation
   authority; columns are thruster wrenches, costs are mass flow. A self-contained two-phase simplex with Bland's rule
   solves `min c.x, A x = b, x >= 0`; the solution becomes "seconds of firing per N s of impulse" per thruster; members
   under a 1 % duty floor are dropped (they would produce minimum-pulse puffs).
4. **Hook the worker.** A Harmony postfix on `FlightComputer.ComputeControl` runs your writers after stock control and
   combines their physics and wake requests; each call has its own receipt because vehicle workers can run in parallel:

```csharp
// AdvancedFlightComputer/Core/VehicleCommandSink.cs L72-87

        if (receipt.Commanded)
            outputs.AnyActuatorCommanded = true;
        if (receipt.WakeupSec < outputs.NextWakeupDeltaTime)
            outputs.NextWakeupDeltaTime = receipt.WakeupSec;
    }

    [HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.ComputeControl),
        new Type[] { typeof(FlightComputerNavigation), typeof(ManualControlInputs), typeof(FlightComputerOutput) },
        new ArgumentType[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref })]
    private static class ComputeControlPatch
    {
        static void Postfix(FlightComputer __instance, in FlightComputerNavigation nav,
                            ref FlightComputerOutput outputs)
            => Run(__instance, in nav, ref outputs);
    }
```

5. **Only ever raise** `CommandPulseTime`, so stock attitude pulses in the same step are kept, and drop pulses under the
   minimum-pulse floor (the core would round them up and over-deliver).
6. Price both strategies (Hold: translate in the current attitude paying for residual torque; Align: turn the strongest
   axis onto the burn vector) and pick Align only when it is cheaper by a margin.

## Complete example

- [AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs)[^ksa-advancedflightcomputer-rcswrenchtable]
- [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs)[^ksa-advancedflightcomputer-rcscomputecontrolpatch]
- [AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs)[^ksa-advancedflightcomputer-rcslpsolver] and [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs)[^ksa-advancedflightcomputer-rcsexecutor-allocation]

## Variations and gotchas

- **Trade-off.** Groups is robust and lets stock attitude control fix torque; LP spends less propellant on asymmetric
  layouts but needs a fresh, matching thruster list.
- **Burn mode.** Suppress engine commands while an RCS burn is active, even if another caller changed `BurnMode`.
- **Worker rules.** No allocation, no locks, catch everything; failures are logged once and must not escape.
- **Stale LP geometry** (list length changed): fall back to Groups for that step.
- **Updates.** Depends on `ThrusterController`, `ThrusterControllerState` and `FlightComputer.ComputeControl`'s signature
  `(FlightComputerNavigation, ManualControlInputs, FlightComputerOutput)`.

## See also

- [Overview](overview.md), [Article 2](command-engines-throttle-and-rcs-from-code-2.md)
- [ksa-advancedflightcomputer/rcs-thruster-allocation](../../../../mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation.md)
- [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md), [Create and attach parts to a live vessel and feed runtime engines from tanks](../add-parts-to-a-live-vessel/overview.md)

[^ksa-advancedflightcomputer-rcswrenchtable]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs
[^ksa-advancedflightcomputer-rcsexecutor-allocation]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs
[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
[^ksa-thrustercontrollerstate]: current/decomp/KSA/ThrusterControllerState.cs (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-rcscomputecontrolpatch]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs
[^ksa-advancedflightcomputer-rcslpsolver]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs
