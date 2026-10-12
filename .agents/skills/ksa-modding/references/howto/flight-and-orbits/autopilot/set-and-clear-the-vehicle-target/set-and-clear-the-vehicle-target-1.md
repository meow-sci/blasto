---
type: How-To
title: Set and clear the target with Universe.SetTargetCommand and UnsetTargetCommand
description: Select an orbiting body or star as the vehicle target from an ImGui button using the game's own terminal commands, which queue a ChangeTargetData for the input point so the change matches stock timing
category: flight-and-orbits
group: autopilot
topic: set-and-clear-the-vehicle-target
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/native-target-selection]
techniques: [input-event-buffer]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Universe, KSA.InputEvents, KSA.Vehicle, KSA.IOrbiter, KSA.OrbitingStar, KSA.Program]
tags: [target, set-target, unset-target, change-target-data, terminal-action, button]
---

Outcome: a "Select X as Target" button that sets the controlled vehicle's target, and a second press that clears it, indistinguishable from the stock Target Track window. Compendium offers it on every body and star page.[Target orbiting bodies through the game's settarget command](../../../../mods/compendium/features/native-target-selection.md)
Prerequisite: StarMap.

## Background: how the game does it

See the [overview](overview.md). `SetTargetCommand` refuses a self-target, resolves both ids with `CurrentSystem.Get`, requires a `Vehicle` and an `IOrbiter`, and adds an `InputEvents.ChangeTargetData` to `ChangeTargetBuffer`; `UnsetTargetCommand` does the same with a null target.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L433-L515)
The game's Target Track window uses these two commands, and Shift+click on a body or orbit under the cursor (`IOrbiter.OnMouseButtonOrbit`, reached from `Program.OnMouseButton` during input polling) queues a `ChangeTargetData` directly.[^ksa-targettrackwindow] [current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L310-L317) [^ksa-iorbiter] [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L327-L370)
`OrbitingStar` is an `IOrbiter`, so stars that orbit a barycenter are valid targets; the stock map view skips them when picking because `CelestialSystem.SetNearestOrbitPoint` skips every `StellarBody`.[^ksa-celestialsystem] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L496-L506)

## Lifecycle and timing

- **Click: `frame:draw-ui`.** The button runs inside the `[StarMapAfterGui]` window; the command only queues.
- **Apply: `frame:input`.** `ApplyInputEvents` runs in the next frame's `PrepareFrame`, so the next step starts with the new target; the indicator appears one frame after the click.
- **Interaction with other mods.** The new native target passes through `Vehicle.SetTarget`, which any prefix (such as the navigation-only target in [approach 2](set-and-clear-the-vehicle-target-2.md)) sees.

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the vehicle:** `Program.ControlledVehicle`; disable the button with a "No Controlled Vehicle" note when null.
2. **Decide whether the body is a native target:** anything that is an `IOrbiter` (planets, moons, small bodies, `OrbitingStar`); roots go to the other path.
3. **Is it already the target?** `vehicle.Target == astronomical`.
4. **Press sets or clears by id:**

```csharp
// src/CdmHelpers.cs L83-L119
private static void DrawTargetControls(Astronomical astronomical)
{
    bool rootTarget = RootNavigationTargets.Supports(astronomical);
    if (astronomical is not IOrbiter && !rootTarget)
    {
        return;
    }

    var vehicle = Program.ControlledVehicle;
    bool isTarget = vehicle != null && (rootTarget
        ? RootNavigationTargets.GetTarget(vehicle) == astronomical
        : vehicle.Target == astronomical);
    using (new ImGuiDisabledScope(vehicle == null || (rootTarget && !RootNavigationTargets.PatchesInstalled)))
    {
        if (ImGui.Button(new ImString($"Select {GetBodyDisplayName(astronomical)} as Target")) && vehicle != null)
        {
            if (isTarget)
            {
                if (rootTarget)
                {
                    RootNavigationTargets.QueueSelection(vehicle, null);
                }
                else
                {
                    Universe.UnsetTargetCommand(vehicle.Id.ToString());
                }
            }
            else if (rootTarget)
            {
                RootNavigationTargets.QueueSelection(vehicle, astronomical);
            }
            else
            {
                Universe.SetTargetCommand(vehicle.Id.ToString(), astronomical.Id.ToString());
            }
        }
    }
```

5. **Do not read the target back in the same frame.** `ChangeTargetData.Apply` sets it and shows the alert next frame.
6. **Use the same idiom for any UI:** `Universe.SetTargetCommand(vehicle.Id.ToString(), body.Id.ToString())` and `Universe.UnsetTargetCommand(vehicle.Id.ToString())`, or add a `ChangeTargetData` yourself when you hold the `IOrbiter` (and optionally a `TargetPart`).

## Complete example

| File | Role |
|---|---|
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L83-L144) | target button, current-target indicator, no-vehicle note |
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L209-L283) | star page that hosts the button |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L689) | body page call site |

## Variations and gotchas

- **Never call `Vehicle.SetTarget` directly from a draw hook**: the solver's navigation and the target nodes read it.
- **`UnsetTargetCommand` returns early when `vehicle.Target == null`**, so it cannot clear a mod-held navigation target (approach 2 patches it).
- **Ids are strings**: both commands take ids, not objects, and log an error for unknown ids or non-vehicles.
- A target change also recalculates the flight plan's and burn plan's target nodes, so expect a small cost the frame it applies.

## See also

- [Overview](overview.md); [root navigation targets](set-and-clear-the-vehicle-target-2.md)
- [Target orbiting bodies through the game's settarget command](../../../../mods/compendium/features/native-target-selection.md)
- [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-targettrackwindow]: KSA.TargetTrackWindow (decompiled, build 2026.10.10.5554)
[^ksa-iorbiter]: KSA.IOrbiter (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
