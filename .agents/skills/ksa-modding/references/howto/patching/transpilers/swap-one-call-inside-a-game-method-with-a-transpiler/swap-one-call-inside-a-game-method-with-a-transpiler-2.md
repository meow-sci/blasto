---
type: How-To
title: Wider signature with an extra ldarg.0 appended
description: Replace NavigationTarget.Create inside two private Vehicle methods with a method that takes the original four arguments plus the vehicle, by inserting ldarg.0 before the call and moving the call site's labels and exception markers onto it
category: patching
group: transpilers
topic: swap-one-call-inside-a-game-method-with-a-transpiler
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/navball-target-marker-and-tgt-frame]
techniques: [harmony-transpiler, call-site-rewrite, il-stack]
hooks:
  - starmap StarMapImmediateLoad
  - harmony-transpiler KSA.Vehicle.UpdateNavBallMarkers
  - harmony-transpiler KSA.Vehicle.UpdateNavballData
lifecycle: [startup:immediate-load, frame:apply-solvers]
game_types: [KSA.Vehicle, KSA.NavigationTarget]
tags: [harmony, transpiler, call-site, ldarg, instance, extra-argument, navball]
---

Sometimes the replacement needs more information than the original call carries. Compendium wants the navball to show a target that has no orbit, so it
replaces `NavigationTarget.Create(target, targetPart, parent, time)` inside two private `Vehicle` methods with
`CreateMarkerTarget(target, targetPart, parent, time, vehicle)`, which also receives the vehicle. The transpiler inserts a single `ldarg.0` before the
call, so the four arguments already on the stack are followed by `this`, and makes the new first instruction inherit the old call's labels and exception
markers. The replacement forwards to the original when it has nothing to add.

Prerequisites: a StarMap mod with `0Harmony` and the same-signature swap from [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md). Demonstrated by Compendium in
[Navball target marker and TGT frame for a target without an orbit](../../../../mods/compendium/features/navball-target-marker-and-tgt-frame.md).

## Background: how the game does it

`NavigationTarget.Create` is a static factory that returns `null` without a target and otherwise reads `target.Orbit.GetStateVectorsAt(time)`.
`Vehicle.UpdateNavBallMarkers` calls it once to place the target marker, and `Vehicle.UpdateNavballData` calls it once in the `Dock` (TGT) frame case,
where a null result throws "Navigation target is null in dock frame":

```csharp
// current/decomp/KSA/NavigationTarget.cs L16-L24
public static NavigationTarget? Create(IOrbiter? target, Part? targetPart, IParentBody myParent, UniverseTime time)
{
	if (target == null)
	{
		return null;
	}
	doubleQuat cce2Cci = myParent.GetCce2Cci();
	StateVectors stateVectorsAt = target.Orbit.GetStateVectorsAt(time);
	double3 double5;
```

Source: [current/decomp/KSA/NavigationTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavigationTarget.cs#L16-L24)[^ksa-navigationtarget]

```csharp
// current/decomp/KSA/Vehicle.cs L2797-L2803
private void UpdateNavBallMarkers()
{
	ref readonly StateVectors stateVectors = ref Orbit.StateVectors;
	double3 double5 = stateVectors.PositionCci + CtrlOriginBody.Transform(GetBody2Cci());
	double3 velocityCci = stateVectors.VelocityCci;
	NavigationTarget? target = NavigationTarget.Create(Target, TargetPart, Parent, stateVectors.StateTime);
	doubleQuat? vlf2Cci = VehicleReferenceFrameEx.GetVlf2Cci(double5, velocityCci);
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2797-L2803)[^ksa-vehicle]

```csharp
// current/decomp/KSA/Vehicle.cs L2760-L2777
case VehicleReferenceFrame.Dock:
{
	doubleQuat doubleQuat3 = GetCtrl2Cci().Inverse();
	NavigationTarget? navigationTarget = NavigationTarget.Create(Target, TargetPart, Parent, stateVectors.StateTime);
	if (navigationTarget.HasValue)
	{
		NavigationTarget valueOrDefault = navigationTarget.GetValueOrDefault();
		if (true)
		{
			double3 double5 = valueOrDefault.DockFrameRatesCci.Transform(doubleQuat3);
			doubleQuat2 = doubleQuat.Concatenate(valueOrDefault.Dock2Cci, doubleQuat3);
			attitudeRates = CtrlRates - double5;
			altitude = GetBarometricAltitude();
			speed = (stateVectors.VelocityCci - valueOrDefault.VelocityCci).Length();
			break;
		}
	}
	throw new InvalidOperationException("Navigation target is null in dock frame");
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2760-L2777)

The IL of the call in `UpdateNavBallMarkers`. The four arguments are loaded in order (`Target`, `TargetPart`, `Parent`, the state time); `ldarg.0` is `this`
because the method is an instance method:

```text
// KSA.dll IL via ilspycmd -il: KSA.Vehicle::UpdateNavBallMarkers (parameter lists shortened)
IL_0031: ldarg.0
IL_0032: call instance class KSA.IOrbiter KSA.Vehicle::get_Target(...)
IL_0037: ldarg.0
IL_0038: call instance class KSA.Part KSA.Vehicle::get_TargetPart(...)
IL_003d: ldarg.0
IL_003e: call instance class KSA.IParentBody KSA.Vehicle::get_Parent(...)
IL_0043: ldloc.0
IL_0044: ldfld valuetype KSA.UniverseTime KSA.StateVectors::StateTime
IL_0049: call valuetype [System.Runtime]System.Nullable`1<valuetype KSA.NavigationTarget> KSA.NavigationTarget::Create(...)
IL_004e: stloc.3
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

- **Install: `startup:immediate-load`.** The patches are attribute patches applied by `Harmony.PatchAll` from `[StarMapImmediateLoad]`; a transpiler runs once
  at that moment and the exactly-one check fails the install rather than patching a changed method.
- **Run: `frame:apply-solvers`, main thread.** `UpdateNavballData` and `UpdateNavBallMarkers` are private and called only from `Vehicle.UpdatePerFrameData`,
  whose per-frame caller is `Universe.ApplyVehicleSolvers`, which ends with `CurrentSystem.UpdatePerFrameData()` after `ApplyResultsToMainThread`
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)[^ksa-universe]). The vehicle's state is settled there. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Write the replacement with the original arguments in order, then the extra parameter.** It returns a synthesized target when the vehicle has a root
   target and forwards to the original otherwise, so vehicles without a root target behave exactly as before:

```csharp
// src/RootNavigationTargets.cs L181-L187
public static NavigationTarget? CreateMarkerTarget(IOrbiter? target, Part? targetPart, IParentBody parent, UniverseTime time, Vehicle vehicle)
{
    var root = GetTarget(vehicle);
    return root != null
        ? CreateNavigationTarget(root, parent, time)
        : NavigationTarget.Create(target, targetPart, parent, time);
}
```

Source: [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L181-L187)[^compendium-rootnavigationtargets]

2. **Find the call and emit `ldarg.0` then the new `call`.** Reuse the old call's labels and blocks on the first emitted instruction with
   `MoveLabelsFrom` and `MoveBlocksFrom`, so a branch into the call site (or the start of an exception region) still lands correctly. Count the replacements and
   throw unless there is exactly one:

```csharp
// src/RootTargetPatches.cs L62-L87
[HarmonyPatch(typeof(Vehicle), "UpdateNavBallMarkers")]
[HarmonyTranspiler]
internal static IEnumerable<CodeInstruction> AddMarkerTarget(IEnumerable<CodeInstruction> instructions)
{
    var create = AccessTools.Method(typeof(NavigationTarget), nameof(NavigationTarget.Create));
    var replacement = AccessTools.Method(typeof(RootNavigationTargets), nameof(RootNavigationTargets.CreateMarkerTarget));
    int replacements = 0;
    foreach (var instruction in instructions)
    {
        if (instruction.Calls(create))
        {
            // Preserve branch targets on the first instruction of the replacement.
            yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            yield return new CodeInstruction(OpCodes.Call, replacement);
            replacements++;
        }
        else
        {
            yield return instruction;
        }
    }
    if (replacements != 1)
    {
        throw new InvalidOperationException($"Compendium expected one navball target call, found {replacements}.");
    }
}
```

Source: [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L62-L87)[^compendium-roottargetpatches]

3. **Apply the same transpiler to the second method.** `UpdateNavballData` has exactly one such call in the 5554 IL, so the second class just delegates:

```csharp
// src/RootTargetFlightComputerPatches.cs L61-L66
[HarmonyPatch(typeof(Vehicle), "UpdateNavballData")]
[HarmonyTranspiler]
private static IEnumerable<CodeInstruction> AddNavballFrameTarget(IEnumerable<CodeInstruction> instructions)
{
    return RootTargetPatches.AddMarkerTarget(instructions);
}
```

Source: [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L61-L66)[^compendium-roottargetflightcomputerpatches]

4. **Know when more than one transpiler lands on a method.** `UpdateNavballData` also carries the presence rewrite described in
   [Match the instruction that consumes the result](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-4.md); they match different instructions, so their order does not matter.

5. **Build the synthesized value to match the game's own conventions.** `CreateNavigationTarget` mirrors the game's branch for a target in another sphere of
   influence: positions and velocities relative to the vehicle's parent, rotated by `parent.GetCce2Cci()`:

```csharp
// src/RootNavigationTargets.cs L165-L179
public static NavigationTarget CreateNavigationTarget(Astronomical body, IParentBody parent, UniverseTime time)
{
    double3 parentPosition = parent is IOrbiter positionOrbiter
        ? positionOrbiter.GetPositionEcl(time) : parent.GetPositionEcl();
    double3 parentVelocity = parent is IOrbiter velocityOrbiter
        ? velocityOrbiter.GetVelocityEcl(time) : parent.GetVelocityEcl();
    doubleQuat ecl2Cci = parent.GetCce2Cci();
    return new NavigationTarget
    {
        PositionCci = (body.GetPositionEcl() - parentPosition).Transform(ecl2Cci),
        VelocityCci = (body.GetVelocityEcl() - parentVelocity).Transform(ecl2Cci),
        Dock2Cci = VehicleReferenceFrameEx.GetDock2Cci(doubleQuat.Concatenate(body.GetBodyFixed2Ecl(), ecl2Cci), doubleQuat.Identity),
        DockFrameRatesCci = body.GetBodyRates().Transform(doubleQuat.Concatenate(body.GetBodyFixed2Ecl(), ecl2Cci))
    };
}
```

Source: [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L165-L179)

## Complete example

| File | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L62-L87) | The call-site transpiler |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L165-L187) | The replacement and the synthesized target |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L61-L66) | Second application of the transpiler |

## Variations and gotchas

- **Where `ldarg.0` goes matters.** The call's arguments are already on the stack, so `ldarg.0` is appended as the fifth value; the replacement's
  last parameter is the vehicle. For a static caller there is no `this`; load whatever else you need instead.
- **Labels to the first instruction.** Moving labels onto the inserted `ldarg.0` keeps branches into the call site landing before the extra load; an
  end-of-region marker would instead stay with the call ([Move labels and begin-exception markers onto the inserted instruction](../insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md)).
- **Private targets are resolved by name.** `UpdateNavBallMarkers` and `UpdateNavballData` are matched by name; a rename becomes a patch failure at load.
- **PatchAll stops at the first failure.** A thrown check leaves earlier classes patched and skips later ones, and in Compendium it also skips the
  fonts, data and logo loaded after `Patcher.Patch` in the same handler.

## See also

- [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md), [Replacement that does mod work, then calls the original](swap-one-call-inside-a-game-method-with-a-transpiler-3.md), [Gate helper that skips the call for the mod's own condition](swap-one-call-inside-a-game-method-with-a-transpiler-4.md), [Adapter that fixes up arguments and converts the result back](swap-one-call-inside-a-game-method-with-a-transpiler-5.md)
- [Swapping one call inside a game method with a transpiler](overview.md)
- [Navball target marker and TGT frame for a target without an orbit](../../../../mods/compendium/features/navball-target-marker-and-tgt-frame.md)
- [Match the instruction that consumes the result](../identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-4.md), [Writing a Harmony transpiler for a KSA method](../write-a-harmony-transpiler/overview.md)
- Related task: synthesize-a-navigation-target-for-the-flight-computer

[^ksa-navigationtarget]: KSA.NavigationTarget (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^compendium-rootnavigationtargets]: Compendium/src/RootNavigationTargets.cs
[^compendium-roottargetpatches]: Compendium/src/RootTargetPatches.cs
[^compendium-roottargetflightcomputerpatches]: Compendium/src/RootTargetFlightComputerPatches.cs
