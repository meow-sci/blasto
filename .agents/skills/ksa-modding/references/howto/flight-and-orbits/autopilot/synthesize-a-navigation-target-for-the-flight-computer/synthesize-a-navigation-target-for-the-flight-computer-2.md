---
type: How-To
title: Transpile NavigationTarget.Create call sites and target-presence checks for the navball
description: Swap the one NavigationTarget.Create call in the navball marker and navball data methods for a mod method, and rewrite Vehicle.Target null tests so a synthesized target counts as present
category: flight-and-orbits
group: autopilot
topic: synthesize-a-navigation-target-for-the-flight-computer
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/navball-target-marker-and-tgt-frame]
techniques: [harmony-transpiler, harmony-postfix]
hooks:
  - harmony-transpiler KSA.Vehicle.UpdateNavBallMarkers
  - harmony-transpiler KSA.Vehicle.UpdateNavballData
  - harmony-transpiler KSA.GaugeCanvas.IsContextVisible
  - harmony-postfix KSA.Vehicle.IsDisabled
lifecycle: [startup:immediate-load, frame:apply-solvers, frame:draw-ui, frame:render]
game_types:
  - KSA.NavigationTarget
  - KSA.Vehicle
  - KSA.NavBallData
  - KSA.GaugeCanvas
  - KSA.GaugeButtonNavBallMode
  - KSA.VehicleReferenceFrameEx
tags: [navigation-target, navball, transpiler, il, gauges, tgt-frame, presence-check]
---

Outcome: the navball draws a target marker toward a navigation-only target, the TGT navball mode works, the TGT gauge panel appears and the flight computer's TGT reference-frame button is enabled. Compendium does not fake an orbit: it rewrites the one call that builds the struct and the few places that ask whether a target exists.[Navball target marker and TGT frame for a target without an orbit](../../../../mods/compendium/features/navball-target-marker-and-tgt-frame.md)
Prerequisites: StarMap and Harmony (transpilers).

## Background: how the game does it

`Vehicle.UpdateNavBallMarkers` calls `NavigationTarget.Create` once to place the target marker; `Vehicle.UpdateNavballData` calls it once in the `Dock` frame case, where a null result throws "Navigation target is null in dock frame".[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2797-L2801) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2760-L2777)
Both methods are private and called only from `Vehicle.UpdatePerFrameData`, which `Universe.ApplyVehicleSolvers` reaches through `CurrentSystem.UpdatePerFrameData()`.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2679) The presence tests are `Vehicle.get_Target()` calls followed by a null test in `GaugeCanvas.IsContextVisible` (`GaugeVisibilityFlag.Target` canvases),
`GaugeButtonNavBallMode.IsDisabled` and `OnReleased`, and `Vehicle.UpdateNavballData`; the stock body of `Vehicle.IsDisabled(VehicleReferenceFrame)` disables `Dock` whenever `Target == null`.[^ksa-gaugecanvas] [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276) [^ksa-gaugebuttonnavballmode] [current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L19-L38) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6549-L6552)

## Lifecycle and timing

- **Install: `startup:immediate-load`.** All patches are attribute patches applied by `Harmony.PatchAll` from `[StarMapImmediateLoad]`; transpilers run once, and the exactly-one checks fail the install rather than patching a changed method.
- **Navball update: `frame:apply-solvers`, main thread.** `ApplyVehicleSolvers` runs before `ApplyInputEvents` in `PrepareFrame`, so a target selected at this frame's input point first appears on the navball in the next frame.
- **Gauges and button presses: `frame:draw-ui`**, after `[StarMapAfterGui]` in the same frame: `Program.OnDrawUiThreadSafe` calls `GaugeCanvas.CanvasesOnDrawUi`, which checks `IsContextVisible` and dispatches hover and release.
- **Button state: also `frame:render`.** `GaugeBundle.UpdateInstanceBuffer` calls every binding's `PackData` while `Program.RenderGame` records the gauge canvases, so the transpiled `IsDisabled` and the `IsDisabled(VehicleReferenceFrame)` postfix run twice per frame; both are main-thread reads of the side table.

The replacement methods only read the side table and celestial positions, allocate nothing and do not touch ImGui. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Swap the call site.** Find the `call NavigationTarget::Create` and replace it with `ldarg.0` followed by a call to your method; the four original arguments stay on the stack and `this` becomes a fifth argument. Move labels and exception-block markers to the new first instruction. Throw unless exactly one call was replaced:

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

   Apply the same transpiler to `UpdateNavballData` from a second class; the two transpilers on that method match different instructions, so their order does not matter.
2. **Write the replacement** to forward to the original when there is no synthesized target, so vehicles without one behave exactly as before:

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

3. **Rewrite presence checks.** Target four methods through `TargetMethods()`; replace the `Vehicle::get_Target()` call that is immediately followed by a null test with a static call to `GetNavigationTarget(Vehicle)` returning `(object?)vehicle.Target ?? RootNavigationTargets.GetTarget(vehicle)`; set the opcode to `call` (the gauge methods used `callvirt`); throw unless exactly one test was rewritten:

```csharp
// src/RootTargetFlightComputerPatches.cs L20-L55
internal static object? GetNavigationTarget(Vehicle vehicle)
{
    return (object?)vehicle.Target ?? RootNavigationTargets.GetTarget(vehicle);
}

[HarmonyTranspiler]
private static IEnumerable<CodeInstruction> IncludeRootPresence(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
{
    var code = instructions.ToList();
    var getter = AccessTools.PropertyGetter(typeof(Vehicle), nameof(Vehicle.Target));
    var replacement = AccessTools.Method(typeof(RootTargetPresencePatches), nameof(GetNavigationTarget));
    int replacements = 0;
    for (int i = 0; i < code.Count - 1; i++)
    {
        if (!code[i].Calls(getter))
        {
            continue;
        }
        var next = code[i + 1].opcode;
        // Only replace null tests; actual IOrbiter consumers retain the native target.
        if (next == OpCodes.Brtrue || next == OpCodes.Brtrue_S ||
            next == OpCodes.Brfalse || next == OpCodes.Brfalse_S ||
            (next == OpCodes.Ldnull && i + 2 < code.Count &&
             (code[i + 2].opcode == OpCodes.Ceq || code[i + 2].opcode == OpCodes.Cgt_Un)))
        {
            code[i].opcode = OpCodes.Call;
            code[i].operand = replacement;
            replacements++;
        }
    }
    if (replacements != 1)
    {
        throw new InvalidOperationException($"Compendium expected one target presence check in {__originalMethod.Name}, found {replacements}.");
    }
    return code;
}
```

4. **Enable the TGT frame button.** A postfix on the private `Vehicle.IsDisabled(VehicleReferenceFrame)` sets `__result = !IsControllable` for `Dock` when a root target exists; the flight-computer gauge buttons reach that overload through `Vehicle.IsFlightComputerDisabled<T>`.
5. **Synthesize the struct** as in [approach 1](synthesize-a-navigation-target-for-the-flight-computer-1.md) (`CreateNavigationTarget`).

## Complete example

| File | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L62-L87) | `NavigationTarget.Create` call-site transpiler with label and block preservation |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L9-L56) | presence transpiler over four methods |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L61-L76) | `UpdateNavballData` swap and the `Dock` frame enable postfix |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L165-L187) | synthesized `NavigationTarget` and the marker replacement |

## Variations and gotchas

- **IL shape checks are your safety net.** In build 2026.10.10.5554 each method contains exactly one such call or test; a game update that adds a second makes the install fail loudly instead of patching the wrong site.
- **Only rewrite null tests.** Real `IOrbiter` consumers of `Target` (passing it on to `Create`) must keep the native value, so the transpiler matches `brtrue`/`brfalse` or `ldnull; ceq/cgt.un` after the getter.
- **`callvirt` to `call`** is required when the replacement is a static method.
- **Run order.** Two transpilers on one method are fine only if they match different instructions.

## See also

- [Overview](overview.md); [finalizer injection](synthesize-a-navigation-target-for-the-flight-computer-1.md)
- [Navball target marker and TGT frame for a target without an orbit](../../../../mods/compendium/features/navball-target-marker-and-tgt-frame.md)
- [Set or clear a vehicle's target, including bodies the game cannot target](../set-and-clear-the-vehicle-target/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonnavballmode]: KSA.GaugeButtonNavBallMode (decompiled, build 2026.10.10.5554)
