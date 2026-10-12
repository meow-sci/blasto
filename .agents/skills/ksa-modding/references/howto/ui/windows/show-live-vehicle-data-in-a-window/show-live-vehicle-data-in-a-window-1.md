---
type: How-To
title: "Live vehicle data: a plain ImGui window from StarMapAfterGui with guarded Begin and End"
description: Read the controlled vehicle's settled physics state by ref readonly and show derived aerodynamic numbers in a small auto-sized window that cannot corrupt the ImGui stack when something throws
category: ui
group: windows
topic: show-live-vehicle-data-in-a-window
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksaero/features/aero-readout-window]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:apply-solvers, frame:draw-ui]
game_types: [KSA.Program, KSA.Vehicle, KSA.PhysicsEnvironment, KSA.PhysicsStates, Brutal.ImGuiApi.ImGui]
tags: [readout, controlled-vehicle, ref-readonly, try-finally, log-once, ksaero]
---
The smallest useful vehicle readout is one auto-sized window, drawn every frame from a StarMap GUI hook, that reads a handful of fields from the controlled vehicle and formats them as text. KSAero does this for aerodynamics: while the vehicle is in an atmosphere, a "KSAero" window shows Mach number, dynamic pressure, angle of attack, lift and drag coefficients, forces and a stall warning, re-running the same pure model the physics uses. This article covers the parts that generalise: where the vehicle state is safe to read, how to bind the structs without copying, and how to keep `Begin`/`End` balanced when anything throws. Demonstrated by KSAero. Prerequisite: StarMap.

## Background: how the game does it

- `Program.ControlledVehicle` is a static property that is `null` when nothing is controlled; `Vehicle.IsDisposed` is true for destroyed vehicles.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L502)[^ksa-program]
- `Vehicle` exposes its main-thread physics state as `ref readonly` properties - `BubbleOrigin`, `KinematicStates`, `Props` and `PhysicsEnvironment` - so a mod can bind a struct without copying it.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L537-L543)[^ksa-vehicle]
- `PhysicsEnvironment` carries `InPhysicsRadius`, `AtmosphericPressure` and `AtmosphericDensity`; density and pressure are zero outside an atmosphere.[current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L20-L24)[^ksa-physicsenvironment]
- `PhysicsStates.ComputeAirVelocityBody(in origin, in kinematic, in environment)` is a public static helper that returns the air-relative velocity in body axes.[current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L182-L182)[^ksa-physicsstates]

## Lifecycle and timing

`[StarMapAfterGui]` is invoked from a Harmony postfix on `Program.OnDrawUiViewports` (`frame:draw-ui`), inside `if (DrawUI)`, between `PrepareImGui` and `ImGui.Render`: ImGui calls are legal and the window vanishes with the rest of the HUD. Earlier in the same frame `Program.PrepareFrame` ran `Universe.ApplyVehicleSolvers` (`frame:apply-solvers`), which copies the finished physics step onto each `Vehicle` and then dispatches the next one on the workers' own copies.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2166-L2167)[^ksa-program] The `Vehicle` fields you read in `frame:draw-ui` therefore do not change until next frame's apply, and no lock is needed; the values are one solver step behind what the workers are computing. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the hook.** The mod's `[StarMapAfterGui]` method calls the readout only while its Harmony patch is live:

```csharp
// KSAero/Mod.cs L38-L43
[StarMapAfterGui]
public void DrawGui(double dt)
{
    if (_harmony != null)
        AeroReadout.Draw();
}
```

2. **Guard the whole draw** with `try`/`catch` that logs the first failure only, so an exception in UI code can neither spam the log nor break the frame:

```csharp
// KSAero/AeroReadout.cs L15-L29
private static bool _failureLogged;

internal static void Draw()
{
    try
    {
        DrawWindow();
    }
    catch (Exception ex)
    {
        if (!_failureLogged)
            DefaultCategory.Log.Warning($"[KSAero] Readout failed (logged once): {ex}");
        _failureLogged = true;
    }
}
```

3. **Find the vehicle and the gate.** Return early with nothing drawn when there is no controlled vehicle or no atmosphere, binding the structs by `ref readonly`:

```csharp
// KSAero/AeroReadout.cs L31-L55
private static void DrawWindow()
{
    Vehicle? vehicle = Program.ControlledVehicle;
    if (vehicle == null || vehicle.IsDisposed)
        return;

    ref readonly PhysicsEnvironment environment = ref vehicle.PhysicsEnvironment;
    if (!environment.InPhysicsRadius || environment.AtmosphericDensity <= 0f)
        return;

    ref readonly VehicleProperties props = ref vehicle.Props;
    AeroBody body = AirForcePatch.BodyFor(in props);
    if (!body.IsValid)
        return;

    float3 air = PhysicsStates.ComputeAirVelocityBody(in vehicle.BubbleOrigin, in vehicle.KinematicStates, in environment);
    double speed = air.Length();
    double density = environment.AtmosphericDensity;
    double q = 0.5 * density * speed * speed;
    double soundSpeed = AeroModel.SpeedOfSound(environment.AtmosphericPressure, density);
    double mach = soundSpeed > 0.0 ? speed / soundSpeed : 0.0;
    AeroCoefficients c = speed > 1e-3
        ? AeroModel.Evaluate(in body, air.X, air.Y, air.Z, mach)
        : default;
    double qa = q * body.ReferenceArea;
```

4. **Draw with `End` in a `finally`.** `Begin` returns false for a collapsed window, but `End` must still be called; the `finally` makes that true on the early `return` and on an exception thrown by a `Text` call. Invariant-culture formatting keeps decimal separators independent of the player's locale:

```csharp
// KSAero/AeroReadout.cs L57-L76,L78
    bool open = ImGui.Begin("KSAero", ImGuiWindowFlags.AlwaysAutoResize);
    try
    {
        if (!open)
            return;

        ImGui.Text(Format("Mach {0:F2}   q {1:F1} kPa", mach, q * 1e-3));
        ImGui.Text(Format("AoA {0:F1} deg {1}", c.AngleOfAttack * 180.0 / Math.PI, c.TailFirst ? "(tail-first)" : ""));
        ImGui.Text(Format("CL {0:F3}   CD {1:F3}   L/D {2:F2}", c.Lift, c.Drag, c.Drag > 0.0 ? c.Lift / c.Drag : 0.0));
        ImGui.Text(Format("Lift {0:F1} kN   Drag {1:F1} kN", c.Lift * qa * 1e-3, c.Drag * qa * 1e-3));
        if (!c.TailFirst && c.LeadingAngle * 180.0 / Math.PI > AeroParameters.StallAngleDeg)
            ImGui.Text("STALL (attached lift lost)");
        ImGui.Separator();
        ImGui.TextDisabled(Format("Cylinder L {0:F1} m  D {1:F2} m  L/D {2:F1}", body.Length, body.Diameter, body.Fineness));
    }
    finally
    {
        ImGui.End();
    }
}
// ...
private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
```

## Complete example

| File | Role |
|---|---|
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L38-L43) | `[StarMapAfterGui]` entry |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L13-L79) | Guard, state reads, window |

## Variations and gotchas

- **Re-evaluate, do not publish.** Running a pure model again on the main thread avoids cross-thread publication from the physics workers entirely; the number shown is not identical to the applied force, because the solver also multiplies by the exposed fraction and a limiter (per the mod's notes).
- **`Format(string, params object[])` boxes every number per frame.** Harmless at UI rate; never copy it into the physics path.
- **The HUD hides it.** `StarMapAfterGui` does not fire when `Program.DrawUI` is false; see [keep UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md).
- **EVA kittens are vehicles too**, so a window gated on `ControlledVehicle` shows for them as well (per the mod feature).
- **Version.** Checked against 2026.10.10.5554. `ref readonly` property signatures and `ComputeAirVelocityBody` are public API that a game update could change.

## See also

- [Overview of show-live-vehicle-data-in-a-window](overview.md)
- [Approach 2: Harmony postfix on Vehicle.OnDrawUi](show-live-vehicle-data-in-a-window-2.md)
- [Approach 3: Harmony postfix on VehicleEditor.OnDrawUi](show-live-vehicle-data-in-a-window-3.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)
- Mod feature: [Aero readout window](../../../../mods/ksaero/features/aero-readout-window.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
