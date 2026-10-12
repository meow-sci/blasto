---
type: How-To
title: Adopt a separated booster as its own guided vehicle
description: Record the detached root parts before a staging request fires, then match the new vehicle that appears after the split against those ids and give it guidance state, retrying the engage until the part tree has settled
category: flight-and-orbits
group: autopilot
topic: run-an-autopilot-for-every-vehicle-each-step
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/boostback-impact-prediction]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.SequenceList, KSA.Decoupler, KSA.Part, KSA.PartTree]
tags: [booster, separation, hand-over, part-ids, stage, boostback, adoption]
---

Outcome: when a rocket stages, the separated first stage becomes its own `Vehicle` and the autopilot keeps flying it (here: a boostback toward the launch site) while the player rides the upper stage.
KSA-AdvancedFlightComputer records what the separation will throw overboard and adopts the new vehicle.[Booster boostback with a drag-integrated impact predictor](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

Separating creates a new `Vehicle`; after the split the detached parts belong to a vehicle the mod has no handle on, but part `InstanceId`s survive. Which part separates when a `Decoupler` fires is the tree-child side of its connection (the same rule `Vehicle.Split` applies). `Vehicle.Parts` is the `PartTree` to search, and the
per-vehicle step (see [approach 1](run-an-autopilot-for-every-vehicle-each-step-1.md)) is called for the new vehicle too, because `PrepareWorker` runs for every vehicle.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1594-L1668) [^ksa-sequencelist]

## Lifecycle and timing

Everything runs in the `Vehicle.PrepareWorker` prefix for the booster vehicle (`frame:dispatch-solvers`). The recording happens before a staging request fires (from the guidance step of the parent vehicle); the match happens in the first step of the new, unfocused, unknown vehicle. The mod deliberately does **not** engage the boostback on that frame: it is the frame the split happened,
the part tree is least settled and the engage needs an aero surrogate fitted to a bounding box that may not exist yet; a refused engage would leave an adopted booster that is never flown with the hand-over record already consumed. So the engage is retried from the step until it takes. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Record before staging.** Find the next un-activated sequence, walk its parts' `Decoupler` modules and store the detached root part ids with an expiry; match roots rather than subtrees, because nested separations overlap but have distinct roots:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L262-L300
/// </summary>
private static void ArmBoosterHandover(Vehicle vehicle, SequenceList sequenceList,
                                       double now)
{
    DropExpiredHandovers(now);
    _s.ReserveStaged = true;

    Sequence next = null;
    ReadOnlySpan<Sequence> sequences = sequenceList.Sequences;
    for (int i = 0; i < sequences.Length; i++)
    {
        if (!sequences[i].Activated && !sequences[i].Parts.IsEmpty)
        {
            next = sequences[i];
            break;
        }
    }
    if (next == null)
        return;

    int firstArmed = _handovers.Count;
    Part detached = null;
    ReadOnlySpan<Part> parts = next.Parts;
    for (int i = 0; i < parts.Length; i++)
    {
        Span<Decoupler> decouplers = parts[i].SubtreeModules.Get<Decoupler>();
        for (int j = 0; j < decouplers.Length; j++)
        {
            Part root = DetachedRoot(decouplers[j]);
            if (root == null || AlreadyArmed(root.InstanceId, firstArmed))
                continue;
            detached ??= root;

            // Nested separations have overlapping subtrees but distinct roots. Matching the root keeps the inner vehicle from claiming the outer vehicle's record.
            _handovers.Add(new BoosterHandover
            {
                RootId = root.InstanceId,
                ExpiresAt = now + HandoverWindowS,
            });
```

2. **Define the detached root** with the same rule the game uses:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L420-L431
private static Part DetachedRoot(Decoupler decoupler)
{
    Part.Connection conn = decoupler.Connector?.Connection;
    if (conn == null)
        return null;
    Part a = conn.Connectors[0].ConnectionPart;
    Part b = conn.Connectors[1].ConnectionPart;
    if (a == null || b == null)
        return null;
    return !a.TreeChildren.Contains(b) ? a : b;
}

```

3. **Match in the step of an unknown, unfocused vehicle.** Search the new vehicle's part tree for a recorded root id, consume only that record (other boosters can still claim theirs), create state with the stored landing site and mark the hand-over pending:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L339-L383
private static bool TryAdoptBooster(Vehicle vehicle)
{
    double now = SimNow();
    DropExpiredHandovers(now);
    if (_handovers.Count == 0)
        return false;

    PartTree tree = vehicle?.Parts;
    if (tree == null)
        return false;

    BoosterHandover mine = null;
    ReadOnlySpan<Part> parts = tree.Parts;
    for (int i = 0; i < _handovers.Count && mine == null; i++)
    {
        for (int j = 0; j < parts.Length; j++)
        {
            if (parts[j].InstanceId != _handovers[i].RootId)
                continue;
            mine = _handovers[i];
            break;
        }
    }
    if (mine == null)
        return false;

    // Consume only this match so other boosters can still claim their records.
    _handovers.Remove(mine);

    _s = VehicleAutopilotState.For(vehicle);
    _s.SiteLatDeg = mine.SiteLatDeg;
    _s.SiteLonDeg = mine.SiteLonDeg;
    GuidanceLog.Info(vehicle, $"adopted as a separated booster, boostback engages within {HandoverWindowS:F0} s.");

    // NOT engaged here, deliberately. This runs on the frame the split happened, which is the frame the part tree is least settled - and ExecuteBoostback needs an aero surrogate fitted to a bounding box that may not exist yet. It refuses rather than throwing, and refusing here would leave a booster that had been adopted and would never be flown, with the hand-over record already consumed. So the engage is retried from the sweep until it takes.
    _s.HandoverPendingUntil = now + HandoverWindowS;
    return true;
}

/// <summary>
/// Engage boostback on an adopted booster, retrying until the part tree has settled
/// enough for the aero sweep to fit a surrogate to it. Gives up at the window rather
/// than retrying a fault forever.
/// </summary>
private static void StepBoosterHandover(Vehicle vehicle)
```

4. **Retry the engage until the window ends.** Skip until the next attempt time; call the engage; on refusal wait 0.5 s; give up with a status message at the window end rather than retrying a fault forever.
5. **Try adoption last** in the state lookup order, after the focused vehicle and vehicles that already have state, so a hot path stays cheap (`else if (!TryAdoptBooster(vehicle)) return;`).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L255-L420) | `ArmBoosterHandover`, `TryAdoptBooster`, `StepBoosterHandover` |
| [AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ReturnableStages.cs) | which stages can return (a control module in the separating subtree) |
| [AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Numerics/Flight/ImpactPredictor.cs#L1-L60) | unpowered impact prediction used by the boostback |

## Variations and gotchas

- **Records expire** after a generous window so a staging that never separates does not adopt a stranger.
- **Return only stages that can return**: the separating subtree must contain a control module (the test stock uses for `Vehicle.IsControllable`).
- **Several boosters at once** (nested separations) work because each record is matched by its own root id.
- The impact predictor integrates unpowered flight in a body-centred inertial frame with the co-rotating atmosphere (`v_air = v - omega x r`), as `PhysicsStates.ComputeAirVelocityBody` does.

## See also

- [Overview](overview.md); [step loop](run-an-autopilot-for-every-vehicle-each-step-1.md)
- [Booster boostback with a drag-integrated impact predictor](../../../../mods/ksa-advancedflightcomputer/features/boostback-impact-prediction.md)
- Related topics: [Fly a boostback burn and glide a booster to its landing site](../../guidance/fly-a-boostback-and-glide-to-a-landing-site/overview.md), [Predict impact points with drag and build a ballistic computer](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: KSA.SequenceList (decompiled, build 2026.10.10.5554)
