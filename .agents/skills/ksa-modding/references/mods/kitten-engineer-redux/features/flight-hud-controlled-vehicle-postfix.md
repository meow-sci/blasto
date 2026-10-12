---
type: Mod Feature
title: Flight HUD on the controlled vehicle
description: A Harmony postfix on Vehicle.OnDrawUi, filtered to the controlled vehicle in the main viewport, gathers orbit, thrust, TWR, mass, acceleration and staging data once per frame into a shared frame record and draws a sectioned ImGui HUD from it
mod: kitten-engineer-redux
category: ui
techniques: [harmony-postfix, imgui-window, imgui-drawlist]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Program
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.GameViewport
  - KSA.PartTree
  - KSA.EngineController
  - KSA.RocketNozzle
  - KSA.PhysicsEnvironment
  - KSA.SequenceList
tags: [flight, hud, imgui, harmony, postfix, controlled-vehicle, twr, thrust, telemetry]
---

In flight the player gets "Kitten Engineer Redux - Flight", a left-docked window with a build stamp,
a Settings popup, an alert banner, configurable stat tiles and nine collapsible sections: Live, Fuel,
Orbit, Suicide burn, Stages, Orbit elements, Orbit planner, Surface and History. Each section can be
popped out into its own window. All numbers are computed once per frame from the controlled vehicle
and shared by every section, tile and alert.

## How it works

1. `Patch_FlightHud` postfixes `Vehicle.OnDrawUi`, the per-vehicle UI hook the game calls for every
   vehicle it draws markers for, and returns unless the call is for `Program.MainViewport` and the
   instance is `Program.ControlledVehicle` ([Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs#L6-L18)).[^ker-patch-flighthud]
2. `FlightHud.Draw` fills a single static `FrameData` object
   ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L153)):[^ker-flighthud]
   - `Orbit` from the orbit summary and `SuicideBurn` from the suicide-burn calculator.
   - Analysis environment: atmosphere height of `vehicle.Parent`; the analysis altitude is initialised
     once from `GetBarometricAltitude()` and then follows the slider.
   - `Burn`: native staging results (`recomputeIfDirty: false`) or the custom estimator with
     `vehicle.TotalMass`.
   - Live thrust: `ActiveEngineThrust.ComputeActual` sums `State.Performance.TotalThrust` and
     `MassFlowRate` over the `PartTree.RocketNozzles` states of every active engine's cores, so it
     reflects throttle and current conditions ([ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs#L12-L35); details in
     [Live and rated engine thrust with TWR](live-engine-thrust-and-twr.md)).[^ker-activeenginethrust]
   - Maximum thrust: `Vehicle.ComputeActiveThrust(PhysicsEnvironment.AtmosphericPressure)`, the game's
     full-throttle thrust at the live ambient pressure.
   - TWR (actual and max) against `G * M / r^2` at the current barometric altitude; acceleration in g
     from `Vehicle.AccelerationBody`.
   - `ActiveSequence` from `SequenceList.ActiveSequence`, or `GetNextSequenceNumber()` before the first
     stage fires.
3. It records a telemetry sample, evaluates alert rules, then draws the window with the draw-list
   widget kit: tiles from `HudWidgets.Build`, then each `HudSection` docked in order, then any popped
   sections as their own windows ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L155-L200)).
4. Section draw callbacks (`DrawLiveSection`, `DrawFuelSection`, and so on) read only the cached
   `FrameData`, so a popped-out section and the docked panel show identical numbers.

## Lifecycle and timing

- Call chain for the main viewport: `Program.OnDrawUiThreadSafe` → `Universe.OnDrawUi(MainViewport)`
  (only when `EditorFlag` is false) → `CelestialSystem.OnDrawUi` → `Astronomical.OnDrawUi` per drawn
  object ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3229-L3233),
  [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L364-L406)).[^ksa-program] [^ksa-celestialsystem]
  `CelestialSystem.OnDrawUi` culls objects outside the camera's field of view but always keeps the
  controlled vehicle (it is given distance -1 and sorted last, so its `OnDrawUi` is called after every
  other object's). That makes the postfix fire exactly once per frame for the controlled vehicle in
  the main viewport.
- Secondary viewports also call `Universe.OnDrawUi(this)` from `GameViewport.DrawImGui` during
  `OnDrawUiViewports`, which runs earlier in the frame
  ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L247-L250)); the
  `inViewport != Program.MainViewport` check discards those calls.[^ksa-gameviewport]
- This is `frame:draw-ui`. The vehicle solver for the next step was dispatched in `PrepareFrame`, so
  the main-thread `Vehicle` holds the state applied in `frame:apply-solvers` this frame; reads are
  consistent within the frame, and the mod writes nothing to the vehicle.
- Hiding the game UI (`DrawUI == false`) skips the whole chain, so the HUD, alerts and telemetry
  sampling all pause.
- `KittenEva` derives from `Vehicle` and does not override `OnDrawUi`, so the HUD also appears while
  controlling a kitten on EVA.[^ksa-kitteneva]

## Key code

| Path | Role |
|---|---|
| [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs) | Postfix with main-viewport and controlled-vehicle filter |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs) | Per-frame data gathering, window, section callbacks |
| [ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs) | Live nozzle thrust and rated thrust at a pressure |
| [EnvironmentHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EnvironmentHelpers.cs) | Local gravity and atmosphere lookups |

## Snippets

```csharp
// Patch_FlightHud.cs L6-L18
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnDrawUi))]
internal static class Patch_FlightHud
{
    private static void Postfix(Vehicle __instance, GameViewport inViewport)
    {
        if (inViewport != Program.MainViewport)
            return;
        if (Program.ControlledVehicle != __instance)
            return;

        FlightHud.Draw(__instance, inViewport);
    }
}
```

```csharp
// FlightHud.cs L130-L147
        float livePressure = (float)vehicle.PhysicsEnvironment.AtmosphericPressure;
        f.Engine = ActiveEngineThrust.ComputeActual(vehicle.Parts);
        f.MaximumThrust = vehicle.ComputeActiveThrust(livePressure);
        float currentGravity = EnvironmentHelpers.ComputeSurfaceGravity(
            vehicle.Parent, Math.Max(0f, vehicle.GetBarometricAltitude()));
        f.Mass = vehicle.TotalMass;
        f.ActualTwr = f.Mass > 0f && currentGravity > 0f
            ? f.Engine.TotalThrust / (f.Mass * currentGravity)
            : 0f;
        f.MaximumTwr = f.Mass > 0f && currentGravity > 0f
            ? f.MaximumThrust / (f.Mass * currentGravity)
            : 0f;
        f.AccelerationG = (float)(vehicle.AccelerationBody.Length() / KSA.Constants.STANDARD_GRAVITY);
        TelemetryHistory.Record(vehicle, f.Engine, f.ActualTwr);

        f.ActiveSequence = vehicle.Parts.SequenceList.ActiveSequence;
        if (f.ActiveSequence <= 0)
            f.ActiveSequence = vehicle.Parts.SequenceList.GetNextSequenceNumber();
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle.OnDrawUi(IGameViewport)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3848) | Postfix target |
| `Program.ControlledVehicle`, `Program.MainViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L484-L506) | Filter |
| `Vehicle.TotalMass`, `AccelerationBody`, `PhysicsEnvironment`, `Parts`, `Parent` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L543-L605) | Mass, acceleration, ambient pressure, part tree, parent body |
| `Vehicle.ComputeActiveThrust(float)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6694-L6708) | Full-throttle thrust of active engines at a pressure |
| `Vehicle.GetBarometricAltitude()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3063) | Radius minus mean radius |
| `PartTree.RocketNozzles` (`GetModulesAndStates`, `State.Performance`) | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L76) | Live per-nozzle thrust and mass flow |
| `PhysicsEnvironment.AtmosphericPressure` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L22) | Live ambient pressure |
| `SequenceList.ActiveSequence`, `GetNextSequenceNumber()` | [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L131-L208) | Highlight the burning stage |
| `Universe.OnDrawUi`, `CelestialSystem.OnDrawUi` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1811) | The caller chain |

## Pitfalls and version notes

- As with the editor patch, the postfix declares `GameViewport` while the target takes
  `IGameViewport` in every decompiled build from 2026.9.7.5402 to 5554. Harmony 2.4.2 binds it by name
  with no type check, and it is sound only because `GameViewport` is the sole `IGameViewport`
  implementation. Secondary viewports reach the postfix too and are rejected by a reference
  comparison before any member is touched; prefer the interface in your own patches.
- Patching `Vehicle.OnDrawUi` instead of using `[StarMapAfterGui]` ties the HUD to the game's own
  "draw this vehicle's UI" decision: no HUD in the editor, in a map with no controlled vehicle, or
  when `DrawUI` is off, with no extra checks in the mod.
- Popped-out section windows are drawn from inside the vehicle's `OnDrawUi`, so they disappear
  whenever the HUD is not drawn (editor open, no controlled vehicle, UI hidden) and reappear for the
  next controlled vehicle.
- `TWR (max)` uses `ComputeActiveThrust` (active engines that still have propellant); inactive
  next-stage engines do not count.

## See also

- [HUD stat tiles and threshold alerts](hud-widgets-and-threshold-alerts.md)
- [Telemetry history and CSV export](telemetry-history-and-csv-export.md)
- [Orbit readouts and Hohmann estimate](orbit-readouts-and-hohmann-estimate.md)
- [Suicide-burn calculator](suicide-burn-calculator.md)
- [Draw-list widget kit with pop-out sections](draw-list-widget-kit.md)
- [Editor panel injected into the vehicle editor](editor-panel-injection.md)
- [Live and rated engine thrust with TWR](live-engine-thrust-and-twr.md)
- [Live vehicle data: Harmony postfix on Vehicle.OnDrawUi filtered to the main viewport](../../../howto/ui/windows/show-live-vehicle-data-in-a-window/show-live-vehicle-data-in-a-window-2.md) - how-to article demonstrating this feature

[^ker-patch-flighthud]: Patch_FlightHud.cs
[^ker-flighthud]: FlightHud.cs
[^ker-activeenginethrust]: ActiveEngineThrust.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
