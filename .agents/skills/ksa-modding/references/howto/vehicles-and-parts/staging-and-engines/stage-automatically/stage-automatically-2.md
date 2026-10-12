---
type: How-To
title: Drop a spent stage while the vehicle is still under thrust by predicting what Vehicle.Split would shed
description: Predict the parts the next sequence would jettison, require them to be spent, wait out a dwell, refuse when they still feed the vehicle, and only then stage
category: vehicles-and-parts
group: staging-and-engines
topic: stage-automatically
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/spent-stage-drop]
techniques: [jettison-prediction, dwell-gate]
hooks: [harmony-postfix KSA.Universe.ApplyVehicleSolvers]
lifecycle: [frame:apply-solvers]
game_types:
  - KSA.Decoupler
  - KSA.Part
  - KSA.PartTree
  - KSA.SequenceList
  - KSA.Tank
  - KSA.PlumbedCore
  - KSA.FuelLink
  - KSA.Control
tags: [staging, jettison, spent-stage, crossfeed, control-guard]
---
Outcome: when the next row would shed nothing but burnt-out engines and something else is still thrusting, the vehicle
stages without waiting for everything to run dry. A set of gates keeps it from throwing away fuel or the cockpit. Builds on
[article 1](stage-automatically-1.md), whose state machine calls this from its `Monitoring` phase. Prerequisites: StarMap,
Harmony. Demonstrated by [ksa-advancedflightcomputer/spent-stage-drop](../../../../mods/ksa-advancedflightcomputer/features/spent-stage-drop.md).

## Background: how the game does it

A decoupler's activation ends in `Vehicle.Split`, which keeps whichever endpoint of the connection is not a tree child of the
other and sheds the subtree below the child side. Nothing in the game exposes "what will the next row shed", so AFC mirrors
the split rule on the part graph; the game then does the real work through the normal activation ([Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md)).
See the [overview](overview.md).

## Lifecycle and timing

Evaluated every frame in `frame:apply-solvers` (postfix on `Universe.ApplyVehicleSolvers`), with the workers joined. The
condition is level-triggered, so it needs a dwell measured in sim time, because evaluation also runs while paused. The
jettison set is cached per vehicle and rebuilt only when the sequence generation, the part count or the next sequence number
changes. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Predict the jettison set.** Look at the next sequence: decline if it lights an engine or separates nothing predictable;
   for each decoupler work out which side leaves (`Predicted`, `None` or `Unpredictable` when the endpoints are neither or both
   tree parents) and collect that subtree:

```csharp
// AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs L38-90
    private static bool Rebuild(Vehicle vehicle, HashSet<Part> jettisonSet)
    {
        jettisonSet.Clear();

        SequenceList seqList = vehicle.Parts.SequenceList;
        int number = seqList.GetNextSequenceNumber();
        if (number < 0)
            return Decline(vehicle, "nothing left to stage");

        Sequence? target = FindSequence(seqList, number);
        if (target == null)
            return Decline(vehicle, $"sequence {number} is not in the list");

        bool anyDecoupler = false;
        ReadOnlySpan<Part> parts = target.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            foreach (ISequenced module in parts[i].InSequence(number))
            {
                if (module is EngineController)
                    return Decline(vehicle, $"sequence {number} lights an engine");

                if (module is not Decoupler decoupler)
                    continue;

                switch (GetJettisonedRoot(decoupler, out Part? root))
                {
                    case Separation.Predicted:
                        anyDecoupler = true;
                        AddSubtree(root!, jettisonSet);
                        break;
                    case Separation.None:
                        break;
                    case Separation.Unpredictable:
                        return Decline(vehicle, $"sequence {number} has a decoupler whose separation cannot be predicted");
                }
            }
        }

        if (!anyDecoupler)
            return Decline(vehicle, $"sequence {number} separates nothing");

        if (DebugConfig.AutoStage)
        {
            int engines = 0;
            foreach (Part part in jettisonSet)
                engines += part.SubtreeModules.Get<EngineController>().Length;
            DefaultCategory.Log.Debug(
                $"[AFC] Spent-stage jettison armed on '{vehicle.Id}': sequence {number} would shed {jettisonSet.Count} part(s) carrying {engines} engine(s).");
        }

        return true;
    }
```
```csharp
// AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs L190-218
    private enum Separation { Predicted, None, Unpredictable }

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

2. **Cache it** under a key of (sequence generation, part count, next sequence number) and write the key before rebuilding so a
   throw cannot repeat every frame:

```csharp
// AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs L16-36
    public static IReadOnlySet<Part>? GetPendingJettison(Vehicle vehicle, StagingState state)
    {
        int generation = StagingHelpers.SequenceGeneration;
        // The part count catches tree changes the sequence generation misses, such as a decouple from a part menu.
        int partCount = vehicle.Parts.Count;
        int sequenceNumber = vehicle.Parts.SequenceList.GetNextSequenceNumber();

        if (state.JettisonGeneration != generation
            || state.JettisonPartCount != partCount
            || state.JettisonSequenceNumber != sequenceNumber)
        {
            // The key is written before the rebuild, so a throw mid-rebuild leaves the set unusable until the inputs change instead of throwing again every frame.
            state.JettisonGeneration = generation;
            state.JettisonPartCount = partCount;
            state.JettisonSequenceNumber = sequenceNumber;
            state.JettisonValid = false;
            state.JettisonValid = Rebuild(vehicle, state.JettisonSet);
        }

        return state.JettisonValid ? state.JettisonSet : null;
    }
```

3. **Survey the engines** and compute the blocker, one reason per refusal (nothing spent, an engine not yet run, one still
   fueled, an unresolved motor stack, nothing thrusting outside the set, burn complete). Only a clean survey starts the dwell:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs L313-340
    private static void TickSpentJettison(Vehicle vehicle, StagingState state, FlightComputer fc, double now,
        in StagingHelpers.EngineSurvey survey, IReadOnlySet<Part>? jettison)
    {
        StagingState.SpentDropBlocker blocker =
            jettison == null ? StagingState.SpentDropBlocker.NoJettison
            : survey.SpentInside == 0 ? StagingState.SpentDropBlocker.NothingSpent
            : survey.InactiveInside > 0 ? StagingState.SpentDropBlocker.InactiveInside
            : survey.FueledInside > 0 ? StagingState.SpentDropBlocker.FueledInside
            : survey.BrokenInside > 0 ? StagingState.SpentDropBlocker.BrokenInside
            : !survey.ThrustingOutside ? StagingState.SpentDropBlocker.NothingThrusting
            : IsBurnComplete(fc) ? StagingState.SpentDropBlocker.BurnComplete
            : StagingState.SpentDropBlocker.None;

        if (blocker != StagingState.SpentDropBlocker.None)
        {
            ReportSpentJettison(vehicle, state, blocker, in survey, measured: jettison != null);
            state.SpentJettisonSince = double.NaN;
            return;
        }

        if (double.IsNaN(state.SpentJettisonSince))
        {
            ReportSpentJettison(vehicle, state, StagingState.SpentDropBlocker.None, in survey, measured: true);
            state.SpentJettisonSince = now;
            return;
        }
        if (now - state.SpentJettisonSince < SpentJettisonDwellSeconds)
            return;
```

4. **Dwell, then the crossfeed gate.** After 0.25 s of sim time with no blocker, run the live check that no tank in the shed
   parts still feeds a consumer that stays aboard, and no enabled fuel link crosses the separation:

```csharp
// AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs L144-188
    public static bool CarriesOffUsablePropellant(Vehicle vehicle, IReadOnlySet<Part> jettison, out string? reason)
    {
        reason = null;
        ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;

        foreach (Part part in jettison)
        {
            // SubtreeModules, because a tank usually sits on a sub-part of the tree part.
            Span<Tank> tanks = part.SubtreeModules.Get<Tank>();
            for (int i = 0; i < tanks.Length; i++)
            {
                Tank tank = tanks[i];
                if (tank.ComputeSubstanceMass(moleStates) <= 0f)
                    continue;

                // AvailableConsumers is narrower than what a FurtherestToNearest flow rule drains, which is fine on stock parts, whose decoupler joints carry no BulkFluid capability.
                foreach ((ResourceManager manager, int _) in tank.AvailableConsumers)
                {
                    if (manager.Consumer is not PlumbedCore consumer)
                        continue;
                    if (jettison.Contains(consumer.Parent.FullPart))
                        continue;

                    reason = $"a tank on '{part.DisplayName}' still feeds '{consumer.Parent.FullPart.DisplayName}', which stays aboard";
                    return true;
                }
            }
        }

        // A player-drawn fuel link is plumbing the resource graph does not necessarily model.
        ReadOnlySpan<FuelLink> links = vehicle.Parts.FuelLinks.Links;
        for (int i = 0; i < links.Length; i++)
        {
            FuelLink link = links[i];
            if (!link.Enabled)
                continue;
            if (jettison.Contains(link.PartA.FullPart) == jettison.Contains(link.PartB.FullPart))
                continue;

            reason = $"an enabled fuel link joins '{link.PartA.FullPart.DisplayName}' and '{link.PartB.FullPart.DisplayName}' across the separation";
            return true;
        }

        return false;
    }
```

5. **Stage**, with the control guard in `ExecuteStaging` refusing a row that would separate every control module (it holds,
   alerts once, and leaves the row for the player):

```csharp
// AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs L94-123
    public static bool WouldSeparateLastControl(Vehicle vehicle)
    {
        SequenceList seqList = vehicle.Parts.SequenceList;
        Sequence? next = FindSequence(seqList, seqList.GetNextSequenceNumber());
        if (next == null)
            return false;

        _guardSet.Clear();
        ReadOnlySpan<Part> parts = next.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            foreach (ISequenced module in parts[i].InSequence(next.Number))
            {
                if (module is Decoupler decoupler && GetJettisonedRoot(decoupler, out Part? root) == Separation.Predicted)
                    AddSubtree(root!, _guardSet);
            }
        }

        bool separatesLastControl = false;
        if (_guardSet.Count > 0)
        {
            Span<Control> controls = vehicle.Parts.Modules.Get<Control>();
            separatesLastControl = controls.Length > 0;
            for (int i = 0; i < controls.Length && separatesLastControl; i++)
                separatesLastControl = _guardSet.Contains(controls[i].Parent.FullPart);
        }
        // Cleared on the way out, so the scratch set does not pin a shed subtree.
        _guardSet.Clear();
        return separatesLastControl;
    }
```

6. **Switch.** The behaviour is the `drop_spent_stages` flag in `[staging]` of `autostage.toml`, default true:

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs L12-18
    public const bool DropSpentStagesDefault = true;

    private static string _modDir = string.Empty;
    private static string _vehiclesDir = string.Empty;
    private static string _configPath = string.Empty;

    public static bool DropSpentStages { get; set; } = DropSpentStagesDefault;
```

## Complete example

- [AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs)[^ksa-advancedflightcomputer-jettisonanalysis]
- [AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs#L311-L364)[^ksa-advancedflightcomputer-stagingdetector]
- [AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs#L40-L84)[^ksa-advancedflightcomputer-staginghelpers]

## Variations and gotchas

- **Unpredictable splits.** When both or neither endpoint is a tree child the picked side depends on connector order, so the
  analysis declines rather than guessing.
- **Spent decouplers.** `IsActive` never flips on a decoupler, so a spent one is recognised by its connector having lost the
  connection.
- **Crossfeed is not fully modelled.** The tank check uses `AvailableConsumers`, which the code comments call narrower than what a
  `FurtherestToNearest` flow rule drains; fine for stock parts, whose decoupler joints carry no bulk-fluid capability.
- **Player-drawn fuel links** are checked separately because the resource graph does not necessarily model them.
- **Part-count key.** The part count catches tree changes the sequence generation misses, such as a decouple from a part menu.

## See also

- [Overview](overview.md), [Article 1](stage-automatically-1.md), [Article 3](stage-automatically-3.md)
- [ksa-advancedflightcomputer/spent-stage-drop](../../../../mods/ksa-advancedflightcomputer/features/spent-stage-drop.md)
- [Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md)

[^ksa-advancedflightcomputer-stagingdetector]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingDetector.cs
[^ksa-advancedflightcomputer-jettisonanalysis]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/JettisonAnalysis.cs
[^ksa-advancedflightcomputer-staginghelpers]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/AutoStage/StagingHelpers.cs
