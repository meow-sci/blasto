---
type: How-To
title: Patch the non-inlined entry points that reach the inlined method too
description: Cover the flight-computer methods SetNullRot, RateHold and TrackTarget, which are AggressiveInlining, by also patching the non-inlined Vehicle.SetEnum, ToggleEnum, SetStabilization and KittenEva.HandleKittenAction that reach them, and by listing the paths that stay exposed
category: patching
group: internals
topic: patch-around-aggressive-inlining
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ohmystars/features/attitude-mode-handoff-patches]
techniques: [harmony-prefix, entry-point-coverage]
hooks:
  - harmony-prefix KSA.FlightComputer.SetNullRot
  - harmony-prefix KSA.FlightComputer.RateHold
  - harmony-prefix KSA.FlightComputer.TrackTarget
  - harmony-prefix KSA.Vehicle.SetEnum
  - harmony-prefix KSA.Vehicle.ToggleEnum
  - harmony-prefix KSA.Vehicle.SetStabilization
lifecycle: [startup:all-mods-loaded, frame:input, physics:vehicle-solver]
game_types: [KSA.FlightComputer, KSA.Vehicle, KSA.KittenEva]
tags: [harmony, prefix, inlining, aggressiveinlining, flight-computer, entry-points]
---

Oh My Stars has to notice the moment the player leaves its custom star-orientation attitude target, or it would keep overriding the player. The obvious hooks are the flight computer's `TrackTarget`, `RateHold` and
`SetNullRot`, but all three are `AggressiveInlining`. The mod patches them and also the non-inlined public methods that normally lead to them, so the usual paths are covered even if a caller inlined the body.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by Oh My Stars in [Attitude-mode hand-off patches](../../../../mods/ohmystars/features/attitude-mode-handoff-patches.md).

## Background: how the game does it

The three flight-computer methods are tiny setters, each marked for inlining; `RateHold` calls `SetNullRot`:

```csharp
// current/decomp/KSA/FlightComputer.cs L331-L337
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void SetNullRot(VehicleReferenceFrame attitudeFrame)
{
	AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.None;
	AttitudeFrame = attitudeFrame;
	AttitudeTarget = AttitudeTarget.Zero;
}
```

Source: [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L331-L337)[^ksa-flightcomputer]

```csharp
// current/decomp/KSA/FlightComputer.cs L1978-L1998
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void RateHold(VehicleReferenceFrame frame)
{
	AttitudeMode = FlightComputerAttitudeMode.Auto;
	SetNullRot(frame);
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public bool IsTrackingTarget(FlightComputerAttitudeTrackTarget target)
{
	bool flag = AttitudeMode == FlightComputerAttitudeMode.Auto;
	bool flag2 = AttitudeTrackTarget == target;
	return flag && flag2;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void TrackTarget(FlightComputerAttitudeTrackTarget target)
{
	AttitudeMode = FlightComputerAttitudeMode.Auto;
	AttitudeTrackTarget = target;
}
```

Source: [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1978-L1998)

The player-facing paths reach them from non-inlined methods. `Vehicle.SetEnum` handles the navball and mode buttons and calls `TrackTarget` or `RateHold`; `SetStabilization` calls `RateHold` or assigns `AttitudeMode`
directly; `KittenEva.HandleKittenAction` writes `AttitudeMode` for `ManualTrim`:

```csharp
// current/decomp/KSA/Vehicle.cs L3618-L3628
public void SetStabilization(bool active)
{
	if (active)
	{
		FlightComputer.RateHold(NavBallData.Frame);
	}
	else
	{
		FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Manual;
	}
}
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3618-L3628)[^ksa-vehicle]

```csharp
// current/decomp/KSA/KittenEva.cs L218-L238
protected override void HandleKittenAction(KittenEvaAction action)
{
	switch (action)
	{
	case KittenEvaAction.ViewMode:
		SetControlMode(KittenControlMode.View);
		break;
	case KittenEvaAction.DirectMode:
		SetControlMode(KittenControlMode.Direct);
		break;
	case KittenEvaAction.Rcs:
		base.FlightComputer.ToggleRCSMode();
		break;
	case KittenEvaAction.ManualTrim:
		base.FlightComputer.AttitudeMode = FlightComputerAttitudeMode.Manual;
		break;
	case KittenEvaAction.RateTrim:
		base.FlightComputer.RateHold(VehicleReferenceFrame.EclBody);
		break;
	}
}
```

Source: [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L218-L238)[^ksa-kitteneva]

## Lifecycle and timing

Install at `startup:all-mods-loaded`. The `SetEnum`, `ToggleEnum`, `SetStabilization` and kitten-action prefixes run on the main thread in `frame:input` (`InputEvents.ApplyInputEvents` runs queued key and navball events in
`PrepareFrame`); the flight-computer prefixes also run on vehicle workers in `physics:vehicle-solver`, when the worker's copy of the computer calls them. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Hook the frame with StarMap lifecycle attributes](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Apply the setter prefixes anyway.** A prefix on an inlined method works when the caller was not compiled with the body inlined ([Changing behaviour of a game method marked AggressiveInlining](overview.md)); it costs nothing to have:

```csharp
// src/StarOrientationObserver.cs L117-L137
[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.RateHold))]
private static class RateHoldPatch {
    static void Prefix(FlightComputer __instance) {
        try {
            OhMyStarsWindow.ClearOrientedStar(__instance);
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] RateHold Prefix: {ex}");
        }
    }
}

[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.SetNullRot))]
private static class SetNullRotPatch {
    static void Prefix(FlightComputer __instance) {
        try {
            OhMyStarsWindow.ClearOrientedStar(__instance);
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] SetNullRot Prefix: {ex}");
        }
    }
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L117-L137)[^ohmystars-starorientationobserver]

2. **Add the entry points that cannot be inlined into the UI paths.** `SetStabilization`, `SetEnum` and `ToggleEnum` are ordinary methods; their prefixes filter the argument to the attitude-related enums with `IsAttitudeModeChange`:

```csharp
// src/StarOrientationObserver.cs L21-L29
// Every standard mode button/keybind ultimately funnels through Vehicle.SetEnum/ToggleEnum
// (see FlightComputerInputData.Apply). Some of those paths (e.g. plain Manual/Auto mode selection
// via ToggleAttitudeMode, or a direct AttitudeMode assignment) never touch TrackTarget/RateHold/SetNullRot,
// so they must be caught here too or star orientation keeps getting silently reapplied every physics tick.
private static bool IsAttitudeModeChange(Enum? enumValue) {
    return enumValue is FlightComputerAttitudeMode ||
           enumValue is VehicleReferenceFrame ||
           (enumValue is FlightComputerAttitudeTrackTarget target && target != FlightComputerAttitudeTrackTarget.Custom);
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L21-L29)

```csharp
// src/StarOrientationObserver.cs L139-L176
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetStabilization))]
private static class SetStabilizationPatch {
    static void Prefix(Vehicle __instance, bool active) {
        try {
            if(!active) {
                OhMyStarsWindow.ClearOrientedStar(__instance.FlightComputer);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] SetStabilization Prefix: {ex}");
        }
    }
}

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.SetEnum))]
private static class SetEnumPatch {
    static void Prefix(Vehicle __instance, Enum? enumValue) {
        try {
            if(IsAttitudeModeChange(enumValue)) {
                OhMyStarsWindow.ClearOrientedStar(__instance.FlightComputer);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] SetEnum Prefix: {ex}");
        }
    }
}

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.ToggleEnum))]
private static class ToggleEnumPatch {
    static void Prefix(Vehicle __instance, Enum? enumValue) {
        try {
            if(IsAttitudeModeChange(enumValue)) {
                OhMyStarsWindow.ClearOrientedStar(__instance.FlightComputer);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] ToggleEnum Prefix: {ex}");
        }
    }
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L139-L176)

3. **Name a protected method by string.** `HandleKittenAction` is `protected override`: see [TargetMethods with per-name AccessTools lookups and the __args array](../patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-5.md).

```csharp
// src/StarOrientationObserver.cs L67-L82
// Kitten trim actions are the kitten-side equivalents of the game's attitude mode buttons.
// ManualTrim writes FlightComputer.AttitudeMode directly, bypassing every patched method above,
// so the kitten's star orientation must be dropped here or the mod would keep reapplying it.
// (HandleKittenAction is protected, hence the string method name.)
[HarmonyPatch(typeof(KittenEva), "HandleKittenAction")]
private static class KittenActionPatch {
    static void Prefix(KittenEva __instance, KittenEvaAction action) {
        try {
            if(action is KittenEvaAction.ManualTrim or KittenEvaAction.RateTrim) {
                OhMyStarsWindow.ClearOrientedStar(__instance.FlightComputer);
            }
        } catch(Exception ex) {
            DefaultCategory.Log.Warning($"[OhMyStars] KittenAction Prefix: {ex}");
        }
    }
}
```

Source: [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L67-L82)

4. **Write down the paths that stay exposed.** After the prefixes above, these ways of leaving the custom target still do not necessarily reach a prefix that fires:

| Path | Why |
|---|---|
| `FlightComputer.UpdateAttitudeTarget` calling `SetInertialNullRot` and then `SetNullRot` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1276)) | Worker-side, nested inlined setters; reached only if the `SetNullRot` prefix fires |
| `FlightComputer.UpdateBurnTarget` calling `SetNullRot(BurnBody)` when an auto burn finishes ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1244-L1252)) | Worker-side, same reason |
| `ComputeControl` assigning `AttitudeTrackTarget = PositiveDv` for an auto burn ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L478-L482)) | Direct field assignment; no method at all |

5. **Confirm with a behavioural check.** Neither this article nor the mod's code proves the setter prefixes fire in the running game. The check is to count hits: increment a counter in each prefix, drive the
   in-game paths (navball buttons, hotkeys, an auto burn, kitten trim), and compare with what the feature expects. A prefix that never counts while its path ran has been bypassed.

## Complete example

| File | Role |
|---|---|
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L9-L29) | Class-by-class apply and the enum filter |
| [src/StarOrientationObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarOrientationObserver.cs#L117-L176) | Setter and entry-point prefixes |

## Variations and gotchas

- **The mod's own caveat.** The feature page records that `SetNullRot`, `RateHold` and `TrackTarget` are `AggressiveInlining` and that `Vehicle.SetStabilization(true)` relies on the `RateHold` prefix alone; in this build `KSA.dll` carries `DisableOptimizations`, so the prefixes most likely fire. That is an inference, not an in-game result.
- **Two prefixes run on workers.** `SetNullRot` and the MMU methods run on vehicle workers and touch the mod's plain collections; see [Patches on planner tasks that run on pooled threads](../../harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md).
- **Patching both layers is the point.** If a later build inlines `RateHold` into `SetStabilization`, the outer prefix still fires.
- **Return values.** The prefixes here only clear state and return; none skips the original ([Skipping Harmony prefixes](../../harmony/skip-the-original-method-with-a-prefix/overview.md)).

## See also

- [Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md), [Postfix the single non-inlined outer method instead of a per-vehicle callee](patch-around-aggressive-inlining-3.md), [Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md)
- [Changing behaviour of a game method marked AggressiveInlining](overview.md)
- [Attitude-mode hand-off patches](../../../../mods/ohmystars/features/attitude-mode-handoff-patches.md)
- [TargetMethods with per-name AccessTools lookups and the __args array](../patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-5.md) for the `TargetMethods` form used in the same file
- Related topic: drive-the-flight-computer-and-attitude-modes-from-a-mod

[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ohmystars-starorientationobserver]: OhMyStars/src/StarOrientationObserver.cs
