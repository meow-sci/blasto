---
type: Mod Feature
title: Drive with the flight keys
description: Map a car's throttle, brake, steering and air controls onto KSA's existing vehicle key bindings by reading the craft's held thruster flags, reflected engine switch and throttle keys, and a patched-in sprint key
mod: ksacars
category: ui
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.Vehicle.OnKey
  - harmony-postfix KSA.Vehicle.OnKey
  - harmony-postfix KSA.Vehicle.ProcessInput
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - reflection KSA.Vehicle._manualControlInputs
  - reflection KSA.Vehicle._engineFlags
lifecycle: [frame:input, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.ThrusterMapFlags
  - KSA.ManualControlInputs
  - KSA.EngineFlags
  - KSA.InputAction
  - KSA.InputEvents
  - KSA.Input
  - KSA.Program
  - RenderCore.Input.GlfwKeyEvent
tags: [input, keys, controls, thruster-flags, reflection, harmony, sprint, rcs]
---

A car has no key bindings of its own. KSACars reuses the ones the player already flies with: the
pitch keys (W/S) are throttle and brake-then-reverse, the yaw keys (A/D) steer, and on the
flying Eldorado the engine start/stop (Z/X), throttle (Up/Down), roll (Q/E), sprint (Left Shift)
and RCS (R) keys work its rockets.[^ksacars-readme] Most of it needs no patch at all: the engine
already records held keys on the vehicle, and the mod reads them back each physics step. Only the
sprint key, which a vehicle never hears, needs Harmony.

## How it works

**Held thruster keys (no patch).** KSA maps the flight actions onto `ThrusterMapFlags` on every
controllable craft: `InputAction.Forward` (W) sets `PitchDown`, `Backward` (S) `PitchUp`, `Left`
(A) `YawLeft`, `Right` (D) `YawRight`, `RollLeft`/`RollRight` (Q/E) the roll flags, stored in
`_manualControlInputs.ThrusterCommandFlags` and exposed by `Vehicle.GetThrusterFlags()`.[^ksa-vehicle]
`Buggies.ReadInput` turns them into `DriveInput(Throttle = PitchDown - PitchUp, Steer = YawLeft - YawRight)`
and `ReadTurn` into the air-turn input.[^ksacars-buggies] A craft only needs a command source
(`<Control />`, see [car part game data](./car-part-gamedata.md)) for KSA to treat it as
controllable and fill these flags.

**Engine switch and throttle keys (reflection).** Start and shutdown set
`_manualControlInputs.EngineOn` inside `Vehicle.ProcessInput`; throttle up and down set bits in the
private `_engineFlags`. `KsaWorld` reflects both fields once, checks their field types, and reads
them with `EngineOn(craft)` and `ThrottleKeys(craft)`.[^ksacars-ksaworld] The mod ramps its **own**
rocket throttle from the held keys instead of using `GetManualThrottle()`, because
`Vehicle.PrepareWorker` clamps `EngineThrottle` to `GetMinThrottle()` (`Parts.EngineThrottleMin`),
which is 1 on a craft with no engine.[^ksacars-notes]

**Sprint and RCS (`BoostHook`).**[^ksacars-boosthook]

| Patch | What it does |
|---|---|
| Postfix on `Vehicle.OnKey(GlfwKeyEvent)` | When the key matches `InputAction.Sprint` and the vehicle is a car with a boost, queue an `InputEvents.VehicleInputData { Action = Sprint }` into `InputEvents.VehicleInputBuffer`, the same way `OnKey` queues the throttle keys |
| Postfix on `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` | For `Sprint`, write `ManualControlInputs.Sprint` by reflection (`KsaWorld.TrySetSprint`) so `Vehicle.GetSprintInput()` reads it back; for `ToggleRCS` press, flip the car's downforce thrusters |
| Prefix on `Vehicle.OnKey` | While a car is boosting, strip `Shift` from every other key event, because KSA matches vehicle keys with their modifiers exactly and would otherwise ignore W/A/S/D under a held Shift |

Storing sprint in `_manualControlInputs` (rather than a mod field) means KSA's own
`ClearHeldPlayerInput` clears it with the other held keys.

**Bridge override.** `Buggies.Hold(craft, input, seconds, turn, boost)` lets the
[command bridge](./file-drop-command-bridge.md) hold an input for a number of simulated seconds,
taking priority over the keys.

## Lifecycle and timing

Everything happens on the main thread inside `Program.PrepareFrame`, in this order:[^ksa-program]

1. `Glfw.PollEvents()` delivers key events to `Program.OnKey` (the GLFW key callback). It drops the
   event when the console, a popup or ImGui (`WantCaptureKeyboard`) has the keyboard, and otherwise
   `DispatchKeyEvent` calls `ControlledVehicle.OnKey` (only while `IsControlledVehicleActive`).
   **The `OnKey` prefix and postfix run here** (`frame:input`).
2. `InputEvents.ApplyInputEvents()` drains `VehicleInputBuffer`, calling
   `Vehicle.ProcessInput` for each queued action. **The `ProcessInput` postfix runs here.**[^ksa-inputevents]
3. `Universe.ExecuteNextVehicleSolvers` calls `Vehicle.PrepareWorker`; **the mod's physics prefix
   reads the flags here** (`frame:dispatch-solvers`) and turns them into forces for the step about
   to run.

So a key pressed this frame affects this frame's physics step. `PrepareWorker` then clears the
flown craft's held input (`ClearHeldPlayerInput`: thruster flags, sprint, grab, engine flags) if
the controlled vehicle is not active, ImGui wants the keyboard, or the simulation speed is above
30x.[^ksa-vehicle] That is why the mod's panel never takes keyboard focus (see
[car panel](./car-panel-and-modmenu.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L735-L758) | `ReadInput` and `ReadTurn` from `GetThrusterFlags()` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L536-L562) | Per-step input gathering: bridge hold, sprint, engine keys, throttle ramp |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L20-L28) | Reflected `_manualControlInputs` and `_engineFlags`, type-checked |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L132-L208) | `EngineOn`, `TrySetEngineOn`, `TrySetSprint`, `ThrottleKeys` |
| [src/KSACars/Ksa/BoostHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BoostHook.cs) | Sprint and RCS keys for a car; Shift stripping |

## Snippets

```csharp
// src/KSACars/Ksa/Buggies.cs L735-L748
    // The craft's own held controls, so the player's bindings carry over: the pitch keys are the
    // throttle and the yaw keys steer. Nothing is held on a craft nobody is flying.
    private static DriveInput ReadInput(Vehicle craft)
    {
        ThrusterMapFlags flags;
        try { flags = craft.GetThrusterFlags(); }
        catch { return default; }

        double throttle = (flags.HasFlag(ThrusterMapFlags.PitchDown) ? 1.0 : 0.0)
                          - (flags.HasFlag(ThrusterMapFlags.PitchUp) ? 1.0 : 0.0);
        double steer = (flags.HasFlag(ThrusterMapFlags.YawLeft) ? 1.0 : 0.0)
                       - (flags.HasFlag(ThrusterMapFlags.YawRight) ? 1.0 : 0.0);
        return new DriveInput(throttle, steer);
    }
```

```csharp
// src/KSACars/Ksa/KsaWorld.cs L20-L28
    // A craft's engine switch and its held throttle keys are private to it, and KSA has no accessor for
    // either. Null when KSA has renamed them, and then a car's rockets cannot be lit from the keys.
    private static readonly FieldInfo? ManualInputs =
        typeof(Vehicle).GetField("_manualControlInputs", BindingFlags.NonPublic | BindingFlags.Instance) is { } f
        && f.FieldType == typeof(ManualControlInputs) ? f : null;

    private static readonly FieldInfo? EngineKeys =
        typeof(Vehicle).GetField("_engineFlags", BindingFlags.NonPublic | BindingFlags.Instance) is { } f
        && f.FieldType == typeof(EngineFlags) ? f : null;
```

```csharp
// src/KSACars/Ksa/BoostHook.cs L105-L127
    // Inside the engine's key handling: nothing here may throw.
    private static void AfterKey(Vehicle __instance, GlfwKeyEvent keyEvent)
    {
        try
        {
            if (keyEvent.Action == GlfwKeyAction.Repeat || !Input.Contains(in keyEvent, InputAction.Sprint)) return;
            if (Buggies.Of(__instance) is not { } car || !car.Drive.Profile.HasBoost) return;

            InputEvents.VehicleInputBuffer.Add(new InputEvents.VehicleInputData
            {
                Vehicle = __instance,
                Action = InputAction.Sprint,
                KeyAction = keyEvent.Action,
                Modifiers = keyEvent.Mods,
            });
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("reading the boost key failed", e);
        }
    }
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Vehicle.GetThrusterFlags`, `ClearHeldPlayerInput`, `GetSprintInput` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6089-L6105)) | Held flight keys; what KSA clears; sprint read-back |
| `Vehicle.OnKey(GlfwKeyEvent)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3274)) | `public virtual`; turns key events into thruster flags or queued `VehicleInputData` |
| `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536-L3605)) | `public virtual`; applies queued actions (throttle flags, engine on/off, RCS toggle) |
| `Vehicle.PrepareWorker` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2411)) | Clears held input under ImGui keyboard capture; clamps throttle to `GetMinThrottle()` |
| `Vehicle._manualControlInputs` (`ManualControlInputs`), `Vehicle._engineFlags` (`EngineFlags`) | Private; reached by reflection |
| `InputEvents.VehicleInputBuffer`, `VehicleInputData`, `ApplyInputEvents` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1010)) | Public input queue the sprint action is pushed onto |
| `Input.Contains` / default bindings ([current/decomp/KSA/Input.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Input.cs#L121-L232)) | Matching a key event to an `InputAction`; W/S/A/D/Q/E/Z/X/Up/Down/Shift/R defaults[^ksa-input] |
| `Program.OnKey` / `DispatchKeyEvent` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1999)) | Key routing to the controlled vehicle; ImGui keyboard capture drops keys |

## Pitfalls and version notes

- **Reflection is type-checked and degrades.** If KSA renames `_manualControlInputs` or
  `_engineFlags`, the fields resolve to null, the rockets cannot be lit from the keys, and driving
  (thruster flags) still works.
- **Only the controlled craft hears keys.** A parked car with nobody flying it reads empty flags,
  which is what lets an idle railed car return early from the physics step.
- **Shift is a modifier, not just a key.** KSA's vehicle bindings match modifiers exactly, so a
  "hold Shift to boost" feature silently disables every other key unless Shift is stripped (the
  prefix only does this while a boosting car is flown).
- **R toggles both** the car's downforce and KSA's own `FlightComputer.ToggleRCSMode()`; the mod
  postfixes rather than replaces `ProcessInput`.
- Both hooked methods are public and pinned by `PinTheSignature`, so a signature change fails the
  build (see [API surface pinning](./api-surface-pinning-and-diff.md)).

## See also

- [Inject wheel forces from a Vehicle.PrepareWorker prefix](./prepareworker-impulse-injection.md)
- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md)
- [Engine HUD for an engineless craft](./engine-hud-for-engineless-craft.md)
- [Car panel and ModMenu entry](./car-panel-and-modmenu.md)
- [Share input with the game: WantCaptureKeyboard drops key events; Vehicle.OnKey patches add new key meanings](../../../howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-4.md) - how-to article demonstrating this feature

[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-boosthook]: src/KSACars/Ksa/BoostHook.cs
[^ksacars-readme]: README.md
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^ksa-inputevents]: KSA.InputEvents (decompiled)
[^ksa-input]: KSA.Input (decompiled)
