---
type: How-To
title: Release a part by firing only the decoupler on its own joint
description: Find the decoupler holding a launcher on, ask whether the next stage would fire it, and fire it with Decoupler.SetIsActive instead of staging the player's list
category: vehicles-and-parts
group: staging-and-engines
topic: stage-a-vehicle-from-code
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/launcher-separation-at-decoupler]
techniques: [input-buffer-activation, latch-on-request, staging-inspection]
hooks: [starmap StarMapAfterGui (via StepOnce)]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Decoupler, KSA.Part, KSA.PartTree, KSA.SequenceList, KSA.Sequence, KSA.InputEvents, KSA.Vehicle]
tags: [decoupler, separation, staging, input-buffer, latch]
---
Outcome: drop exactly one part (a launcher, a payload) from its stack, regardless of what the player has staged, and know
beforehand whether the player's own next stage would have done it. KSArmory uses it to separate a launcher from a spent
booster before it deploys rounds. Prerequisite: StarMap. Demonstrated by [ksarmory/launcher-separation-at-decoupler](../../../../mods/ksarmory/features/launcher-separation-at-decoupler.md).

## Background: how the game does it

`ActivateNextSequence` fires whatever the player queued next (see [overview](overview.md)); a single decoupler is fired with
`Decoupler.SetIsActive(vehicle, true)`, which only queues an `IActivateInputData{Decouple}`. `InputEvents.ApplyInputEvents`
applies it in `frame:input` of the next frame and `Decoupler.Decouple` splits the vehicle via `Vehicle.Split`
[current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L149-L172)[^ksa-decoupler] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114-L1140)[^ksa-inputevents]. `SetIsActive` ignores a decoupler that is disabled, already
active, or has no connection.

## Lifecycle and timing

Ask and fire from `frame:draw-ui` (KSArmory's StarMap step); the split happens at the start of the next frame, in
`frame:input`. The module does not de-duplicate requests, so a second request before the split queues a second decouple.
After the split the old `Part` references may belong to the new vehicle; see
`anchor-to-parts-across-vehicle-changes`. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Find the decoupler on the joint holding the part**: on the part itself, or on its tree parent, and only a decoupler whose
   connection leads to the launcher. A decoupler further up would drop an entire stage:

```csharp
// src/KSArmory/Ksa/LauncherSeparation.cs L75-99, L127-151
    public static bool TryFind(Part? launcher, out Decoupler found)
    {
        found = null!;
        if (launcher is null) return false;

        try
        {
            Part part = launcher.FullPart;

            // On the launcher itself: it declares a decoupler on its own mounting connector.
            if (TryOnPart(part, part, out found)) return true;

            // Or on whatever it is mounted to, provided the joint is the one holding the launcher.
            // A decoupler further up is somebody else's staging.
            Part? below = part.TreeParent;
            return below is not null && TryOnPart(below, part, out found);
        }
        catch
        {
            // A part tree mid-rebuild reads as "no decoupler", which is the answer that changes
            // nothing rather than the one that fires something.
            found = null!;
            return false;
        }
    }
// ...
    private static bool TryOnPart(Part carrier, Part launcher, out Decoupler found)
    {
        found = null!;

        foreach (Decoupler decoupler in carrier.Modules.Get<Decoupler>())
        {
            if (!decoupler.IsEnabled) continue;
            if (decoupler.Connector.Connection is not { } connection) continue;

            // The joint has to be the one the launcher hangs on. A decoupler on the same part
            // facing the other way is the stage below's business.
            Part far = connection.OtherPart(decoupler.Connector.ConnectionPart);
            Part near = decoupler.Connector.ConnectionPart;

            if (!ReferenceEquals(far.FullPart, launcher) && !ReferenceEquals(near.FullPart, launcher))
            {
                continue;
            }

            found = decoupler;
            return true;
        }

        return false;
    }
```

2. **Ask whether the player's next stage separates it.** Refresh derived sequences first (the part tree rebuilds them once a
   frame, before the solvers), then look only at the first sequence that is not activated and has parts:

```csharp
// src/KSArmory/Ksa/LauncherSeparation.cs L32-66
    public static bool NextStageSeparates(Vehicle? craft, Part? launcher)
    {
        if (craft is null || !TryFind(launcher, out Decoupler decoupler)) return false;

        try
        {
            // Derived data is rebuilt once a frame, before the solvers run, so a tree split after
            // that holds the old list until the next frame unless asked for it.
            craft.Parts.EnsureDerived(DerivedData.Sequences);
            ReadOnlySpan<Sequence> sequences = craft.Parts.SequenceList.Sequences;

            for (int i = 0; i < sequences.Length; i++)
            {
                Sequence sequence = sequences[i];
                if (sequence.Activated || sequence.Parts.IsEmpty) continue;

                // The first unactivated sequence with parts is the next to go, and the only one
                // this question is about.
                ReadOnlySpan<Part> parts = sequence.Parts;

                for (int p = 0; p < parts.Length; p++)
                {
                    if (ReferenceEquals(parts[p], decoupler.Parent)) return true;
                }

                return false;
            }
        }
        catch (Exception e)
        {
            Log.Error("could not read the staging list", e);
        }

        return false;
    }
```

3. **Fire the module, never the sequence:**

```csharp
// src/KSArmory/Ksa/LauncherSeparation.cs L111-125
    public static bool Separate(Vehicle? craft, Part? launcher)
    {
        if (craft is null || !KsaWorld.IsAlive(craft)) return false;
        if (!TryFind(launcher, out Decoupler decoupler) || !decoupler.IsEnabled) return false;

        try
        {
            decoupler.SetIsActive(craft, true);
            return true;
        }
        catch
        {
            return false;
        }
    }
```

4. **Latch before the call.** The split lands a frame later and the module does not de-duplicate, so the caller sets its flag
   first and records what it will need to detect the split:

```csharp
// src/KSArmory/Ksa/IcbmComputer.cs L1435-1459
    private void SeparateOnce(IManualFire? weapon)
    {
        if (_separated || weapon is null) return;

        if (!weapon.CanSeparate)
        {
            _separated = true;
            return;
        }

        // Latched before the call, not after: the split lands a frame later and the module does not
        // de-duplicate, so a second request queues a second decouple.
        _separated = true;

        if (weapon.Separate())
        {
            _awaitingSplit = true;
            _didSplit = true;
            _wasBeforeSplit.Clear();
            KsaWorld.CollectVehicles(_wasBeforeSplit);

            Log.Info($"ICBM computer on {KsaWorld.DisplayName(Craft)} separating the launcher "
                     + "from the stack before deploying");
        }
    }
```

## Complete example

- [src/KSArmory/Ksa/LauncherSeparation.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherSeparation.cs)[^ksarmory-launcherseparation]
- [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L1425-L1459)[^ksarmory-icbmcomputer]

## Variations and gotchas

- **Failure reads as "do nothing".** `TryFind` returns false mid-rebuild and `NextStageSeparates` returns false when the list is
  unreadable, deliberately the answer that lets the rocket fly.
- **Stale derived data.** Without `EnsureDerived(DerivedData.Sequences)` a tree split after the frame's rebuild holds the old
  sequence list.
- **Disabled decouplers** (players can disable the module) return false from `Separate`.
- **No decoupler on the part** means the weapon simply deploys attached.

## See also

- [Overview](overview.md), [Article 1](stage-a-vehicle-from-code-1.md), [Article 2](stage-a-vehicle-from-code-2.md)
- [ksarmory/launcher-separation-at-decoupler](../../../../mods/ksarmory/features/launcher-separation-at-decoupler.md)
- `stage-automatically`

[^ksa-decoupler]: current/decomp/KSA/Decoupler.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-launcherseparation]: KSArmory: src/KSArmory/Ksa/LauncherSeparation.cs
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-icbmcomputer]: KSArmory: src/KSArmory/Ksa/IcbmComputer.cs
