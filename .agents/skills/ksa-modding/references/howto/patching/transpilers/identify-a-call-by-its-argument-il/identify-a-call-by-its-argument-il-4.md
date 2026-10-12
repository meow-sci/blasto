---
type: How-To
title: Match the instruction that consumes the result
description: Rewrite Vehicle.get_Target only where it feeds a null test (brtrue, brfalse, or ldnull then ceq or cgt.un), in four methods chosen by TargetMethods, so the game's own null checks see a mod-supplied root target while uses of Target as a value are left alone
category: patching
group: transpilers
topic: identify-a-call-by-its-argument-il
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/compendium/features/navball-target-marker-and-tgt-frame]
techniques: [harmony-transpiler, il-consumer-match, harmony-target-methods]
hooks:
  - harmony-transpiler KSA.GaugeCanvas.IsContextVisible
  - harmony-transpiler KSA.GaugeButtonNavBallMode.IsDisabled
  - harmony-transpiler KSA.GaugeButtonNavBallMode.OnReleased
  - harmony-transpiler KSA.Vehicle.UpdateNavballData
lifecycle: [startup:immediate-load, frame:apply-solvers, frame:draw-ui, frame:render]
game_types: [KSA.Vehicle, KSA.GaugeCanvas, KSA.GaugeButtonNavBallMode, KSA.NavigationTarget]
tags: [harmony, transpiler, il, null-check, getter, consumer, target-methods]
---

Sometimes the instruction you want to change is the same getter call in several places, and only some of those uses mean "is there a target?". Compendium wants the game's own null checks on
`Vehicle.Target` to see a mod-supplied navigation target (so the TGT gauge panel shows and the TGT navball button enables), but must leave alone the places where `Target` is
*passed on* as a value. It identifies the right uses by the instruction that **consumes** the result: `brtrue`/`brfalse`, or `ldnull` followed by `ceq`/`cgt.un`. The same transpiler is applied to four methods with `TargetMethods`.

Prerequisites: a StarMap mod with `0Harmony` and the recipe from [Retarget the operand of one call (same signature)](../write-a-harmony-transpiler/write-a-harmony-transpiler-1.md). Demonstrated by Compendium in
[Navball target marker and TGT frame for a target without an orbit](../../../../mods/compendium/features/navball-target-marker-and-tgt-frame.md).

## Background: how the game does it

Four stock methods test `Vehicle.Target` for null. Each is a one-line decision:

| Method | What the null test controls | Decompiled source |
|---|---|---|
| `GaugeCanvas.IsContextVisible` | `GaugeVisibilityFlag.Target` canvases (the TGT gauge panel) are shown | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276)[^ksa-gaugecanvas] |
| `GaugeButtonNavBallMode.IsDisabled` | The `Dock` navball-mode button is enabled | [current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L19-L26)[^ksa-gaugebuttonnavballmode] |
| `GaugeButtonNavBallMode.OnReleased` | Pressing it actually calls `SetNavBallFrame(Dock)` | [current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L28-L38) |
| `Vehicle.UpdateNavballData` | The navball is not reset out of `Dock` when `Target` is null | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2681-L2697)[^ksa-vehicle] |

In the 5554 IL each method has exactly one `get_Target()` call followed by `ldnull; ceq` (a `!= null` compiles to that plus a second `ceq`). `UpdateNavballData` has a second `get_Target()` that
is an argument to `NavigationTarget.Create`; the three gauge methods read the property with `callvirt`, `UpdateNavballData` with `call`:

```text
// KSA.dll IL via ilspycmd -il: KSA.Vehicle::UpdateNavballData (parameter lists shortened)
IL_00d1: call instance class KSA.IOrbiter KSA.Vehicle::get_Target(...)
IL_00d6: ldnull
IL_00d7: ceq
IL_00d9: br.s IL_00dc
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

```text
// KSA.dll IL via ilspycmd -il: KSA.Vehicle::UpdateNavballData (parameter lists shortened)
IL_0372: call instance class KSA.IOrbiter KSA.Vehicle::get_Target(...)
IL_0377: ldarg.0
IL_0378: call instance class KSA.Part KSA.Vehicle::get_TargetPart(...)
IL_037d: ldarg.0
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)

## Lifecycle and timing

- **Install: `startup:immediate-load`.** Attribute patches applied by `Harmony.PatchAll` from `[StarMapImmediateLoad]`; the exactly-one check fails the install rather than patching a changed method.
- **`UpdateNavballData` runs in `frame:apply-solvers`,** main thread (via `Vehicle.UpdatePerFrameData` at the end of `Universe.ApplyVehicleSolvers`).
- **The gauge methods run in `frame:draw-ui` and `frame:render`.** `Program.OnDrawUiThreadSafe` calls `GaugeCanvas.CanvasesOnDrawUi`, and `GaugeCanvas.OnDrawUi` checks `IsContextVisible` and dispatches button
  hover and release; each button's look is packed again while `Program.RenderGame` records the gauge canvases, so the `IsDisabled` rewrite runs twice per frame, both on the main thread, reading only a side table.
  See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **List the methods in `TargetMethods`.** One transpiler class, four targets; any failed lookup surfaces as a patch failure for the class:

```csharp
// src/RootTargetFlightComputerPatches.cs L9-L18
[HarmonyPatch]
internal static class RootTargetPresencePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GaugeCanvas), nameof(GaugeCanvas.IsContextVisible));
        yield return AccessTools.Method(typeof(GaugeButtonNavBallMode), nameof(GaugeButtonNavBallMode.IsDisabled));
        yield return AccessTools.Method(typeof(GaugeButtonNavBallMode), nameof(GaugeButtonNavBallMode.OnReleased));
        yield return AccessTools.Method(typeof(Vehicle), "UpdateNavballData");
    }
```

Source: [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L9-L18)[^compendium-roottargetflightcomputerpatches]

2. **Match the getter, then test the next instruction.** Replace the call only when the next opcode is `brtrue`/`brfalse` (short or long), or `ldnull` followed by `ceq`/`cgt.un`. A `Target` passed on as a value is left alone:

```csharp
// src/RootTargetFlightComputerPatches.cs L25-L49
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
```

Source: [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L25-L49)

3. **Swap opcode and operand.** The replacement is a static `GetNavigationTarget(Vehicle)` that returns `object`: `(object?)vehicle.Target ?? RootNavigationTargets.GetTarget(vehicle)`. The three gauge methods
   read the property with `callvirt`, so the transpiler sets the opcode to `call`, which a static replacement requires. The replacement takes the instance that was on the stack and returns something a null test accepts:

```csharp
// src/RootTargetFlightComputerPatches.cs L20-L23
internal static object? GetNavigationTarget(Vehicle vehicle)
{
    return (object?)vehicle.Target ?? RootNavigationTargets.GetTarget(vehicle);
}
```

Source: [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L20-L23)

4. **Count per method and throw unless exactly one.** `__originalMethod.Name` goes in the message so a failure says which of the four methods changed:

```csharp
// src/RootTargetFlightComputerPatches.cs L50-L55
    if (replacements != 1)
    {
        throw new InvalidOperationException($"Compendium expected one target presence check in {__originalMethod.Name}, found {replacements}.");
    }
    return code;
}
```

Source: [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L50-L55)

5. **Pair with a postfix where the check is in a private overload.** The flight computer's TGT frame button is enabled by a postfix on the private `Vehicle.IsDisabled(VehicleReferenceFrame)`:

```csharp
// src/RootTargetFlightComputerPatches.cs L68-L76
[HarmonyPatch(typeof(Vehicle), "IsDisabled", new[] { typeof(VehicleReferenceFrame) })]
[HarmonyPostfix]
private static void EnableRootFrame(Vehicle __instance, VehicleReferenceFrame frame, ref bool __result)
{
    if (frame == VehicleReferenceFrame.Dock && RootNavigationTargets.GetTarget(__instance) != null)
    {
        __result = !__instance.IsControllable;
    }
}
```

Source: [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L68-L76)

## Complete example

| File | Role |
|---|---|
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L9-L76) | Presence transpiler, `GetNavigationTarget`, frame-button postfix |
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L62-L87)[^compendium-roottargetpatches] | The call-site swap on the same methods ([Wider signature with an extra ldarg.0 appended](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-2.md)) |

## Variations and gotchas

- **`UpdateNavballData` carries two transpilers** (this presence rewrite and the call swap); they match different instructions, so order does not matter.
- **The matcher depends on compiler output.** It recognises `brtrue`/`brfalse` or `ldnull` plus `ceq`/`cgt.un` right after the getter. A build compiled differently (for example a pattern match that
  emits another sequence) changes the count and makes the patch throw.
- **`GetNavigationTarget` returns `object`.** It is valid only where the value is compared with null; any value use of `Target` still sees the native target only.
- **A failed check disables more than targeting.** `PatchAll` stops at the first failing class, and Compendium's handler also skips the fonts, data and logo loaded after `Patcher.Patch`.
- **What stays disabled.** Rendezvous planning, docking alignment and the native Target Track window still need a native orbiting target.

## See also

- [Scan backwards through the argument loads to the previous call](identify-a-call-by-its-argument-il-1.md), [Anchor on a unique neighbouring call, then walk to the target](identify-a-call-by-its-argument-il-2.md), [Anchor on a UTF-8 label literal in the method](identify-a-call-by-its-argument-il-3.md)
- [Identifying the right call in a method that makes several similar calls](overview.md)
- [Navball target marker and TGT frame for a target without an orbit](../../../../mods/compendium/features/navball-target-marker-and-tgt-frame.md)
- [Wider signature with an extra ldarg.0 appended](../swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-2.md), [TargetMethods with per-name AccessTools lookups and the __args array](../../internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-5.md)
- Related tasks: set-and-clear-the-vehicle-target, synthesize-a-navigation-target-for-the-flight-computer

[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonnavballmode]: KSA.GaugeButtonNavBallMode (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^compendium-roottargetflightcomputerpatches]: Compendium/src/RootTargetFlightComputerPatches.cs
[^compendium-roottargetpatches]: Compendium/src/RootTargetPatches.cs
