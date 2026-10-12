---
type: Mod Feature
title: KSA's engine panel and throttle gauge for a craft with no engine
description: Show KSA's own engine-control gauge panel for an engineless car that flies, withhold the "No active engines" alert, and put the mod's throttle on the stock throttle gauge by writing Vehicle._manualControlInputs between the GUI pass and UpdateGauges
mod: ksacars
category: ui
techniques: [harmony-postfix, reflection, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.GaugeCanvas.IsContextVisible
  - harmony-postfix KSA.Vehicle.AreAllEnginesInactive
  - reflection KSA.Vehicle._manualControlInputs
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:gauges, frame:dispatch-solvers]
game_types:
  - KSA.GaugeCanvas
  - KSA.GaugeVisibilityFlag
  - KSA.Vehicle
  - KSA.ManualControlInputs
  - KSA.FixedAlert
  - KSA.Alert
  - KSA.Program
  - KSA.PartTree
  - KSA.EngineController
tags: [ui, hud, gauges, harmony, postfix, reflection, throttle, alerts, frame-order]
---

When you fly the Eldorado, KSA's own engine-control panel appears with its throttle gauge showing
the rockets' throttle, and the "No active engines" alert stays away while the rockets are lit.
None of this would happen on its own: the car has no `EngineController`, which is what KSA shows
the panel for and what it counts as an engine. Two Harmony postfixes on public methods fix the
visibility and the alert, and a reflection write of the mod's throttle into the craft's private
manual inputs, timed between the GUI pass and `UpdateGauges`, fills the gauge.[^ksacars-hudhook][^ksacars-claude]

## How it works

| Piece | Mechanism |
|---|---|
| Panel visible | Postfix on `GaugeCanvas.IsContextVisible()`: if KSA said no, and the canvas's `VisibleInContext` is exactly `[Engines]`, and the controlled craft is a car whose profile has rockets, return true |
| Alert withheld | Postfix on `Vehicle.AreAllEnginesInactive()` (static): if KSA said yes and the controlled craft is a flying car, return false |
| Gauge value | `KsaWorld.ShowThrottle(craft, throttle)` writes `ManualControlInputs.EngineThrottle` into `Vehicle._manualControlInputs` by reflection, every frame, for every car with rockets |

**Visibility.** `GaugeCanvas.IsContextVisible` returns true for an empty `VisibleInContext` and
otherwise requires every listed flag to hold; `GaugeVisibilityFlag.Engines` means
`controlledVehicle.Parts.Modules.Get<EngineController>().Length > 0`.[^ksa-gaugecanvas] Core's
`EngineControl` canvas lists only `Engines`.[^ksa-gauges-xml] The postfix uses a list pattern,
`__instance.VisibleInContext is not [GaugeVisibilityFlag.Engines]`, so a canvas with any other
condition is left to KSA.[^ksacars-hudhook]

**Alert.** `Vehicle.AreAllEnginesInactive` returns true when the controlled vehicle's
`_manualControlInputs.EngineOn` is set and `IsAnyEngineActive()` is false. KSA wires it to the
`AlertNoEnginesActive` `FixedAlert` as a delegate, evaluated when `Alert.DrawAll` draws the list;
Harmony patches the method itself, so the delegate sees the postfix.[^ksa-vehicle][^ksa-fixedalert][^ksa-alert] The car's rockets are lit through that
same `EngineOn` bit (see [drive with flight keys](./drive-with-flight-keys.md)), which is why the
alert would otherwise fire.

**Throttle.** KSA's throttle gauge is `ThrottleRoller.SetTarget((int)(vehicle.GetManualThrottle() * 100f))`
in `Program.UpdateGauges`, and `GetManualThrottle()` returns `_manualControlInputs.EngineThrottle`.[^ksa-program][^ksa-vehicle]
`KsaWorld` caches `typeof(Vehicle).GetField("_manualControlInputs", NonPublic | Instance)` only if
its type is `ManualControlInputs`; `ShowThrottle` reads the struct, sets `EngineThrottle` to the
clamped mod throttle and writes it back.[^ksacars-ksaworld] `Buggies.Sync` calls it for every car
with rockets.[^ksacars-buggies]

## Lifecycle and timing

The write works only because of where it lands in `Program.OnFrame`:[^ksa-program]

| Order | Step | Effect on `_manualControlInputs.EngineThrottle` |
|---|---|---|
| 1 | `PrepareFrame` -> `ExecuteNextVehicleSolvers` -> `Vehicle.PrepareWorker` | ramped by held keys, then clamped to `[GetMinThrottle(), 1]`; with no `EngineController` the minimum is 1, so it becomes 1 |
| 2 | `OnDrawUiFrame` (L2267) | `Alert.DrawAll` evaluates `AreAllEnginesInactive` (postfix 2 runs here) |
| 3 | `OnDrawUiViewports` (L2268) + `[StarMapAfterGui]` postfix | `StepOnce` -> `Buggies.Sync` -> `ShowThrottle` writes the mod throttle |
| 4 | `OnDrawUiThreadSafe` (L2272) | `GaugeCanvas.CanvasesOnDrawUi` -> `GaugeCanvas.OnDrawUi` calls `IsContextVisible` (postfix 1 runs here) |
| 5 | `UpdateGauges` (L2300) | reads `GetManualThrottle()`: the mod's value |
| 6 | next frame's `PrepareWorker` | clamps it back to 1 before the worker snapshot |

`StarMapAfterGui` is StarMap's Harmony postfix on `Program.OnDrawUiViewports`.[^starmap-programpatcher]
`PartTree.RecomputeRocketControls` starts `_engineThrottleMin` at 1 and lowers it only per
`EngineController`, and `PrepareWorker` clamps the manual throttle to it.[^ksa-parttree][^ksa-vehicle]
So the mod's value lives only from step 3 to step 6: it is shown, and never read back by the physics.
All three hooks are main-thread ImGui-pass code. When the UI is hidden (F2), steps 2 to 4 do not run,
`StepOnce` falls back to `[StarMapAfterOnFrame]` after `UpdateGauges`, and nothing is drawn anyway.[^ksacars-mod]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/HudHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/HudHook.cs) | Both postfixes, the `FlyingCarIsFlown` test, signature pin |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L158-L174) | `ShowThrottle`: reflection write of `EngineThrottle` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L20-L24) | Type-checked `FieldInfo` for `_manualControlInputs` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L129-L131) | `Sync` calls `ShowThrottle` for every car with rockets |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L87-L93) | `[StarMapAfterGui]` -> `StepOnce` |

## Snippets

```csharp
// src/KSACars/Ksa/HudHook.cs L70-L98
    private static bool FlyingCarIsFlown =>
        Program.ControlledVehicle is { } craft && Buggies.Of(craft) is { } car && car.Drive.Profile.HasRockets;

    // Inside the engine's HUD pass: nothing here may throw.
    private static void AfterContextVisible(GaugeCanvas __instance, ref bool __result)
    {
        try
        {
            // Only a canvas whose one condition is engines: any other it names is KSA's to judge.
            if (__result || __instance.VisibleInContext is not [GaugeVisibilityFlag.Engines]) return;
            if (FlyingCarIsFlown) __result = true;
        }
        catch (Exception e)
        {
            Complain(e);
        }
    }

    private static void AfterEnginesInactive(ref bool __result)
    {
        try
        {
            if (__result && FlyingCarIsFlown) __result = false;
        }
        catch (Exception e)
        {
            Complain(e);
        }
    }
```

```csharp
// src/KSACars/Ksa/KsaWorld.cs L158-L174
    /// <summary>
    /// Puts a throttle on a craft's own throttle gauge. KSA reads the gauge after the GUI pass and
    /// clamps the value back in the next physics window, so this is written every frame and never read.
    /// </summary>
    public static void ShowThrottle(Vehicle craft, double throttle)
    {
        try
        {
            if (ManualInputs?.GetValue(craft) is not ManualControlInputs inputs) return;
            inputs.EngineThrottle = (float)Math.Clamp(throttle, 0.0, 1.0);
            ManualInputs.SetValue(craft, inputs);
        }
        catch
        {
            // The gauge reads full, as it does on any craft with no engine.
        }
    }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `GaugeCanvas.IsContextVisible()`, `VisibleInContext` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276) | Patch target 1; the `Engines` flag test; field at L103-L104 |
| `GaugeCanvas.OnDrawUi` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L717-L723) | The only caller of `IsContextVisible` |
| `Vehicle.AreAllEnginesInactive()` (static) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6584-L6600) | Patch target 2 |
| `FixedAlert` list | [current/decomp/KSA/FixedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedAlert.cs#L21-L26) | "No active engines" bound to `Vehicle.AreAllEnginesInactive` |
| `Alert.DrawAll` | [current/decomp/KSA/Alert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Alert.cs#L17-L25) | Draws fixed alerts; called from `Program.OnDrawUiFrame` |
| `Vehicle.GetManualThrottle()`, `GetMinThrottle()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1259-L1267) | What the gauge reads; the clamp floor |
| `Vehicle.PrepareWorker` throttle clamp | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2402-L2411) | Puts the throttle back each physics window |
| `PartTree.RecomputeRocketControls` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L993-L1016) | Minimum throttle starts at 1, lowered per `EngineController` |
| `Program.OnFrame`, `OnDrawUiFrame`, `OnDrawUiThreadSafe`, `UpdateGauges` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | Frame order; `ThrottleRoller` at L2792, alerts at L3133, canvases at L3203 |
| `<GaugeCanvas Id="EngineControl">` | [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765-L766) | `VisibleInContext` = `Engines` |

## Pitfalls and version notes

- **Write between `PrepareWorker` and `UpdateGauges`.** Any main-thread point in that span works
  (`StarMapBeforeGui` would too). Written in the `PrepareWorker` window it is clamped back to 1
  before the gauge reads it; written from `StarMapAfterOnFrame` it lands after `UpdateGauges` and is
  clamped before the next one, so it is never shown.[^ksa-program]
- **It is display only.** KSA clamps the value to its minimum before the worker sees it, so this
  cannot be used to throttle real engines on a craft that has none of its own; the mod's rockets
  take their throttle from its own field.[^ksacars-ksaworld]
- **Both patches are public and pinned.** `PinTheSignature` references both methods so a KSA change
  breaks the build; if either patch fails the car flies, without the panel or with the alert.[^ksacars-hudhook]
- **Reflection is type-checked.** If `_manualControlInputs` is renamed or retyped, `ManualInputs`
  is null and the gauge simply reads full, as on any engineless craft.[^ksacars-ksaworld]
- **The panel's controls.** The postfix only makes the panel visible; it does not verify what the
  panel's other buttons do on a craft with no engine (unverified: not traced in the gauge bindings).

## See also

- [Drive with flight keys](./drive-with-flight-keys.md) (engine start/stop and throttle keys by reflection)
- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md)
- [Car panel and ModMenu entry](./car-panel-and-modmenu.md)
- [KSACars overview](../overview.md)
- [Show the stock engine panel for a craft with no engines: IsContextVisible and AreAllEnginesInactive postfixes plus a throttle write](../../../howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-3.md) - how-to article demonstrating this feature

[^ksacars-hudhook]: src/KSACars/Ksa/HudHook.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^ksacars-claude]: CLAUDE.md, "The cars"
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-fixedalert]: KSA.FixedAlert (decompiled)
[^ksa-alert]: KSA.Alert (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-parttree]: KSA.PartTree (decompiled)
[^ksa-gauges-xml]: Core/Gauges.xml
