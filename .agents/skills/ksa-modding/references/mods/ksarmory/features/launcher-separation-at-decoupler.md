---
type: Mod Feature
title: Separate a launcher from its stack by firing the decoupler on its own joint
description: Find the Decoupler module on the one connection holding a part on, ask whether the player's next stage would fire it, and fire it directly with Decoupler.SetIsActive so the split goes through KSA's input buffer on the next frame
mod: ksarmory
category: vehicles-and-parts
techniques: [part-module-lookup, staging-inspection, input-buffer-activation, latch-on-request]
hooks: [starmap StarMapAfterGui (via StepOnce)]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Decoupler, KSA.Part, KSA.PartTree, KSA.SequenceList, KSA.Sequence, KSA.InputEvents, KSA.Vehicle]
tags: [decoupler, staging, separation, part-tree, input-buffer]
---

A post-boost bus that steers between releasing warheads wants to drop the spent booster first: the
booster is both the mass it must turn and the lever arm that throws each round. KSArmory's
ballistic computer does this only where the craft's own part tree offers a joint to let go of,
and it treats "can this launcher separate" as a property of the **part** and its joint, never of
the craft or its staging list.[^launcher-separation] The technique is reusable for any mod that
needs to release one specific part without firing whatever the player staged next.

## How it works

| Question | Code | Rule |
|---|---|---|
| Which decoupler holds this part on? | `LauncherSeparation.TryFind(launcher)` | Look at `Decoupler` modules on the launcher's `FullPart` itself, then on its `TreeParent` only. A module counts if it is enabled, its `Connector.Connection` exists, and one side of that connection (`connection.OtherPart(...)` or the connector's own part) is the launcher. Walking further up would find the interstage and drop the whole upper stage with the rounds aboard[^launcher-separation][^ksa-part][^ksa-decoupler] |
| Could it separate at all? | `CanSeparate` | A decoupler was found and `IsEnabled`; knowable before the shot[^launcher-separation] |
| Would the player's next stage separate it? | `NextStageSeparates(craft, launcher)` | `craft.Parts.EnsureDerived(DerivedData.Sequences)` (derived data is rebuilt once a frame, so a split this frame would otherwise read the old list), then find the first `Sequence` in `SequenceList.Sequences` that is not `Activated` and has parts; true only if that sequence contains the decoupler's parent part[^launcher-separation][^ksa-parttree][^ksa-sequencelist][^ksa-sequence] |
| Fire it | `Separate(craft, launcher)` -> `decoupler.SetIsActive(craft, true)` | The module directly, never `ActivateNextSequence`, which fires whatever the player queued next[^launcher-separation] |

`Decoupler.SetIsActive` does not split anything itself: when the decoupler is enabled, not yet
active and its connection has another side, it adds an `IActivateInputData { Operation = Decouple }`
to `InputEvents.IActivateInputBuffer`. The split (`Decoupler.Decouple` -> `Vehicle.Split`, with the
engine's decouple sound and particles) happens when that buffer is applied.[^ksa-decoupler][^ksa-inputevents]

### How the ballistic computer uses it

- **Staging guard**: the computer refuses to stage when `NextStageSeparatesIt` is true (that stage
  would drop the launcher), but not merely because a decoupler exists, which would strand a
  multi-stage stack with a dead first stage.[^icbm-computer][^roles]
- **Separate once**: `SeparateOnce` latches `_separated` *before* calling `Separate()`, because the
  module does not de-duplicate and a second request would queue a second decouple; it then records
  the set of vehicles so the new craft created by the split can be identified next frame.[^icbm-computer]
- Nothing KSArmory ships declares a decoupler under its launcher, so this activates only on craft
  the player builds that way.[^icbm-computer]

## Lifecycle and timing

- **Request: `frame:draw-ui`.** The computer runs inside `StepSimulation` from the
  `[StarMapAfterGui]` hook (via `StepOnce`) and calls `Separate()` there.[^icbm-computer][^weapon-system]
- **Split: `frame:input` of the next frame.** `Program.PrepareFrame` calls
  `InputEvents.ApplyInputEvents()` (Program.cs L2181, after `Universe.ApplyVehicleSolvers` at L2167),
  which runs `ApplyIActivateBatched` and so `Decoupler.Decouple`. This is the engine's own
  main-thread window for structural changes, after the last worker results landed and before the
  next worker is dispatched, which is why a mod should go through the buffer rather than call
  `Vehicle.Split` from a hook.[^ksa-program][^ksa-inputevents]
- Reading the staging list from a hook needs `EnsureDerived` because derived data is rebuilt once a
  frame before the solvers run.[^launcher-separation]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/LauncherSeparation.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherSeparation.cs#L31-L151) | Find, ask, fire |
| [src/KSArmory/Ksa/IcbmComputer.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/IcbmComputer.cs#L1426-L1460) | Staging guard and separate-once latch |
| [src/KSArmory/Ksa/WeaponSystemRoles.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystemRoles.cs#L300-L320) | `CanSeparate` vs `NextStageSeparatesIt` contract |

## Snippets

```csharp
// src/KSArmory/Ksa/LauncherSeparation.cs L127-L151
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

```csharp
// current/decomp/KSA/Decoupler.cs L149-L161
	public void SetIsActive(Vehicle? vehicle, bool activationState)
	{
		if (IsEnabled && !IsActive && activationState && vehicle != null && Connector.Connection?.OtherPart(base.Parent.FullPart) != null)
		{
			InputEvents.IActivateInputData data = new InputEvents.IActivateInputData
			{
				Operation = InputEvents.ActivateOp.Decouple,
				Vehicle = vehicle,
				Decoupler = this
			};
			InputEvents.IActivateInputBuffer.Add(data);
		}
	}
```

## Game APIs touched

| Member | Use |
|---|---|
| `Decoupler.Connector`, `IsEnabled`, `SetIsActive(Vehicle?, bool)`, `Decouple(Vehicle)` ([current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L149)) | The joint and its activation |
| `Part.Modules.Get<T>()`, `FullPart`, `TreeParent` ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L666)), `Part.Connection.OtherPart(Part)` (L501) | Walk to the holding joint |
| `PartTree.EnsureDerived(DerivedData)` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L521)) | Fresh staging data from a hook |
| `SequenceList.Sequences` ([current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L127)), `Sequence.Activated`, `Sequence.Parts` ([current/decomp/KSA/Sequence.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Sequence.cs#L13)) | Next stage to fire |
| `InputEvents.IActivateInputBuffer`, `ApplyInputEvents` ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1114)) | Deferred, batched activation |

## Pitfalls and version notes

- **Only the holding joint.** A decoupler further up the tree belongs to someone else's staging;
  firing it drops more than the launcher.[^launcher-separation]
- **Latch before requesting.** The request lands next frame and is not de-duplicated, so a second
  call queues a second decouple.[^icbm-computer]
- **A tree mid-rebuild reads as "no decoupler"**, the answer that fires nothing.[^launcher-separation]
- **Do not call `Vehicle.Split` from a hook**; splitting between the worker dispatch and the next
  apply mixes results computed for the whole craft into its fragments (see the destruction feature).[^ksa-inputevents]

## See also

- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [MIRV bus trim and release](./mirv-bus-trim-and-release.md)
- [Fly another vehicle](./fly-another-vehicle.md)
- [Safe vehicle destruction and part failure](./safe-vehicle-destruction-and-part-failure.md)
- [Rounds as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md)
- [Release a part by firing only the decoupler on its own joint](../../../howto/vehicles-and-parts/staging-and-engines/stage-a-vehicle-from-code/stage-a-vehicle-from-code-3.md) - how-to article demonstrating this feature

[^launcher-separation]: src/KSArmory/Ksa/LauncherSeparation.cs
[^icbm-computer]: src/KSArmory/Ksa/IcbmComputer.cs
[^roles]: src/KSArmory/Ksa/WeaponSystemRoles.cs
[^weapon-system]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksa-decoupler]: KSA.Decoupler (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: KSA.SequenceList (decompiled, build 2026.10.10.5554)
[^ksa-sequence]: KSA.Sequence (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
