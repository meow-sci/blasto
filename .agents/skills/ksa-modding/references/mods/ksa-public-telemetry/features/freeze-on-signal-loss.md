---
type: Mod Feature
title: Freeze on signal loss
description: Stops sampling and holds the last telemetry when the controlled vehicle loses control, is destroyed, or is about to exceed its structural g-load or dynamic-pressure limit, showing an acquisition-of-signal notice like a real broadcast
mod: ksa-public-telemetry
category: vehicles-and-parts
techniques: [starmap-lifecycle, state-machine]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.StructuralLoad
  - KSA.VehicleStructuralLimits
  - KSA.Program
  - KSA.CelestialSystem
  - KSA.PhysicsBubble
  - KSA.Universe
  - KSA.KittenEva
tags: [telemetry, signal-loss, structural-load, breakup, controllable, freeze]
---

When a rocket breaks up on a real webcast, the telemetry stops on its last values. The mod copies
that: if the vehicle stops being controllable, or the game destroys it, the overlay keeps showing the
last good numbers in a dimmed clock and fades in "Please wait for acquisition of signal".[^kpt-readme]
It also freezes slightly before a structural breakup so the frozen numbers are from the intact
vehicle.

## How it works

1. **Three-way tracking decision.** `TelemetrySampler.DecideTracking` is a pure function of five
   booleans and returns `Clear`, `Hold` or `Sample` ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L140-L169)):
   - No controlled vehicle (`Program.ControlledVehicle` null or `IsDisposed`): hold the last frame if
     one exists and either the previous frame was already a wreck hold or the tracked vehicle id is gone
     from `Universe.CurrentSystem.All` (it was destroyed); otherwise clear (the player went to the menu or
     the editor) ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L171-L197)).
   - A vehicle exists but is not controllable while a wreck hold is active: keep holding.
   - Otherwise sample.
2. **Signal state.** For sampled vehicles `SignalState.ShouldSample(id, isControllable, failureImminent, dt)`
   keeps `Live`/`Lost`/`NoVehicle`, counts seconds since loss, and reports `LostThisFrame` and
   `RegainedThisFrame`. Losing control or an imminent failure flips `Live` to `Lost` and stops sampling;
   regaining control resumes it and rebaselines the event detector
   ([Telemetry/SignalState.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/SignalState.cs#L57-L93), [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L73-L80)).
3. **Controllable.** `Vehicle.IsControllable` is true when the part tree has at least one control
   module (or an override is set), so losing the probe core or crew pod drops it
   ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589)).[^ksa-vehicle]
4. **Imminent failure.** `ToleranceFraction` reads `ref readonly vehicle.StructuralLoad` and takes the
   larger of `GLoadFraction` and `DynamicPressureFraction` (peak over max). When it reaches the
   configured `FreezeAtToleranceFraction` (default 0.95) the overlay freezes
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L236-L241), [current/decomp/KSA/StructuralLoad.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StructuralLoad.cs#L1-L18)).[^ksa-structuralload]
   The game fills that struct on the worker in `PhysicsBubble.DetectStructuralFailure`: the peaks are
   the highest filtered g-load and `0.5 * density * v^2` (air or water density, whichever is higher) of
   the step ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2430-L2450)), the g limit is
   `VehicleStructuralLimits.EffectiveMaxGLoad(boundingSphereRadius)` (`max(5, 50 * min(1, 5 / r))`,
   times 2.5 for kittens) and the dynamic-pressure limit is a fixed 200 kPa. A fraction of 1.0 or more
   creates a `VehicleDestructionEvent` for the whole vehicle; only the g-load check is waived for debris
   and for a vehicle in contact that already has a part-failure event
   ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L999-L1038),
   [current/decomp/KSA/VehicleStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleStructuralLimits.cs#L17-L20)).[^ksa-physicsbubble] [^ksa-vehiclestructurallimits]
   The default 0.95 therefore freezes on the step before the one that would destroy the vehicle, when
   the load climbs gradually.
5. **On screen.** While frozen, the snapshot is not cleared, so every panel keeps drawing the old
   values; the clock text turns muted, and a notched banner fades in above the left shelf with a
   0.25 s fade ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L150-L156), [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L236-L267),
   [Rendering/MissionClockPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/MissionClockPanel.cs#L65-L67)).

## Lifecycle and timing

- **Decision in `frame:after-on-frame`.** `IsControllable`, `IsDisposed`, the system object list and
  `StructuralLoad` are main-thread values updated when the vehicle step is applied in `PrepareFrame`
  (`_structuralLoad = updateData.NewStructuralLoad`, [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2529)). Sampling after the
  frame sees the same state the game used for this frame.
- **Why 0.95 and not 1.0.** The destruction decision is made on the worker in the same step that
  computes the fraction, and the main thread acts on it inside the same `ApplyVehicleSolvers` call that
  copies the load: `VehicleUpdateTask.ApplyResultsToMainThread` applies the results, then
  `ApplyRenderEventsToVehicles` runs `Universe.DestroyVehicleFromEvent`, whose `DestroyVehicle` clears
  `Program.ControlledVehicle` and disposes the vehicle, which deregisters it from the system
  ([current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259),
  [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1971-L2051)).[^ksa-vehicleupdatetask] [^ksa-universe] The after-frame hook therefore never sees a live
  vehicle at 1.0 or more (except under the g-load waivers). At 1.00 the freeze only comes from the
  destroyed-vehicle hold, which keeps the values sampled at the end of the previous frame; below 1.0
  it can trip one step earlier. A single step that jumps from under the threshold to past 1.0 still
  ends in the destroyed-vehicle hold. The setting's help text says the same
  ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L233-L238)).
- **Peaks are per step.** `VehicleUpdateState.PrepareFromWorker` resets `PeakGLoad` and
  `PeakDynamicPressure` before each step ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L326-L339)), so the fraction describes the last applied step, not the
  flight so far.
- **Notice fade** runs in `frame:draw-ui` on the UI `dt`.

## Key code

| Path | Role |
|---|---|
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L40-L91) | Decision, hold, clear, signal gate |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L140-L197) | Pure tracking decision and destroyed-vehicle check |
| [Telemetry/SignalState.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/SignalState.cs#L10-L94) | Live/Lost state machine |
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L236-L267) | "Acquisition of signal" banner |

## Snippets

```csharp
// Telemetry/TelemetrySampler.cs L149-L181, L236-L241
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
// ...
    private static double ToleranceFraction(Vehicle vehicle)
    {
        ref readonly StructuralLoad load = ref vehicle.StructuralLoad;

        return Math.Max(load.GLoadFraction, load.DynamicPressureFraction);
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Vehicle.IsControllable` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589) | Control lost |
| `Vehicle.IsDisposed` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L618) | Vehicle destroyed |
| `Vehicle.StructuralLoad`, `StructuralLoad.GLoadFraction`, `DynamicPressureFraction` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L547), [current/decomp/KSA/StructuralLoad.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StructuralLoad.cs#L1-L18) | Failure margin |
| `PhysicsBubble` structural limit check | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1010-L1017) | Where the game breaks the vehicle |
| `Universe.CurrentSystem.All.TryGet` | [current/decomp/KSA/LookupCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LookupCollection.cs#L284) | Is the tracked vehicle id still in the system |

## Pitfalls and version notes

- **EVA and debris.** `KittenEva` overrides `IsControllable` to always return true
  ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L63)), so EVA never freezes; switching to a piece without
  a control module (debris) does.[^ksa-kitteneva]
- **Threshold below 1.0 can trip in normal flight** for vehicles that run close to their limits; 1.00
  waits for the breakup itself.
- **Holding across vehicle switches.** If the game moves control to another vehicle after a breakup
  (for example a surviving stage), `Program.ControlledVehicle` is non-null and controllable, so the
  overlay starts sampling that vehicle and the freeze ends.

## See also

- [Live flight telemetry sampling](live-flight-telemetry-sampling.md)
- [In-flight event detection and callouts](flight-event-detection-and-callouts.md)
- [Error budget and self-disable](error-budget-self-disable.md)
- [Freeze on signal loss with IsDisposed, IsControllable and StructuralLoad fractions](../../../howto/vehicles-and-parts/craft-tracking/track-and-classify-craft/track-and-classify-craft-4.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-structuralload]: KSA.StructuralLoad (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehiclestructurallimits]: KSA.VehicleStructuralLimits (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
