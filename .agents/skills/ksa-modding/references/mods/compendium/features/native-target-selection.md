---
type: Mod Feature
title: Target orbiting bodies through the game's settarget command
description: The window's Select as Target button calls Universe.SetTargetCommand and UnsetTargetCommand, the same terminal actions the game's Target Track window uses, which validate the ids and queue an InputEvents.ChangeTargetData that reaches Universe.SetTarget at the frame's input point
mod: compendium
category: flight-and-orbits
techniques: [input-event-buffer, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types:
  - KSA.Universe
  - KSA.InputEvents
  - KSA.Vehicle
  - KSA.OrbitingStar
  - KSA.IOrbiter
  - KSA.CelestialSystem
  - KSA.Program
  - KSA.ImGuiDisabledScope
tags: [targets, input-events, stars, terminal-command, imgui]
---

Every body page and every star page in the window has a "Select <name> as Target" button for the
controlled vehicle. Pressing it again, or pressing the green ">> Current Target <<" indicator, clears
the target, and the button is disabled with a "( No Controlled Vehicle )" note when nothing is
controlled. In the Stars category this lets the player target an orbiting star (one member of a
multi-star system) from a list.[^cmp-readme-md] The game's own map view does not offer star orbits for picking:
`CelestialSystem.SetNearestOrbitPoint` skips every `StellarBody` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L496-L506)).[^ksa-celestialsystem]

## How it works

1. **Which path.** `DrawTargetControls(Astronomical)` decides per body: fixed stars and root barycenters
   go to the [navigation-only root targets](root-navigation-targets.md); anything that is an `IOrbiter`
   (planets, moons, small bodies, `OrbitingStar`) uses the game's own target
   ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L83-L119), [current/decomp/KSA/OrbitingStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitingStar.cs#L9)).[^cmp-cdmhelpers] [^ksa-orbitingstar]
2. **Is it already the target.** For a native target the check is `vehicle.Target == astronomical`
   against `Program.ControlledVehicle` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515)).[^ksa-program]
3. **Set or clear by id.** The button calls `Universe.SetTargetCommand(vehicle.Id.ToString(),
   astronomical.Id.ToString())`, or `Universe.UnsetTargetCommand(vehicle.Id.ToString())` to clear. These
   are the `settarget` and `unsettarget` terminal actions. `SetTargetCommand` refuses a self-target,
   resolves both ids with `CurrentSystem.Get`, requires a `Vehicle` and an `IOrbiter`, and then only adds
   an `InputEvents.ChangeTargetData { Vehicle, Target }` to `InputEvents.ChangeTargetBuffer`.
   `UnsetTargetCommand` does the same with a null target, and returns early if the vehicle has no
   target ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L433-L515), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L522-L586)).[^ksa-universe]
4. **Applied later by the game.** `InputEvents.ApplyInputEvents` applies `ChangeTargetBuffer`;
   `ChangeTargetData.Apply` calls `Universe.SetTarget(vehicle, target, part)` (or `UnsetTarget`) and
   shows a "target set" alert. `Universe.SetTarget` is just `vehicle.SetTarget(orbiter, targetPart)`,
   which updates `Target`, the target's back-references and the target nodes on the coast and burn plans
   ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L780-L801), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140),
   [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L517-L520), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2189-L2203)).[^ksa-inputevents] [^ksa-vehicle]
5. **Same idiom as the game.** The game's Target Track window sets and clears targets with these two
   commands, and Shift+clicking the body or orbit under the cursor (`IOrbiter.OnMouseButtonOrbit`,
   reached from `Program.OnMouseButton` during input polling) queues a `ChangeTargetData` directly
   ([current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L310-L317), [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L327-L370)).[^ksa-targettrackwindow] [^ksa-iorbiter]
6. **Star page.** `DrawStarInformation` shows the kind (barycenter, star orbiting X, or star), mass and
   radius in solar units, the orbit of an `OrbitingStar`, and the distance in light years from the home
   system's root, then a focus button and `DrawTargetControls`
   ([src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L209-L283)).[^cmp-cdmstarsystems]

## Lifecycle and timing

- **Click: `frame:draw-ui`.** `DrawTargetControls` runs inside the `[StarMapAfterGui]` window. At that
  point the next vehicle solver step is already running, so changing the target directly would race
  the solver (the flight-plan target nodes and the flight computer's navigation both read it). The
  command only queues.
- **Apply: `frame:input`.** `ApplyInputEvents` runs in the next frame's `Program.PrepareFrame`, after
  `ApplyVehicleSolvers` and before `ExecuteNextVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2167-L2220)), so
  the next step starts with the new target. The "Current Target" indicator appears one frame after the
  click.
- **Interaction with root targets.** The new native target passes through `Vehicle.SetTarget`, whose
  Compendium prefix clears any navigation-only root target on that vehicle.

## Key code

| Path | Role |
|---|---|
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L83-L144) | Target button, current-target indicator, no-vehicle note |
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L209-L283) | Star page that hosts the button |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L689) | Body page call site |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Universe.SetTargetCommand`, `Universe.UnsetTargetCommand` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L433-L586) | Validate ids and queue the change |
| `Universe.SetTarget`, `Universe.UnsetTarget` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L517-L591) | Called by the queued event |
| `InputEvents.ChangeTargetBuffer`, `ChangeTargetData` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L780-L801) | Deferred target change |
| `Vehicle.Target`, `Vehicle.SetTarget` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2189-L2203) | Current target and the actual change |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L515) | Which vehicle gets the target |
| `ImGuiDisabledScope` | [current/decomp/KSA/ImGuiDisabledScope.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiDisabledScope.cs#L6-L26) | Disable the button without a vehicle |

## Pitfalls and version notes

- **Failures only log.** The commands write an error to the game log and do nothing when an id does
  not resolve. For a non-`IOrbiter` target the error text is "is not a vehicle", which is misleading;
  that is why roots must take the other path.
- **Ids are names.** The commands look bodies up by `Id` in the current system; two bodies with the
  same id are not distinguishable through this API.
- **One frame of latency.** Anything that reads `vehicle.Target` in the same frame as the click still
  sees the old target.

## See also

- [Navigation-only root targets](root-navigation-targets.md)
- [Celestial compendium window](celestial-info-window.md)
- [Star systems and the body tree](star-systems-and-body-tree.md)
- [Mutate simulation state safely relative to the physics step](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
- [Set and clear the target with Universe.SetTargetCommand and UnsetTargetCommand](../../../howto/flight-and-orbits/autopilot/set-and-clear-the-vehicle-target/set-and-clear-the-vehicle-target-1.md) - how-to article demonstrating this feature

[^cmp-readme-md]: Compendium: README.md
[^cmp-cdmhelpers]: Compendium: src/CdmHelpers.cs
[^ksa-orbitingstar]: KSA.OrbitingStar (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-targettrackwindow]: KSA.TargetTrackWindow (decompiled, build 2026.10.10.5554)
[^ksa-iorbiter]: KSA.IOrbiter (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^cmp-cdmstarsystems]: Compendium: src/CdmStarSystems.cs
