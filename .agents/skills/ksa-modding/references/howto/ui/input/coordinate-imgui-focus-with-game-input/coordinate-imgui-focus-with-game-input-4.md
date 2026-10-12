---
type: How-To
title: "Share input with the game: WantCaptureKeyboard drops key events; Vehicle.OnKey patches add new key meanings"
description: Understand how KSA drops and clears flight input while ImGui has the keyboard, and give a vehicle a key it does not normally hear with OnKey and ProcessInput patches
category: ui
group: input
topic: coordinate-imgui-focus-with-game-input
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/drive-with-flight-keys]
techniques: [harmony-prefix, harmony-postfix, reflection, starmap-lifecycle]
hooks:
  - harmony-prefix KSA.Vehicle.OnKey
  - harmony-postfix KSA.Vehicle.OnKey
  - harmony-postfix KSA.Vehicle.ProcessInput
  - reflection KSA.Vehicle._manualControlInputs
  - reflection KSA.Vehicle._engineFlags
lifecycle: [frame:input, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.GameSettings
  - KSA.Program
  - KSA.ThrusterMapFlags
  - KSA.ManualControlInputs
  - KSA.EngineFlags
  - KSA.InputAction
  - KSA.InputEvents
  - KSA.Input
tags: [input, keys, vehicle, harmony, thruster-flags, flight-keys, sprint]
---
KSACars has no key bindings of its own: the pitch keys are throttle and brake, the yaw keys steer, and a few more keys work a flying car's rockets. Most of that needs no
patch at all, because KSA already records held flight keys on the vehicle and the mod reads them back each physics step. This article covers the two halves of "sharing
the keyboard with the game": how the engine itself drops and clears flight input when ImGui has the keyboard (so you know when your reads go empty), and the three Harmony hooks that
give a vehicle a key it normally ignores. Demonstrated by KSACars.

## Background: how the game does it

Key events travel this path, all on the main thread during `Glfw.PollEvents` in `PrepareFrame` (`frame:input`):

1. `Program.OnKey` runs `GameSettings.OnKeyAll`, `Popup.OnKeyAll` and the console; then, only if `!ImGui.GetIO().WantCaptureKeyboard`, `DispatchKeyEvent`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754)[^ksa-program]
2. `DispatchKeyEvent` asks the editor, the camera, then the active controller, and finally the controlled vehicle's `Vehicle.OnKey` (see
   [approach 5](coordinate-imgui-focus-with-game-input-5.md) for the controller half).[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1776)[^ksa-program]
3. `Vehicle.OnKey` matches the key against the bound `InputAction`s and **queues** an action: `AddThrusterFlag` and the engine throttle keys both end in
   `InputEvents.VehicleInputBuffer.Add(new VehicleInputData { Vehicle, Action, KeyAction, Modifiers })`.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3230-L3254)[^ksa-vehicle][current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3340-L3360)[^ksa-vehicle]
4. `InputEvents.ApplyInputEvents` drains the buffer and calls `Vehicle.ProcessInput(action, keyAction, modifiers)` for each queued action. `ProcessInput` is where the held state
   is written: `ThrusterCommandFlags |= ThrusterActionLookup.Get(action)` on press for the twelve flight actions (Forward = `PitchDown`, Backward = `PitchUp`, Left = `YawLeft`, Right =
   `YawRight`, `RollLeft`/`RollRight` and the six translations), `_engineFlags` for throttle up and down, `EngineOn` for startup and shutdown.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536-L3606)[^ksa-vehicle][current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1127)[^ksa-inputevents]
5. `Vehicle.PrepareWorker` (at `Universe.ExecuteNextVehicleSolvers`, `frame:dispatch-solvers`) calls `ClearHeldPlayerInput()` for the controlled vehicle when it is not active,
   ImGui wants the keyboard, or the simulation speed is above 30x. The clear resets thruster flags, sprint, grab and engine flags.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2397-L2401)[^ksa-vehicle][current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6094-L6100)[^ksa-vehicle]

Consequence: with a text field focused you get no new key events (step 1) **and** the held ones are released (step 5). The flags are the previous `NewFrame`'s, since input
is polled before `PrepareImGui`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Lifecycle and timing

| Where | Hook | Phase |
|---|---|---|
| Key event reaches a vehicle | Harmony prefix and postfix on `Vehicle.OnKey(GlfwKeyEvent)` | `frame:input` |
| Queued action applied | Harmony postfix on `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` | `frame:input` (inside `ApplyInputEvents`) |
| Flags read for the physics step | The mod's `Vehicle.PrepareWorker` prefix reads `GetThrusterFlags()` | `frame:dispatch-solvers` |

A key pressed this frame affects this frame's physics step. These hooks run inside the engine's input dispatch, so nothing in them may throw.

## Steps

1. **Read held keys with no patch.** `ProcessInput` already maps the flight actions onto `ThrusterMapFlags` in the craft's `_manualControlInputs` (see Background), and `Vehicle.GetThrusterFlags()` exposes them.
   KSACars says a craft only needs a command source (`<Control />`) for KSA to treat it as controllable and fill them. Read them each step:

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

2. **Reach private state by reflection, type-checked.** The engine switch and throttle keys are private (`_manualControlInputs`, `_engineFlags`) and have no accessor. Look the fields up once and
   check the field type, so a rename degrades to "rockets cannot be lit from the keys" and not a crash:

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
// src/KSACars/Ksa/KsaWorld.cs L180-L193
public static bool TrySetSprint(Vehicle craft, bool held)
{
    try
    {
        if (ManualInputs?.GetValue(craft) is not ManualControlInputs inputs) return false;
        inputs.Sprint = held;
        ManualInputs.SetValue(craft, inputs);
        return true;
    }
    catch
    {
        return false;
    }
}
```

3. **Make a vehicle respond to a key it normally ignores** (the sprint key is only heard by a kitten on EVA). A postfix on `Vehicle.OnKey` queues a `VehicleInputData` the same way
   `OnKey` queues the engine keys:

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

4. **Record it where the engine reads it back.** A postfix on `Vehicle.ProcessInput` writes `ManualControlInputs.Sprint` by reflection so `Vehicle.GetSprintInput()` returns it, and
   `ClearHeldPlayerInput` clears it with the other held keys. The same postfix flips the car's downforce on the RCS key:

```csharp
// src/KSACars/Ksa/BoostHook.cs L129-L152
// Inside the engine's input dispatch: nothing here may throw.
private static void AfterInput(Vehicle __instance, InputAction action, GlfwKeyAction keyAction)
{
    try
    {
        if (keyAction == GlfwKeyAction.Repeat || Buggies.Of(__instance) is not { } car) return;

        // The RCS key, which a car has nothing else to do with, switches the downforce rockets.
        if (action == InputAction.ToggleRCS && keyAction == GlfwKeyAction.Press && car.Drive.Profile.HasDownforce)
        {
            car.Downforce = !car.Downforce;
        }

        if (action != InputAction.Sprint || !car.Drive.Profile.HasBoost) return;

        KsaWorld.TrySetSprint(__instance, keyAction == GlfwKeyAction.Press);
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("reading the boost key failed", e);
    }
}
```

5. **Strip modifiers you are using as a key.** The mod reports that vehicle key bindings match modifiers exactly, so holding Shift for boost would silence W/A/S/D. A prefix on
   `Vehicle.OnKey` takes `Shift` off every other key event while a car boosts:

```csharp
// src/KSACars/Ksa/BoostHook.cs L84-L103
// KSA matches the steering, throttle and engine keys with their modifiers exactly, so with Shift
// held as the boost none of them is heard, and one released under it is never let go. While a car
// is boosting, every other key reaches it as if Shift were up. Nothing here may throw.
private static void BeforeKey(Vehicle __instance, ref GlfwKeyEvent keyEvent)
{
    try
    {
        if (keyEvent.IsMouse || (keyEvent.Mods & GlfwModifier.Shift) == 0) return;
        if (!__instance.GetSprintInput() || Input.Contains(in keyEvent, InputAction.Sprint)) return;
        if (Buggies.Of(__instance) is not { } car || !car.Drive.Profile.HasBoost) return;

        keyEvent = new GlfwKeyEvent(keyEvent.Window, keyEvent.Action, keyEvent.Key, keyEvent.Mods & ~GlfwModifier.Shift);
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("reading a key under the boost failed", e);
    }
}
```

6. **Patch by exact signature and pin it.** Look the methods up with `AccessTools.Method(type, name, parameterTypes)` and warn, never throw, when one is missing; a never-called
   `PinTheSignature` method puts the patched members in the assembly's metadata so an API-surface diff catches changes:

```csharp
// src/KSACars/Ksa/BoostHook.cs L34-L67
public static void Install()
{
    if (Installed) return;

    try
    {
        MethodInfo? target = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.ProcessInput),
            [typeof(InputAction), typeof(GlfwKeyAction), typeof(GlfwModifier)]);
        if (target is null)
        {
            Log.Warn("KSA has no Vehicle.ProcessInput to hook; a car's boost cannot be lit from the keyboard");
            return;
        }

        MethodInfo? onKey = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.OnKey), [typeof(GlfwKeyEvent)]);
        if (onKey is null)
        {
            Log.Warn("KSA has no Vehicle.OnKey to hook; a car's boost cannot be lit from the keyboard");
            return;
        }

        MethodInfo postfix = typeof(BoostHook).GetMethod(nameof(AfterInput), BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo keyed = typeof(BoostHook).GetMethod(nameof(AfterKey), BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        MethodInfo unshifted = typeof(BoostHook).GetMethod(nameof(BeforeKey), BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony.Patch(onKey, prefix: new HarmonyMethod(unshifted), postfix: new HarmonyMethod(keyed));
        Installed = true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook vehicle input; a car's boost cannot be lit from the keyboard ({e.Message})");
    }
}
```
```csharp
// src/KSACars/Ksa/BoostHook.cs L154-L159
// Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
private static void PinTheSignature(Vehicle vehicle)
{
    vehicle.ProcessInput(InputAction.Sprint, GlfwKeyAction.Press, default);
    vehicle.OnKey(default);
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/BoostHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/BoostHook.cs) | Sprint and RCS keys for a car; Shift stripping |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L735-L748) | `ReadInput` from `GetThrusterFlags()` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L20-L28) | Reflected private fields, type-checked |

## Variations and gotchas

- **Own your throttle.** `Vehicle.PrepareWorker` clamps `EngineThrottle` to `GetMinThrottle()`, which is 1 on a craft with no engine; KSACars ramps its own rocket throttle from the held keys.
- **Only the controlled craft hears keys.** A parked car with nobody flying it reads empty flags, which lets an idle railed car return early from the physics step.
- **Shift is a modifier, not just a key** (see step 5). **R toggles both** the car's downforce and KSA's `FlightComputer.ToggleRCSMode()`, because the mod postfixes rather than replaces `ProcessInput`.
- **Held input is cleared under ImGui keyboard capture and above 30x warp.** Do not store the only copy of a held key in a place the engine clears unless you want that.
- **Version.** Both hooked methods are public; checked against 2026.10.10.5554.

## See also

- [Overview of coordinate-imgui-focus-with-game-input](overview.md)
- [Approach 3: NoNav windows that never take the keyboard](coordinate-imgui-focus-with-game-input-3.md)
- [Approach 5: a FixedController subclass swallows input](coordinate-imgui-focus-with-game-input-5.md)
- [Add to, drive and hide the stock cockpit gauges and navball](../../menus/work-with-stock-cockpit-gauges/overview.md)
- Mod feature: [Drive with flight keys](../../../../mods/ksacars/features/drive-with-flight-keys.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
