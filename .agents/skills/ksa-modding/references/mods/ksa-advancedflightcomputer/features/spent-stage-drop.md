---
type: Mod Feature
title: Predict a jettison and drop spent boosters early
description: Predict which parts the next sequence would shed by mirroring Vehicle.Split on its decouplers, and stage early only when that sheds nothing but burnt-out engines and carries off no propellant a retained engine can still use
mod: ksa-advancedflightcomputer
category: vehicles-and-parts
techniques: [custom-physics]
hooks: [harmony-postfix KSA.Universe.ApplyVehicleSolvers]
lifecycle: [frame:apply-solvers]
game_types:
  - KSA.Vehicle
  - KSA.Decoupler
  - KSA.Part
  - KSA.Tank
  - KSA.ResourceManager
  - KSA.FuelLink
  - KSA.EngineController
tags: [staging, decoupler, boosters, crossfeed, fuel-link, part-tree]
---

Solid boosters often burn out while a liquid core keeps firing. A stager that waits for every engine
to be dry carries the empty casings as dead mass. AFC also stages when the next sequence would
jettison nothing but burnt-out hardware, which requires predicting what a decoupler will throw away
without firing it, and refusing when the dropped parts still feed the stage that stays.

## How it works

1. **Predict the jettison.** `JettisonAnalysis.GetPendingJettison` walks the next row's decouplers
   and mirrors `Vehicle.Split`: a decoupler that is enabled and still connected sheds the subtree on
   the side that is a tree child of the other; if neither or both are children, `Split`'s pick
   depends on connector order and AFC refuses to predict. The shape is cached per vehicle, keyed by
   sequence generation and part count, so a decouple from a part menu also invalidates it
   ([AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs#L7-L90),
   [AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs#L192-L218)). A row that lights an
   engine is not a pure jettison and returns null.
2. **Survey engines.** `StagingHelpers.SurveyActiveEngines(vehicle, jettison)` counts spent, fueled,
   inactive and broken engines inside the jettison set and whether anything outside is thrusting.
3. **Rules.** Stage only when: something spent is inside; no never-activated engine is inside; no
   fueled or broken engine is inside; an engine that stays is still firing; and the current burn is
   not complete. Each refusal has a named blocker that is logged once per change, because silence
   makes "rode along" and "correctly refused" look alike
   ([AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L311-L362)).
4. **Dwell.** The condition must hold for 0.25 s of simulation time.
5. **Last gate, crossfeed.** `CarriesOffUsablePropellant` checks, live, every tank in the jettison set
   that still has mass: if any of its `AvailableConsumers` is a `PlumbedCore` that stays aboard, or an
   enabled player-drawn `FuelLink` crosses the separation, the drop is refused
   ([AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs#L142-L186)).
6. **Fire** through the same split activation as a normal staging (delays apply).
7. **Guard.** `WouldSeparateLastControl` blocks any automatic row that would separate every control
   module.

The switch "Drop spent stages early" (`drop_spent_stages` in `autostage.toml`) turns the trigger off.

## Lifecycle and timing

Evaluated in the `Universe.ApplyVehicleSolvers` postfix (see [automatic staging](automatic-staging.md)),
on the main thread, with the step's results applied and the workers idle, so part trees and resource
states can be read safely. The expensive tank walk runs only after the dwell, as the last gate.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs) | Jettison prediction, control-loss guard, crossfeed check |
| [AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs) | `TickSpentJettison` rules and dwell |
| [AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs) | Engine survey inside and outside the set |
| [tests/AdvancedFlightComputer.HarnessTests/Features/AutoStage/SpentStageDropTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/AutoStage/SpentStageDropTest.cs) | Headless test of the drop and its refusals |

## Snippets

```csharp
// AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs L192-L218
    private static Separation GetJettisonedRoot(Decoupler decoupler, out Part? root)
    {
        root = null;

        // Mirrors the guard in Decoupler.SetIsActive.
        // IsActive never flips, so a spent decoupler is recognised by its connector having lost the connection.
        if (!decoupler.IsEnabled)
            return Separation.None;

        Part.Connection? connection = decoupler.Connector.Connection;
        if (connection == null)
            return Separation.None;

        // Raw parts, not FullPart: Vehicle.Split tests these same two.
        Part near = decoupler.Connector.ConnectionPart;
        Part far = connection.OtherPart(near);

        // Vehicle.Split keeps whichever endpoint is not a tree child of the other.
        // When neither or both are, its pick depends on connector order, so no side is predicted.
        bool nearIsParent = near.TreeChildren.Contains(far);
        bool farIsParent = far.TreeChildren.Contains(near);
        if (nearIsParent == farIsParent)
            return Separation.Unpredictable;

        root = nearIsParent ? far : near;
        return Separation.Predicted;
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.Split(Part.Connector, ...)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1516)) | Stock separation logic that the prediction mirrors |
| `Decoupler.SetIsActive`, `IsEnabled`, `Connector` ([current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L149)) | Decoupler guard and connection |
| `Part.TreeChildren`, `SubtreeModules`, `FullPart` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs)) | Subtree walk |
| `Tank.ComputeSubstanceMass`, `Tank.AvailableConsumers` ([current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs)) | Remaining propellant and who can draw it |
| `PartTree.FuelLinks` / `FuelLink.Enabled` ([current/decomp/KSA/FuelLink.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FuelLink.cs)) | Player-drawn crossfeed |

## Pitfalls and version notes

- `AvailableConsumers` is narrower than what a "furthest to nearest" flow rule drains; AFC notes this
  is fine for stock parts, whose decoupler joints carry no bulk-fluid capability, but modded parts
  may differ.
- A decoupler's `IsActive` never flips; a spent one is recognised by its lost connection.
- The prediction must refuse rather than guess when `Split`'s choice depends on connector order.

## See also

- [Automatic staging](automatic-staging.md)
- [Staging delays](staging-delays.md)
- [Drop a spent stage while the vehicle is still under thrust by predicting what Vehicle.Split would shed](../../../howto/vehicles-and-parts/staging-and-engines/stage-automatically/stage-automatically-2.md) - how-to article demonstrating this feature

