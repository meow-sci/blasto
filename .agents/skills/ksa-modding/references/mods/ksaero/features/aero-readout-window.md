---
type: Mod Feature
title: Aero readout window
description: An auto-sized ImGui window drawn from StarMapAfterGui that finds the controlled vehicle, reads its main-thread atmosphere and kinematic state and re-runs the physics model to show Mach, q, angle of attack, lift and drag
mod: ksaero
category: ui
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.Vehicle
  - KSA.PhysicsEnvironment
  - KSA.PhysicsStates
  - KSA.VehicleProperties
  - Brutal.ImGuiApi.ImGui
tags: [imgui, ui, readout, controlled-vehicle, starmap]
---

While the controlled vehicle is inside an atmosphere, a small auto-sized window titled "KSAero" shows
Mach, dynamic pressure in kPa, angle of attack (with a tail-first marker), C_L, C_D, L/D, lift and drag
in kN, a stall warning, and the equivalent cylinder's length, diameter and fineness. It disappears
outside the atmosphere and when the patch is not installed. It runs the same `AeroModel.Evaluate` the
physics does, on the same kind of inputs, so what it shows is what the solver applies (before the
submerged fraction and the limiter).[^ksaero-aeroreadout] [^ksaero-readme]

## How it works

1. `Mod.DrawGui(double dt)` is `[StarMapAfterGui]` and calls `AeroReadout.Draw()` only when the Harmony
   patch is live ([KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L38-L43)).[^ksaero-mod]
2. `Draw` wraps everything in `try`/`catch` and logs the first failure once, so a bug in the UI can
   never spam the log or break the frame ([KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L15-L29)).
3. **Find the vehicle.** `Program.ControlledVehicle` (a static property, `null` when nothing is
   controlled) and `Vehicle.IsDisposed` guard the rest
   ([KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L33-L35),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515),
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L618)).[^ksa-program]
4. **Find its atmosphere.** `ref readonly PhysicsEnvironment environment = ref vehicle.PhysicsEnvironment`
   binds to the vehicle's struct without copying it (`Vehicle` exposes `BubbleOrigin`,
   `KinematicStates`, `Props` and `PhysicsEnvironment` as `ref readonly` properties,
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L537-L543)). The window is skipped
   unless `InPhysicsRadius` is true and `AtmosphericDensity > 0`, the same gate the physics uses.[^ksa-vehicle] [^ksa-physicsenvironment]
5. **Same inputs as the solver.** The cylinder comes from `AirForcePatch.BodyFor(in props)`; the air
   velocity in body axes from the game's public `PhysicsStates.ComputeAirVelocityBody(in origin, in
   kinematic, in environment)`, which subtracts the parent's rotation in an inertial bubble frame, the
   same correction `ComputeDerivatives` applies
   ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L181-L190)).[^ksa-physicsstates]
   Then q, the speed of sound, Mach and `AeroModel.Evaluate`; below 1 mm/s the evaluation is skipped
   and the coefficients stay zero ([KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L41-L55)).[^ksaero-airforcepatch]
6. **Draw.** `ImGui.Begin("KSAero", ImGuiWindowFlags.AlwaysAutoResize)`; `ImGui.End()` is in a `finally`,
   so it runs whether the window is collapsed (early `return`) or a `Text` call throws. Text uses
   `string.Format` with `CultureInfo.InvariantCulture`, so decimal separators do not follow the
   player's locale ([KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L57-L78)). `ImGui.Begin`,
   `Text`, `TextDisabled`, `Separator` and `End` are Brutal's bindings
   ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5595)).[^imgui-api]

## Lifecycle and timing

- **Hook: `frame:draw-ui`.** StarMap invokes `[StarMapAfterGui]` methods from a Harmony postfix on
  `Program.OnDrawUiViewports` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36);
  signature rule `void M(double dt)` in [StarMap.API/OnGuiAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnGuiAttributes.cs#L66-L75)).
  In `Program.OnFrame` that call sits after `PrepareImGui` (ImGui `NewFrame`) and before `ImGui.Render`,
  inside `if (DrawUI)`, so ImGui calls are legal and the window vanishes with the rest of the HUD when
  the player hides it ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)).[^starmap-programpatcher] [^starmap-onguiattributes]
- **State it reads is settled.** Earlier in the same frame, `Program.PrepareFrame` waited for the
  vehicle solver and ran `Universe.ApplyVehicleSolvers`, which copies the finished step onto each
  `Vehicle` through `VehicleUpdateTask.ApplyResultsToMainThread` and
  `Vehicle.UpdateFromTaskResultsUnsynchronized`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2158-L2167),
  [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L258),
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2460)).[^ksa-vehicleupdatetask]
  The next step was then dispatched (`Universe.ExecuteNextVehicleSolvers`) and runs on the workers'
  own state copies, so the `Vehicle` fields do not change until the next frame's apply. Reading them
  here needs no lock; the values are one solver step behind what the workers are computing.[^ksa-universe]
- **Why this hook.** It needs an open ImGui frame and the controlled vehicle, nothing earlier. Doing the
  same evaluation inside the physics patch and handing the numbers to the UI would need cross-thread
  publication; re-evaluating the pure model on the main thread avoids that entirely.

## Key code

| Path | Role |
|---|---|
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L38-L43) | `[StarMapAfterGui]` entry, gated on the live patch |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L15-L29) | Log-once exception guard |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L31-L55) | Controlled vehicle, atmosphere gate, model inputs |
| [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L57-L78) | ImGui window with `End` in `finally`, invariant formatting |

## Snippets

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

```csharp
// KSAero/AeroReadout.cs L57-L75
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
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515) | The vehicle the player controls |
| `Vehicle.IsDisposed` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L618) | Skip destroyed vehicles |
| `Vehicle.BubbleOrigin`, `KinematicStates`, `Props`, `PhysicsEnvironment` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L537-L543) | `ref readonly` views of the main-thread state |
| `PhysicsEnvironment.InPhysicsRadius`, `AtmosphericDensity`, `AtmosphericPressure` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L20-L24) | Atmosphere gate and Mach inputs |
| `PhysicsStates.ComputeAirVelocityBody` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L181-L190) | Air-relative velocity in body axes |
| `ImGui.Begin`, `Text`, `TextDisabled`, `Separator`, `End`; `ImGuiWindowFlags.AlwaysAutoResize` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5595) | Window drawing |

## Pitfalls and version notes

- **Not identical to the applied force.** Lift and drag in kN are `C * q * A_ref`; the solver also
  multiplies by the exposed (above-water) fraction and the semi-implicit limiter. The readout's air
  velocity is a packed `float3`, the solver's a `double3`.
- **The stall line** only checks nose-first flight against `StallAngleDeg`; tail-first attached-lift
  fading is not flagged.
- **Allocations per frame.** `Format(string, params object[])` boxes every number. Harmless at UI rate,
  but this style must never be copied into the physics path.
- **Hidden HUD hides it.** `StarMapAfterGui` does not fire when `Program.DrawUI` is false.
- **EVA kittens** are vehicles too, so the window shows for a kitten in atmosphere.

## See also

- [Body-of-revolution aerodynamics](body-of-revolution-aerodynamics.md)
- [Mach number from the game atmosphere](mach-from-game-atmosphere.md)
- [Patch refusal and rollback](patch-refusal-and-rollback.md)
- [Live vehicle data: a plain ImGui window from StarMapAfterGui with guarded Begin and End](../../../howto/ui/windows/show-live-vehicle-data-in-a-window/show-live-vehicle-data-in-a-window-1.md) - how-to article demonstrating this feature

[^ksaero-aeroreadout]: KSAero/AeroReadout.cs
[^ksaero-readme]: KSAero/README.md
[^ksaero-mod]: KSAero/Mod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^imgui-api]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-onguiattributes]: StarMap.API/OnGuiAttributes.cs
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
