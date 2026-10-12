---
type: How-To
title: "Show the stock engine panel for a craft with no engines: IsContextVisible and AreAllEnginesInactive postfixes plus a throttle write"
description: Postfix GaugeCanvas.IsContextVisible so the Engines-only canvas shows, postfix Vehicle.AreAllEnginesInactive to withhold the No active engines alert, and write the throttle gauge's value into the private manual inputs between the GUI pass and UpdateGauges
category: ui
group: menus
topic: work-with-stock-cockpit-gauges
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/engine-hud-for-engineless-craft]
techniques: [harmony-postfix, reflection]
hooks:
  - harmony-postfix KSA.GaugeCanvas.IsContextVisible
  - harmony-postfix KSA.Vehicle.AreAllEnginesInactive
  - reflection KSA.Vehicle._manualControlInputs
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, frame:draw-ui, frame:gauges]
game_types:
  - KSA.GaugeCanvas
  - KSA.GaugeVisibilityFlag
  - KSA.Vehicle
  - KSA.ManualControlInputs
  - KSA.FixedAlert
  - KSA.Program
tags: [gauge-visibility, engine-panel, throttle-gauge, alert, engineless, reflection-write]
---
KSACars' flying car has rocket thrusters of the mod's own, not KSA `EngineController` parts. KSA therefore does not show the EngineControl panel (where the throttle is read) for it, raises "No active engines" whenever the engine switch is on, and would clamp the throttle gauge to full. KSACars fixes all three with two Harmony postfixes on **public** methods and one reflection write of the throttle into the craft's private `ManualControlInputs`. The same recipe works for any craft whose "engines" are not `EngineController`s. Prerequisites: StarMap and Harmony. Demonstrated by [Engine HUD for engineless craft](../../../../mods/ksacars/features/engine-hud-for-engineless-craft.md).

## Background: how the game does it

- **Panel visibility.** `GaugeCanvas.IsContextVisible()` returns true for an empty `VisibleInContext`, otherwise requires every listed `GaugeVisibilityFlag` to hold; `Engines` means `controlledVehicle.Parts.Modules.Get<EngineController>().Length > 0` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276),[^ksa-ksa-gaugecanvas] flags in [current/decomp/KSA/GaugeVisibilityFlag.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeVisibilityFlag.cs)).[^ksa-ksa-gaugevisibilityflag] The `EngineControl` canvas lists only that flag ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765-L766)).[^ksa-current-content-core-gauges-xml] `GaugeCanvas.OnDrawUi` skips a canvas whose `IsContextVisible()` is false ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L717-L723)).
- **The alert.** `Vehicle.AreAllEnginesInactive()` is a public static that returns true when the controlled vehicle's engine switch is on and no `EngineController` is active ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6584-L6600)).[^ksa-ksa-vehicle] `FixedAlert` binds it as a `Func<bool>` for `AlertNoEnginesActive` ([current/decomp/KSA/FixedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedAlert.cs#L26)),[^ksa-ksa-fixedalert] evaluated when `Alert.DrawAll` draws the list ([current/decomp/KSA/Alert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Alert.cs#L17-L24)).[^ksa-ksa-alert] Harmony patches the method itself, so the delegate sees the postfix.
- **The throttle gauge.** `UpdateGauges` sets the throttle roller from `vehicle.GetManualThrottle()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2792)),[^ksa-ksa-program] which is the private `_manualControlInputs.EngineThrottle` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1259-L1262), field [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L239)). `Vehicle.PrepareWorker` clamps it to `[GetMinThrottle(), 1]` every physics window ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2411)), and `PartTree.RecomputeRocketControls` starts the minimum at 1 and lowers it only per `EngineController` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L993-L1016)),[^ksa-ksa-parttree] so with no engine controllers the throttle is clamped to 1.

## Lifecycle and timing

The throttle write works only because of where it lands in `Program.OnFrame`:

| Order | Step | `EngineThrottle` |
|---|---|---|
| 1 | `PrepareFrame` then `Vehicle.PrepareWorker` (`frame:dispatch-solvers`) | ramped by held keys and clamped to `[GetMinThrottle(), 1]`, which is 1 with no engine controller |
| 2 | `OnDrawUiFrame` (`Alert.DrawAll` evaluates `AreAllEnginesInactive`; postfix 2 runs) | unchanged |
| 3 | `OnDrawUiViewports` and `[StarMapAfterGui]` (`frame:draw-ui`) | the mod writes its throttle |
| 4 | `OnDrawUiThreadSafe` (`GaugeCanvas.OnDrawUi` calls `IsContextVisible`; postfix 1 runs) | unchanged |
| 5 | `UpdateGauges` (`frame:gauges`) | reads the mod's value into the gauge |
| 6 | next frame's `PrepareWorker` | clamped back to 1 before the worker snapshot |

The order is read from `Program.OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2250-L2305)), so the value is shown and never read back by the physics: it is **display only**. Written from `[StarMapAfterOnFrame]` it would land after `UpdateGauges` and be clamped before the next one, so it would never be shown. Hidden HUD (F2) skips steps 2 to 4; KSACars' `StepOnce` has a fallback for that case. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Postfix both public methods in one isolated class.** Resolve them with `AccessTools.Method` by exact signature, patch with `Harmony.Patch(..., postfix: ...)`, and warn instead of throwing if either moved. Apply at `[StarMapAllModsLoaded]`; remove on unload and when the mod disables itself:

```csharp
// src/KSACars/Ksa/HudHook.cs L28-L53
    public static void Install()
    {
        if (Installed) return;

        try
        {
            MethodInfo? visible = AccessTools.Method(typeof(GaugeCanvas), nameof(GaugeCanvas.IsContextVisible), Type.EmptyTypes);
            MethodInfo? inactive = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.AreAllEnginesInactive), Type.EmptyTypes);
            if (visible is null || inactive is null)
            {
                Log.Warn("KSA's engine panel or its alert has moved; a flying car shows no throttle");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(visible, postfix: new HarmonyMethod(
                typeof(HudHook).GetMethod(nameof(AfterContextVisible), BindingFlags.NonPublic | BindingFlags.Static)!));
            _harmony.Patch(inactive, postfix: new HarmonyMethod(
                typeof(HudHook).GetMethod(nameof(AfterEnginesInactive), BindingFlags.NonPublic | BindingFlags.Static)!));
            Installed = true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not hook the engine panel; a flying car shows no throttle ({e.Message})");
        }
    }
```

2. **Show the panel.** If KSA said no, the canvas's only condition is `Engines`, and the flown craft is yours, return true. A list pattern, `is not [GaugeVisibilityFlag.Engines]`, leaves every canvas with another condition to KSA. Wrap the body so nothing escapes into the game's HUD pass:

```csharp
// src/KSACars/Ksa/HudHook.cs L70-L86
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
```

3. **Withhold the alert.**

```csharp
// src/KSACars/Ksa/HudHook.cs L88-L98
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

4. **Write the throttle by reflection, type-checked.** Cache the private `FieldInfo` only if its type is `ManualControlInputs`, so a rename or retype makes the gauge read full, as on any engineless craft, instead of throwing:

```csharp
// src/KSACars/Ksa/KsaWorld.cs L20-L24
    // A craft's engine switch and its held throttle keys are private to it, and KSA has no accessor for
    // either. Null when KSA has renamed them, and then a car's rockets cannot be lit from the keys.
    private static readonly FieldInfo? ManualInputs =
        typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.NonPublic | BindingFlags.Instance) is { } f
        && f.FieldType == typeof(ManualControlInputs) ? f : null;
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

5. **Call it every frame from the GUI hook.** `StepOnce` (called from `[StarMapAfterGui]`, and from `[StarMapAfterOnFrame]` as a once-per-frame fallback) calls `_buggies.Sync`, which writes the throttle for every car with rockets:

```csharp
// src/KSACars/Ksa/Buggies.cs L129-L131
        foreach (Entry e in Active.Values)
        {
            if (e.Drive.Profile.HasRockets) KsaWorld.ShowThrottle(e.Craft, e.Throttle);
```

6. **Pin the signatures.** KSACars keeps a method that is never called and references both patched methods, so a KSA change breaks the build instead of failing silently:

```csharp
// src/KSACars/Ksa/HudHook.cs L107-L108
    // Never called: puts the patched methods in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
    private static bool PinTheSignature(GaugeCanvas canvas) => canvas.IsContextVisible() || Vehicle.AreAllEnginesInactive();
```

7. **Check in game.** Fly the craft: the engine panel appears, the throttle gauge follows the mod's throttle and "No active engines" stays away while the rockets are lit.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/HudHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/HudHook.cs#L19-L109) | Both postfixes, the flown-craft test, signature pin |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L158-L174) | `ShowThrottle`: reflection write of `EngineThrottle` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L20-L24) | Type-checked `FieldInfo` for `_manualControlInputs` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L129-L131) | `Sync` calls `ShowThrottle` for every car with rockets |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L45-L52) | `HudHook.Install()` at all-mods-loaded |

## Variations and gotchas

- **Write between `PrepareWorker` and `UpdateGauges`.** Any main-thread point in that span works (`StarMapBeforeGui` too). Written in the `PrepareWorker` window it is clamped back to 1 before the gauge reads it.
- **Display only.** KSA clamps to its minimum before the worker sees it, so this cannot throttle real engines on a craft that has none of its own; the mod's rockets take their throttle from its own field.
- **Both patch targets are public**, which is what makes this robust. If either patch fails the car still flies, without the panel or with the alert.
- **Reflection is type-checked.** If `_manualControlInputs` is renamed or retyped the field is null and the gauge reads full.
- **The panel's other controls.** The postfix only makes the panel visible; what its buttons do on a craft with no engine was not traced (unverified). For buttons see [approach 2](work-with-stock-cockpit-gauges-2.md).
- **A struct write.** `ManualControlInputs` is a struct; read it with `GetValue`, modify, and `SetValue` back, as `ShowThrottle` does ([current/decomp/KSA/ManualControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ManualControlInputs.cs#L3-L12)).[^ksa-ksa-manualcontrolinputs]

## See also

- [Overview](overview.md)
- [Approach 2: re-enable a stock gauge button](work-with-stock-cockpit-gauges-2.md)
- [Approach 4: hide and restore the flight HUD](work-with-stock-cockpit-gauges-4.md)
- [Show live vehicle data in a window](../../windows/show-live-vehicle-data-in-a-window/overview.md)
- [Show alerts and game-styled popups](../../windows/show-alerts-and-popups-in-game/overview.md)
- Mod feature: [Engine HUD for engineless craft](../../../../mods/ksacars/features/engine-hud-for-engineless-craft.md)

[^ksa-ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gaugevisibilityflag]: KSA.GaugeVisibilityFlag (decompiled, build 2026.10.10.5554)
[^ksa-current-content-core-gauges-xml]: Game content: current/Content/Core/Gauges.xml
[^ksa-ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ksa-fixedalert]: KSA.FixedAlert (decompiled, build 2026.10.10.5554)
[^ksa-ksa-alert]: KSA.Alert (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-ksa-manualcontrolinputs]: KSA.ManualControlInputs (decompiled, build 2026.10.10.5554)
