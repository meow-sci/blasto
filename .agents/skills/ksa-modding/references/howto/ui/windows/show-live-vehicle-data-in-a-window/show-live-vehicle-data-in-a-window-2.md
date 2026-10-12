---
type: How-To
title: "Live vehicle data: Harmony postfix on Vehicle.OnDrawUi filtered to the main viewport"
description: Hook the game's own per-vehicle UI call instead of a StarMap attribute and build one per-frame data record from orbit, live nozzle thrust and mass for a HUD window
category: ui
group: windows
topic: show-live-vehicle-data-in-a-window
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/flight-hud-controlled-vehicle-postfix]
techniques: [harmony-postfix, imgui-window]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Program
  - KSA.GameViewport
  - KSA.PartTree
  - KSA.PhysicsEnvironment
  - KSA.CelestialSystem
tags:
  - harmony-postfix
  - hud
  - controlled-vehicle
  - main-viewport
  - thrust
  - twr
  - frame-data
  - kitten-engineer-redux
---
Kitten Engineer Redux (KER) draws its flight HUD, "Kitten Engineer Redux - Flight", from a Harmony postfix on `Vehicle.OnDrawUi` rather than from `[StarMapAfterGui]`. The postfix fires for every vehicle the game draws world-UI for, so it has to filter to the main viewport and the controlled vehicle itself, but in return the HUD exists exactly when the game would draw that vehicle's UI: no HUD in the editor, none without a controlled vehicle, none when the UI is hidden, and no extra checks. Inside the postfix the mod fills one static per-frame record (orbit, suicide-burn, staging, live thrust, TWR, acceleration) that the window, its sections and the alert rules all read. Demonstrated by Kitten Engineer Redux. Prerequisites: StarMap (to load the mod) and Harmony.

## Background: how the game does it

- **Call chain.** `Program.OnDrawUiThreadSafe` calls `Universe.OnDrawUi(MainViewport)` when the editor is not open, which calls `CelestialSystem.OnDrawUi`, which calls `Astronomical.OnDrawUi` for every object in view.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3229-L3232)[^ksa-program] `CelestialSystem.OnDrawUi` culls objects outside the camera's field of view but never the controlled vehicle (it is given distance -1 and so sorted last), so the controlled vehicle's `OnDrawUi` runs exactly once per frame per viewport that calls `Universe.OnDrawUi`.[current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L376-L388)[^ksa-celestialsystem]
- **Secondary viewports.** `GameViewport.DrawImGui` calls `Universe.OnDrawUi(this)` as well, which is why a postfix must compare the viewport argument with `Program.MainViewport`.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L247-L250)[^ksa-gameviewport]
- **State to read.** `Vehicle` exposes `TotalMass`, `AccelerationBody`, `PhysicsEnvironment`, `Parts` (a `PartTree`), `Parent` and `GetBarometricAltitude()`; `ComputeActiveThrust(float ambientPressure)` returns full-throttle thrust of active engines; each nozzle's live state is `PartTree.RocketNozzles` with `State.Performance.TotalThrust` (mass flow times effective exhaust velocity).[current/decomp/KSA/RocketPerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketPerformance.cs#L5-L15)[^ksa-rocketperformance]
- `KittenEva` derives from `Vehicle` without overriding `OnDrawUi`, so the HUD also appears while controlling a kitten on EVA (per the mod feature page).

## Lifecycle and timing

The postfix runs in `frame:draw-ui`, inside the ImGui frame, so it may open windows directly. By then `frame:apply-solvers` has copied the finished physics step onto the vehicle in `PrepareFrame`; reads are consistent within the frame and the mod writes nothing to the vehicle. Hiding the game UI (`DrawUI == false`) skips the whole chain, so the HUD, alerts and the telemetry sampling the mod does in the same call all pause. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch the draw call in `[StarMapAllModsLoaded]`**, with `PatchAll` picking up every `[HarmonyPatch]` class in the assembly (and `UnpatchAll` in the unload hook):

```csharp
// Mod.cs L21-L27
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    _harmony = new Harmony("com.matthew.kittenengineerredux");
    _harmony.PatchAll(typeof(Mod).Assembly);
    DefaultCategory.Log.Info("[KER] Loaded and patched.");
}
```

2. **Write the postfix with the filter.** Return unless this is the main viewport and the controlled vehicle:

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

3. **Fill one frame record**, then draw from it. The data comes from the mod's calculators plus live game state; the thrust reads each active engine's nozzles:

```csharp
// FlightHud.cs L96-L106
public static void Draw(Vehicle vehicle, GameViewport viewport)
{
    SettingsStore.Tick();

    if (!Visible)
        return;

    FrameData f = Frame;
    f.Vehicle = vehicle;
    f.Orbit = OrbitSummaryCalculator.Compute(vehicle);
    f.SuicideBurn = SuicideBurnCalculator.Compute(vehicle, f.Orbit);
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

4. **Sum live nozzle thrust and mass flow** for active engines, rather than using the rated figure:

```csharp
// ActiveEngineThrust.cs L12-L35
public static ActualEnginePerformanceInfo ComputeActual(PartTree parts)
{
    float totalThrust = 0f;
    float totalFlowRate = 0f;
    Span<EngineController> engines = parts.Modules.Get<EngineController>();
    for (int i = 0; i < engines.Length; i++)
    {
        EngineController engine = engines[i];
        if (!engine.IsActive)
            continue;

        foreach (RocketCore core in engine.Cores)
        {
            var nozzles = parts.RocketNozzles.GetModulesAndStates(core.Rocket.Nozzles.AsSpan()).GetEnumerator();
            while (nozzles.MoveNext())
            {
                var nozzle = nozzles.Current;
                totalThrust += nozzle.State.Performance.TotalThrust;
                totalFlowRate += nozzle.State.Performance.MassFlowRate;
            }
        }
    }
    return new ActualEnginePerformanceInfo(totalThrust, totalFlowRate);
}
```

5. **Draw the window from the cached record** with the mod's draw-list kit ([collapsible sections that pop out](../build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-5.md)); sections read only `Frame`, so a docked section and a popped-out one show identical numbers.

## Complete example

| File | Role |
|---|---|
| [Mod.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Mod.cs#L16-L27) | StarMap entry and `PatchAll` |
| [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs#L6-L18) | Postfix with filters |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L96-L199) | Frame record, window, sections |
| [ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs#L12-L35) | Live nozzle thrust |

## Variations and gotchas

- **Declare `IGameViewport` in your own patch.** KER declares `GameViewport inViewport`, while the target takes `IGameViewport`; Harmony binds injected parameters by name, with no type check, and it works only because `GameViewport` is the sole implementation in this build (per the mod feature page; not re-tested here).
- **Popped-out windows live inside this call**, so they vanish when the HUD is not drawn (editor open, no controlled vehicle, UI hidden).
- **`TWR (max)` uses `ComputeActiveThrust`**, which counts active engines that still have propellant; inactive next-stage engines do not count.
- **StarMap hook versus postfix.** A `[StarMapAfterGui]` window (approach 1) draws regardless of vehicle and viewport; a postfix inherits the game's decision about whether to draw the vehicle's UI. Patching a stock method also means the patch can break on a game update.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of show-live-vehicle-data-in-a-window](overview.md)
- [Approach 1: plain ImGui window from StarMapAfterGui](show-live-vehicle-data-in-a-window-1.md)
- [Approach 3: editor postfix](show-live-vehicle-data-in-a-window-3.md)
- [Collapsible sections that pop out into their own windows](../build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-5.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../draw-lists/build-custom-drawn-imgui-widgets/overview.md)
- Mod feature: [Flight HUD on the controlled vehicle](../../../../mods/kitten-engineer-redux/features/flight-hud-controlled-vehicle-postfix.md)

[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-rocketperformance]: KSA.RocketPerformance (decompiled, build 2026.10.10.5554)
