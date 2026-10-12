---
type: Mod Feature
title: RCS jet selection with axis groups or a linear program
description: Build a live per-thruster force, torque and mass-flow table in the control frame, then fire signed-axis groups or solve a fuel-optimal jet-selection LP with a hand-written simplex
mod: ksa-advancedflightcomputer
category: vehicles-and-parts
techniques: [custom-physics]
hooks: [harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [frame:apply-solvers, physics:vehicle-solver]
game_types:
  - KSA.ThrusterController
  - KSA.ThrusterControllerState
  - KSA.RocketCore
  - KSA.RocketNozzle
  - KSA.NozzlePerformance
  - KSA.FlightComputer
tags: [rcs, thrusters, jet-selection, linear-programming, simplex, propellant]
---

To translate a vehicle along an arbitrary burn direction with RCS, something must decide which
thrusters to pulse and for how long. AFC offers two allocators per burn. **Groups** (default) fires
the six signed control-axis groups and lets stock attitude control counter residual torque. **LP**
solves a fuel-optimal jet-selection problem over individual thruster forces and torques (the
Bergmann/Draper formulation) and falls back to Groups when it is infeasible. Both run on data AFC
measures itself, because the stock thruster cache only keeps what the stock control map uses.

## How it works

### Wrench table

`RcsWrenchTable.Build` walks `FlightComputer.VehicleConfig.Thrusters` in index order and, for each
active thruster, sums over its rocket cores (skipping cores without propellant) and nozzles:
`RocketCore.ComputeConditions(1f)` (pulses always run at full throttle), then
`RocketNozzle.ComputePerformance(conditions, ambientPressure)`, force along the exhaust direction
transformed into the vehicle assembly, torque `cross(nozzle position - center of mass, force)`, and
mass flow. Results are projected onto the control axes with `RcsCtrlFrame`, which reads
`Vehicle.Ctrl2Body` because "Control From Here" can rotate the axes and the worker's own frame is
not copied back ([AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs),
[AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs)). This mirrors how
`ThrusterController.RecomputeDynamicData` computes authority, but keeps the full nozzle wrench and
live pressure ([current/decomp/KSA/ThrusterController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterController.cs#L84)).[^ksa-thrustercontroller] The table is rebuilt
when the thruster list or control frame changes (compared by module reference, because staging can
replace the list without changing its count).

### Groups allocator

The worker converts the remaining delta-v into a control-frame impulse `J`. For each axis,
`ShapeAxis` returns 0 when the needed impulse is below that sign's minimum correcting impulse (firing
would overshoot more than it corrects) and otherwise caps it at `force * MaxPulseSec` (0.1 s, one
control period). Each thruster in the group gets the largest pulse any axis asks of it, written into
`ThrusterControllerState.CommandPulseTime` (only ever raised, so stock attitude pulses in the same
step are kept) ([AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs#L73-L120)).

### LP allocator

`EnsureLpSolution` builds, on the main thread, a 6-row problem: three rows force the net force to
be the burn direction scaled, three rows bound torque, with priced torque slack allowed only on axes
that have rotation authority (stock attitude control can absorb that torque at a cost estimated from
rotation mass flow). Columns are thruster wrenches, costs are mass flow
([AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs#L23-L64)).
`RcsLpSolver` is a self-contained two-phase simplex with Bland's rule (`min c.x, A x = b, x >= 0`),
with row scaling and input validation, so the mod needs no LP library
([AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs)). The solution becomes
"seconds of firing per N s of impulse" per thruster index; members under a 1 % duty floor are
dropped (they would produce minimum-pulse puffs). The worker scales that pattern to the impulse cap
of one control period. The solve is refreshed when the direction drifts or once per second; if the
thruster list length changed since publishing, the worker falls back to Groups.

### Estimates

`RcsExecutor.Estimates` prices Hold (translate in the current attitude, paying for residual torque)
and Align (turn the strongest axis onto the burn vector, including an acceleration-and-braking slew
estimate with the flight computer rate limit). Auto strategy picks Align only when it is cheaper by
a 10 % margin ([AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.cs#L522-L544)).

## Lifecycle and timing

The wrench table, LP solve and estimates run on the main thread in the
[post-solver vehicle tick](post-solver-vehicle-tick.md) (`frame:apply-solvers`), at most once per
second per vehicle, because a simplex and a part walk are too heavy for the worker. The worker side
(`physics:vehicle-solver`, inside `FlightComputer.ComputeControl`) only shapes and writes pulses
from the immutable published arrays: no allocation, no locks.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs) | Live per-thruster force, torque, mass flow in the control frame |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsCtrlFrame.cs) | Projection onto control axes |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsLpSolver.cs) | Two-phase simplex with Bland's rule |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Allocation.cs) | LP problem construction, refresh and fallback |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Estimates.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsExecutor.Estimates.cs) | Hold and Align propellant estimates |
| [AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs) | Worker-side group and LP pulse writers |
| [tests/AdvancedFlightComputer.HarnessTests/Features/RcsTranslation/RcsLpSolverTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/RcsTranslation/RcsLpSolverTest.cs) | Solver unit checks in the harness |

## Snippets

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsWrenchTable.cs L92-L115
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

```csharp
// AdvancedFlightComputer/Features/RcsTranslation/RcsComputeControlPatch.cs L188-L205
    internal static float ShapeAxis(
        float j, float forcePos, float forceNeg,
        float minCorrectingImpPos, float minCorrectingImpNeg, float maxPulse)
    {
        if (j > 0f)
        {
            if (forcePos <= 0f || j < minCorrectingImpPos)
                return 0f;
            return Math.Min(j, forcePos * maxPulse);
        }
        if (j < 0f)
        {
            if (forceNeg <= 0f || -j < minCorrectingImpNeg)
                return 0f;
            return Math.Max(j, -forceNeg * maxPulse);
        }
        return 0f;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `ThrusterController.RecomputeDynamicData` ([current/decomp/KSA/ThrusterController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterController.cs#L84)) | Stock authority model the table mirrors |
| `ThrusterControllerState.CommandPulseTime` ([current/decomp/KSA/ThrusterControllerState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterControllerState.cs)) | Pulse command written on the worker |
| `RocketCore.ComputeConditions(float)` ([current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L154)) | Combustion conditions at full throttle |
| `RocketNozzle.ComputePerformance(in RocketCoreConditions, float)` ([current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L150)), `NozzlePerformance.GetTotalThrust` ([current/decomp/KSA/NozzlePerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NozzlePerformance.cs#L43)) | Live thrust and mass flow at ambient pressure |
| `FlightComputer.ComputeRcsControl` (private, [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742)) | Stock RCS control that runs before AFC's pulses |

## Pitfalls and version notes

- Stock thruster authority uses the smaller of both signs and can be zero for a one-sided layout;
  AFC passes positive and negative forces separately.
- The LP can demand costly counter-thrust; that is why it is opt-in and priced against attitude
  control.
- Index-based arrays are only valid while `VehicleConfig.Thrusters` is unchanged; check the length
  on the worker and rebuild on the main thread.

## See also

- [RCS burn execution](rcs-burn-execution.md)
- [RCS burn editor and gauge UI](rcs-burn-editor-and-gauge-ui.md)
- [Flight computer command sink](flight-computer-command-sink.md)
- [Intercept the Auto burn-mode request and fly the burn with RCS only](../../../howto/flight-and-orbits/autopilot/execute-burns-with-rcs-and-throttle-inputs/execute-burns-with-rcs-and-throttle-inputs-1.md) - how-to article demonstrating this feature
- [Choose which RCS jets fire from a live wrench table](../../../howto/vehicles-and-parts/modules-and-resources/command-engines-throttle-and-rcs-from-code/command-engines-throttle-and-rcs-from-code-4.md) - how-to article demonstrating this feature

[^ksa-thrustercontroller]: KSA.ThrusterController (decompiled, build 2026.10.10.5554)
