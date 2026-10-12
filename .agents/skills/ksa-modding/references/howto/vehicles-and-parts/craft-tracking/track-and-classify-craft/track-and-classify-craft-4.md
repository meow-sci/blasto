---
type: How-To
title: Freeze on signal loss with IsDisposed, IsControllable and StructuralLoad fractions
description: Hold the last good values when the vehicle is destroyed or uncontrollable, and freeze before a structural breakup by reading GLoadFraction and DynamicPressureFraction
category: vehicles-and-parts
group: craft-tracking
topic: track-and-classify-craft
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/freeze-on-signal-loss]
techniques: [state-machine]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.StructuralLoad
  - KSA.PhysicsBubble
  - KSA.Part
  - KSA.Program
tags: [loss, disposed, controllable, structural-load, freeze]
---
Outcome: the overlay keeps showing the last good numbers and fades in "Please wait for acquisition of signal" when the vehicle stops being controllable, is destroyed, or is about to break up. It decides in one place with a pure function of a few booleans, and a small state machine tracks
live, lost and no-vehicle.

Prerequisite: StarMap. Demonstrated by [ksa-public-telemetry/freeze-on-signal-loss](../../../../mods/ksa-public-telemetry/features/freeze-on-signal-loss.md).

## Background: how the game does it

- `Program.ControlledVehicle` is null or disposed when the vehicle was destroyed (or the player is in a menu or the editor); `IsDisposed` is set by `Vehicle.Dispose`.
- `Vehicle.IsControllable` is true when the part tree has at least one control module, so losing the probe core or crew pod drops it. `KittenEva` overrides it to always true, so EVA never freezes.
- `Vehicle.StructuralLoad.GLoadFraction` and `DynamicPressureFraction` are peak over max for the last applied physics step. The game destroys a vehicle when either reaches 1.0 on the worker; the main thread then acts inside the same `ApplyVehicleSolvers`: `DestroyVehicleFromEvent` clears
  `Program.ControlledVehicle` and disposes the vehicle, which deregisters it from the system ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1036)[^ksa-physicsbubble], [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971)[^ksa-universe]). The g-limit is `VehicleStructuralLimits.EffectiveMaxGLoad(boundingSphereRadius)` (times 2.5 for kittens) and the dynamic-pressure limit is a fixed 200 kPa.
- Peaks are per step (reset before each), so the fraction describes the last applied step, not the flight so far.

## Lifecycle and timing

The decision runs in `frame:after-on-frame`: `IsControllable`, `IsDisposed`, the system object list and `StructuralLoad` are main-thread values updated when the vehicle step is applied in `PrepareFrame` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529)[^ksa-vehicle]), so sampling after the frame sees the state the game used. The notice fade runs in `frame:draw-ui`.
**Why 0.95 and not 1.0:** the destruction is applied in the same `ApplyVehicleSolvers` call that copies the load, so an after-frame hook never sees a live vehicle at 1.0 or more (except under the g-load waivers). At 1.00 the freeze only comes from the destroyed-vehicle hold; below 1.0 it trips one step earlier
([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Decide tracking as a pure function.** Three outcomes: clear (player went to the menu), hold (wreck or destroyed vehicle), sample:

```csharp
// Telemetry/TelemetrySampler.cs L149-L181
public static TrackingDecision DecideTracking(
    bool holdingFrame,
    bool vehicleUnavailable,
    bool trackedGone,
    bool isControllable,
    bool wreckHold)
{
    if (vehicleUnavailable)
    {
        return holdingFrame && (wreckHold || trackedGone)
            ? new TrackingDecision(TrackingAction.Hold, true)
            : new TrackingDecision(TrackingAction.Clear, false);
    }

    if (wreckHold && !isControllable && holdingFrame)
    {
        return new TrackingDecision(TrackingAction.Hold, true);
    }

    return new TrackingDecision(TrackingAction.Sample, false);
}

private static bool TrackedVehicleIsGone()
{
    if (_lastVehicleId.Length == 0)
    {
        return false;
    }

    CelestialSystem? system = Universe.CurrentSystem;

    return system is not null && !system.All.TryGet(_lastVehicleId, out Astronomical? _);
}
```

2. **Detect "the tracked vehicle is gone"** (`TrackedVehicleIsGone`, at the end of the snippet above): look the last-seen id up in `Universe.CurrentSystem.All` with `TryGet`; a destroyed vehicle has been deregistered, so the lookup fails and the decision holds the old frame.
3. **Read the failure margin** and freeze when it crosses the configured fraction (default 0.95):

```csharp
// Telemetry/TelemetrySampler.cs L236-L241
private static double ToleranceFraction(Vehicle vehicle)
{
    ref readonly StructuralLoad load = ref vehicle.StructuralLoad;

    return Math.Max(load.GLoadFraction, load.DynamicPressureFraction);
}
```

4. **Track signal state.** `ShouldSample(id, isControllable, failureImminent, dt)` keeps `Live`, `Lost`, `NoVehicle`, counts seconds since loss and reports `LostThisFrame` / `RegainedThisFrame`; regaining control rebaselines the event detector of [article 3](track-and-classify-craft-3.md):

```csharp
// Telemetry/SignalState.cs L57-L93
public bool ShouldSample(string vehicleId, bool isControllable, bool failureImminent, double dt)
{
    LostThisFrame = false;
    RegainedThisFrame = false;

    if (!string.Equals(_vehicleId, vehicleId, StringComparison.Ordinal))
    {
        _vehicleId = vehicleId;
        Status = SignalStatus.NoVehicle;
        SecondsSinceLoss = 0.0;
    }

    if (isControllable && !failureImminent)
    {
        RegainedThisFrame = Status == SignalStatus.Lost;
        Status = SignalStatus.Live;
        SecondsSinceLoss = 0.0;
        return true;
    }

    if (Status == SignalStatus.Live)
    {
        Status = SignalStatus.Lost;
        SecondsSinceLoss = 0.0;
        LostThisFrame = true;
        return false;
    }

    if (Status == SignalStatus.Lost)
    {
        SecondsSinceLoss += dt;
        return false;
    }
        
    Status = SignalStatus.Live;
    return true;
}
```

5. **Keep the old snapshot while frozen** so every panel keeps drawing the last values.

## Complete example

- [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L149-L181)[^ksa-public-telemetry-telemetrysampler] (decision), [Telemetry/SignalState.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/SignalState.cs#L1-L90)[^ksa-public-telemetry-signalstate] (state machine).

## Variations and gotchas

- **EVA and debris.** `KittenEva.IsControllable` is always true, so EVA never freezes; switching to a piece without a control module does.
- **A threshold below 1.0 can trip in normal flight** for vehicles that run close to their limits; 1.00 waits for the breakup itself.
- **Control moves after a breakup.** If the game moves control to a surviving stage, the overlay starts sampling that vehicle and the freeze ends.
- **Per-step peaks.** A single step that jumps from under the threshold to past 1.0 still ends in the destroyed-vehicle hold.
- **To prevent the destruction instead of observing it**, see [Make a vehicle indestructible](../../vehicles/make-a-vehicle-indestructible/overview.md).

## See also

- [Overview](overview.md); [Article 3](track-and-classify-craft-3.md)
- [ksa-public-telemetry/freeze-on-signal-loss](../../../../mods/ksa-public-telemetry/features/freeze-on-signal-loss.md)
- [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)

[^ksa-physicsbubble]: current/decomp/KSA/PhysicsBubble.cs (decompiled, build 2026.10.10.5554)
[^ksa-universe]: current/decomp/KSA/Universe.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-public-telemetry-telemetrysampler]: KSA-Public-Telemetry: Telemetry/TelemetrySampler.cs
[^ksa-public-telemetry-signalstate]: KSA-Public-Telemetry: Telemetry/SignalState.cs
