---
type: How-To
title: Sum ComputeActivePerformance thrust per active controller for a live thrust and T/W readout
description: Get the current total engine thrust and thrust-to-weight of the controlled vehicle by calling EngineController.ComputeActivePerformance for each active controller at the live ambient pressure and throttle
category: flight-and-orbits
group: guidance
topic: compute-a-suicide-burn
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/live-flight-telemetry-sampling]
techniques: [snapshot-copy, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.EngineController
  - KSA.EngineControllerState
  - KSA.ReadOnlyPhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.Program
tags: [thrust, thrust-to-weight, telemetry, engine, snapshot]
---
Outcome: the vehicle's current total thrust (N) and thrust-to-weight ratio, computed with the game's own per-engine performance call so that throttle, ambient pressure and propellant supply are all accounted for. This is the simplest live thrust number, and the right building block for a suicide-burn readout when you want the thrust you are actually getting rather than the rated full-throttle thrust of [approach 1](compute-a-suicide-burn-1.md). KSA-Public-Telemetry uses it for its broadcast overlay.[Live flight telemetry sampling](../../../../mods/ksa-public-telemetry/features/live-flight-telemetry-sampling.md)
Prerequisite: StarMap.

## Background: how the game does it

`EngineController.ComputeActivePerformance(in EngineControllerState state, float3 centerOfMassAsmb, float ambientPressure, float throttle)` returns a `RocketControllerData` (`ThrustMax` as a `float3`, `MassFlowRateMax`, `MinimumThrottle`). It returns `RocketControllerData.Zero` when the controller is not `IsActive` or the state's `IsPropellantAvailable` is false, the cached `VacuumData` for zero pressure at full throttle, and otherwise `RocketControllerData.ComputeFromCores` over its cores.[^ksa-enginecontroller] See [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75).

Ambient pressure and gravity come from the vehicle's `ReadOnlyPhysicsStates` (`Vehicle.GetPhysicsStates()`), whose `Environment` is the `PhysicsEnvironment` with `AtmosphericPressure`, `AtmosphericDensity` and `GravitationBub`.[^ksa-readonlyphysicsstates] [^ksa-physicsenvironment]

## Lifecycle and timing

- **Hook:** `[StarMapAfterOnFrame]` is invoked from a Harmony postfix on `Program.OnFrame`, so it runs after the whole frame (`frame:after-on-frame`) on the main thread.[^starmap-programpatcher] [StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L38-L48)
- **State read:** the vehicle snapshot from the last `ApplyVehicleSolvers`; the next step is already running. Reading is safe, writing simulated state is not.
- **Cadence:** once per frame, cheaper than doing it in each panel; the panels read the copy, never the vehicle. The hook also fires on frames where the game returned early, so guard for no vehicle.
- The [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) lists every phase.

## Steps

1. **Add the hook** to your StarMap mod class:

```csharp
// TelemetryOverlayMod.cs L65-L83
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

2. **Pick the vehicle:** `Program.ControlledVehicle`; skip when null or `IsDisposed`.
3. **Read pressure, density and gravity** into the snapshot first, because thrust needs the pressure:

```csharp
// Telemetry/TelemetrySampler.cs L322-L339
        ReadOnlyPhysicsStates physics = vehicle.GetPhysicsStates();

        physics.GetStatesCci(out double3 positionCci, out double3 velocityCci, out _);
        double radius = positionCci.Length();
        snapshot.VerticalSpeed = radius > 0.0
            ? double3.Dot(velocityCci, positionCci / radius)
            : 0.0;

        float ambientPressure = physics.Environment.AtmosphericPressure;
        float ambientDensity = physics.Environment.AtmosphericDensity;
        float airspeed = physics.ComputeAirVelocityBody().Length();
        double gravity = physics.Environment.GravitationBub.Length();

        snapshot.AmbientPressure = ambientPressure;
        snapshot.AboveAtmosphere = ambientPressure <= 0f;
        snapshot.AmbientDensity = ambientDensity;
        snapshot.LocalGravity = (float)gravity;
        snapshot.DynamicPressure = 0.5f * ambientDensity * airspeed * airspeed;
```

4. **Sum thrust over active controllers.** Walk `ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>` module/state pairs (`TryGetFrom(parts.States, out var controllerStates)`), take the throttle from the core states, and call the performance function. The mod starts from `vehicle.TotalMassPropsAsmb.Offset` for the centre of mass:

```csharp
// Telemetry/TelemetrySampler.cs L500-L508
            if (controllerActive && controllerThrottle > 0f)
            {
                totalThrust += controller
                    .ComputeActivePerformance(in controllerState, centreOfMass, ambientPressure, controllerThrottle)
                    .ThrustMax.Length();
            }
        }

        snapshot.Thrust = totalThrust;
```

5. **Thrust-to-weight** is thrust over mass times local gravity:

```csharp
// Telemetry/TelemetrySampler.cs L97-L98
        double weight = snapshot.TotalMass * snapshot.LocalGravity;
        snapshot.ThrustToWeight = weight > 0.0 ? (float)(snapshot.Thrust / weight) : 0f;
```

6. **Feed it to the burn maths.** For a suicide burn, pass `throttle = 1f` instead of the current core throttle to get the full-throttle capability, then reuse the formulas in [approach 1](compute-a-suicide-burn-1.md).

## Complete example

| File | Role |
|---|---|
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L65-L83) | the `StarMapAfterOnFrame` entry point |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L40-L100) | sample driver, vehicle selection, T/W |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L308-L345) | kinematics and environment |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L418-L510) | engine walk and thrust sum |

## Variations and gotchas

- **Throttle matters.** The sampler passes the core's reported throttle, so the number is the current thrust, not capability. A zero throttle (or a coasting engine) gives zero, which is useless for a "can I land" check; pass `1f` for capability.
- **Starved engines drop out automatically**: `ComputeActivePerformance` returns zero when the controller is inactive or its propellant is not available.
- **Part-cores with partial supply** are treated as supplied when any core has fuel; if you need exact per-core sums see [approach 3](compute-a-suicide-burn-3.md).
- **No structural cap.** The manual flight computer limits the throttle to a g-load cap that this sum ignores (see approach 3).
- **Copy, do not alias.** The mod stores plain floats in a snapshot so panels cannot mutate game state; keep that boundary.
- **Weight uses the local gravity vector** (`GravitationBub.Length()`), which includes the body's pull at the vehicle position; on the pad this is the surface gravity.[^ksa-vehicle]

## See also

- [Overview](overview.md); [Closed-form calculator](compute-a-suicide-burn-1.md); [Core-model evaluation with throttle inversion](compute-a-suicide-burn-3.md)
- [Live flight telemetry sampling (mod feature)](../../../../mods/ksa-public-telemetry/features/live-flight-telemetry-sampling.md)
- [Read vehicle flight state](../../flight-state/read-vehicle-flight-state/overview.md)

[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
