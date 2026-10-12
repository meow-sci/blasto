---
type: Mod Feature
title: Navball target marker and TGT frame for a target without an orbit
description: Transpilers swap the navball's NavigationTarget.Create call for a mod method and make four Target null tests see the navigation-only target, so the TGT marker, TGT navball mode, gauge panel and TGT frame work for a fixed star or barycenter
mod: compendium
category: patching
techniques: [harmony-transpiler, harmony-postfix, il-call-replacement]
hooks:
  - harmony-transpiler KSA.Vehicle.UpdateNavBallMarkers
  - harmony-transpiler KSA.Vehicle.UpdateNavballData
  - harmony-transpiler KSA.GaugeCanvas.IsContextVisible
  - harmony-transpiler KSA.GaugeButtonNavBallMode.IsDisabled
  - harmony-transpiler KSA.GaugeButtonNavBallMode.OnReleased
  - harmony-postfix KSA.Vehicle.IsDisabled(VehicleReferenceFrame)
lifecycle: [startup:immediate-load, frame:apply-solvers, frame:draw-ui, frame:render]
game_types:
  - KSA.Vehicle
  - KSA.NavigationTarget
  - KSA.NavBallData
  - KSA.GaugeCanvas
  - KSA.GaugeButtonNavBallMode
  - KSA.GaugeButtonFlightComputer
  - KSA.VehicleReferenceFrame
  - KSA.VehicleReferenceFrameEx
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.StellarBody
  - KSA.Barycenter
tags: [navball, transpiler, harmony, il, targets, gauges, reference-frame]
---

With a fixed star or barycenter selected as a [navigation-only root target](root-navigation-targets.md),
the navball draws the target marker toward it, the TGT navball mode button is enabled and keeps the
navball in the target frame, the TGT gauge panel becomes visible, and the flight computer's TGT
reference-frame button is enabled. The game produces all of this from one struct,
`NavigationTarget`, which it can only build for an `IOrbiter` with an orbit. Compendium does not fake
an orbit: it rewrites the one call that builds the struct and the few places that ask whether a target
exists.[^cmp-readme-md]

## How it works

1. **Where the navball gets its target.** `NavigationTarget.Create(IOrbiter? target, Part? targetPart,
   IParentBody myParent, UniverseTime time)` returns null without a target and otherwise reads
   `target.Orbit.GetStateVectorsAt(time)` ([current/decomp/KSA/NavigationTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavigationTarget.cs#L16-L90)).[^ksa-navigationtarget]
   `Vehicle.UpdateNavBallMarkers` calls it once to place the target marker, and
   `Vehicle.UpdateNavballData` calls it once in the `Dock` (TGT) frame case, where a null result throws
   "Navigation target is null in dock frame" ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2797-L2801),
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2760-L2777)).[^ksa-vehicle]
2. **Call-site swap.** The transpiler `AddMarkerTarget` finds the `call NavigationTarget::Create`
   instruction and replaces it with `ldarg.0` followed by `call RootNavigationTargets.CreateMarkerTarget`.
   The four original arguments stay on the evaluation stack and `this` becomes a fifth argument; labels
   and exception-block markers move to the new first instruction so branches into the call site still
   land correctly. It throws unless exactly one call was replaced
   ([src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L62-L87)).[^cmp-roottargetpatches] The same transpiler is applied to
   `UpdateNavballData` from a second class ([src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L61-L66)).[^cmp-roottargetflightcomputerpatches]
   In the IL of build 2026.10.10.5554 each method contains exactly one such call. `UpdateNavballData`
   therefore carries two transpilers (this call swap and the presence rewrite in step 5); they match
   different instructions, so their order does not matter.
3. **Replacement method.** `CreateMarkerTarget(target, targetPart, parent, time, vehicle)` returns a
   synthesized target when the vehicle has a root target and otherwise forwards to the original
   `NavigationTarget.Create`, so vehicles without a root target behave exactly as before
   ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L181-L187)).[^cmp-rootnavigationtargets]
4. **Synthesized target.** `CreateNavigationTarget` mirrors the game's branch for a target in another
   sphere of influence: position and velocity are the root's ECL values minus the vehicle parent's
   (sampled at `time` when the parent is an `IOrbiter`), rotated by `parent.GetCce2Cci()`; `Dock2Cci` comes
   from `VehicleReferenceFrameEx.GetDock2Cci(bodyFixed2Ecl ∘ cce2Cci, Identity)` and the frame rates
   from the body rates in the same frame ([src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L165-L179),
   [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L381-L397)).[^ksa-vehiclereferenceframeex] For both root types
   the game returns an identity body orientation, zero body rates and zero velocity
   ([current/decomp/KSA/StellarBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StellarBody.cs#L139-L147), [current/decomp/KSA/Barycenter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Barycenter.cs#L43-L52),
   [current/decomp/KSA/Barycenter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Barycenter.cs#L151-L159), [current/decomp/KSA/FixedStar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedStar.cs#L26-L35)), so the TGT frame is fixed ECL
   axes and the relative velocity is just the vehicle's own.[^ksa-stellarbody] [^ksa-barycenter] [^ksa-fixedstar]
5. **Presence checks.** `RootTargetPresencePatches` targets four methods through `TargetMethods()` and
   rewrites the single `Vehicle::get_Target()` call in each that is immediately followed by a null test
   (`brtrue`/`brfalse`, or `ldnull` then `ceq`/`cgt.un`). The getter call becomes a static call to
   `GetNavigationTarget(Vehicle)`, which returns `(object?)vehicle.Target ?? RootNavigationTargets.GetTarget(vehicle)`.
   Uses of `Target` as a value (passed on to `NavigationTarget.Create`) are left alone, and the patch
   throws unless exactly one test was rewritten per method
   ([src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L9-L56)). In the 5554 IL each of the four has exactly one
   `get_Target(); ldnull; ceq` sequence (a `!= null` compiles to that plus a second `ceq`). The three
   gauge methods read the property with `callvirt`, so the transpiler also sets the opcode to `call`,
   which a static replacement requires:[^ksa-gaugecanvas] [^ksa-gaugebuttonnavballmode]

   | Method | What the null test controls | Decompiled source |
   |---|---|---|
   | `GaugeCanvas.IsContextVisible` | `GaugeVisibilityFlag.Target` canvases (the TGT gauge panel) are shown | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276) |
   | `GaugeButtonNavBallMode.IsDisabled` | The `Dock` navball-mode button is enabled | [current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L19-L26) |
   | `GaugeButtonNavBallMode.OnReleased` | Pressing it actually calls `SetNavBallFrame(Dock)` | [current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L28-L38) |
   | `Vehicle.UpdateNavballData` | The navball is not reset out of `Dock` when `Target` is null | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2681-L2697) |

6. **TGT reference-frame button.** A postfix on the private `Vehicle.IsDisabled(VehicleReferenceFrame)`
   sets `__result = !IsControllable` for `Dock` when a root target exists; the stock body disables `Dock`
   whenever `Target == null` ([src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L68-L76),
   [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6549-L6552)). The flight-computer gauge buttons reach that overload through
   `Vehicle.IsFlightComputerDisabled<T>` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6860-L6900),
   [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L151-L154)).[^ksa-gaugebuttonflightcomputer]

## Lifecycle and timing

- **Install: `startup:immediate-load`.** All of these are attribute patches applied by
  `Harmony.PatchAll` in `Patcher.Patch`, called from `[StarMapImmediateLoad]`
  ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1245), [src/Patcher.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Patcher.cs#L11-L21)).[^cmp-compendiummain] [^cmp-patcher]
  Transpilers run once at that moment; the exactly-one checks fail the install rather than patching a
  changed method.
- **Navball update: `frame:apply-solvers`, main thread.** `UpdateNavballData` and
  `UpdateNavBallMarkers` are private and are called only from `Vehicle.UpdatePerFrameData`
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2679)). The per-frame caller is `Universe.ApplyVehicleSolvers`, which
  ends with `CurrentSystem.UpdatePerFrameData()` after `ApplyResultsToMainThread`
  ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)); that walks every root with `IParentBody.UpdatePerFrameDataTree`
  ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L274-L285), [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L110-L123)).[^ksa-universe]
  [^ksa-celestialsystem] [^ksa-iparentbody] The vehicle's state is settled there. Because
  `ApplyVehicleSolvers` runs before `InputEvents.ApplyInputEvents` in `PrepareFrame`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2167-L2181)), a root selected at this frame's input point first appears on the
  navball in the next frame.[^ksa-program]
- **Gauge canvases and button presses: `frame:draw-ui`, main thread.** `Program.OnDrawUiThreadSafe`
  calls `GaugeCanvas.CanvasesOnDrawUi` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3205)), and `GaugeCanvas.OnDrawUi` checks
  `IsContextVisible` and dispatches button hover and release ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L717-L720)). This is
  after `[StarMapAfterGui]` in the same frame. `IsContextVisible` has no other caller. The navball-mode
  button's `OnReleased` writes the frame directly with `SetNavBallFrame`, which only touches
  `NavBallData` (a main-thread display struct the solver does not read).
- **Button state: also `frame:render`.** Each button's look is packed while `Program.RenderGame`
  records the gauge canvases: `GaugeBundle.UpdateInstanceBuffer` calls every binding's `PackData`
  ([current/decomp/KSA/GaugeBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeBundle.cs#L116-L146), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940)).[^ksa-gaugebundle]
  `GaugeButtonNavBallMode.PackData` calls the transpiled `IsDisabled`, and
  `GaugeButtonFlightComputer.PackData` reaches the `IsDisabled(VehicleReferenceFrame)` postfix through
  `IsFlightComputerDisabled` ([current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L40-L57),
  [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L156-L173)). So these two patches run twice per frame, once in
  the draw (release) and once in the render pass; both are main-thread reads of the side table.
- The replacement methods only read the side table and celestial positions, never allocate managed
  objects, and do not touch ImGui.

## Key code

| Path | Role |
|---|---|
| [src/RootTargetPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetPatches.cs#L62-L87) | `NavigationTarget.Create` call-site transpiler with label/block preservation |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L9-L56) | Presence transpiler over four methods |
| [src/RootTargetFlightComputerPatches.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootTargetFlightComputerPatches.cs#L61-L76) | `UpdateNavballData` swap and the `Dock` frame enable postfix |
| [src/RootNavigationTargets.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/RootNavigationTargets.cs#L165-L187) | Synthesized `NavigationTarget` and the marker replacement |

## Snippets

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

```csharp
// src/RootNavigationTargets.cs L165-L187
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

public static NavigationTarget? CreateMarkerTarget(IOrbiter? target, Part? targetPart, IParentBody parent, UniverseTime time, Vehicle vehicle)
{
    var root = GetTarget(vehicle);
    return root != null
        ? CreateNavigationTarget(root, parent, time)
        : NavigationTarget.Create(target, targetPart, parent, time);
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `NavigationTarget.Create`, `NavigationTarget` fields | [current/decomp/KSA/NavigationTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavigationTarget.cs#L5-L90) | The struct the navball and flight computer read; call replaced |
| `Vehicle.UpdateNavBallMarkers`, `Vehicle.UpdateNavballData` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2681-L2801) | Transpiled |
| `Vehicle.UpdatePerFrameData` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2645-L2679) | Only caller of the two navball methods |
| `Vehicle.IsDisabled(VehicleReferenceFrame)` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6549-L6552) | Postfix enables `Dock` |
| `GaugeCanvas.IsContextVisible` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276) | Presence transpiler |
| `GaugeButtonNavBallMode.IsDisabled`, `OnReleased` | [current/decomp/KSA/GaugeButtonNavBallMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonNavBallMode.cs#L19-L38) | Presence transpiler |
| `VehicleReferenceFrameEx.GetDock2Cci` | [current/decomp/KSA/VehicleReferenceFrameEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleReferenceFrameEx.cs#L381-L385) | Dock frame of the synthesized target |
| `IParentBody.GetCce2Cci`, `IOrbiter.GetPositionEcl(UniverseTime)`, `Astronomical.GetBodyFixed2Ecl`, `GetBodyRates` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L279-L281) | Inputs to the synthesized target |

## Pitfalls and version notes

- **A failed check disables more than targeting.** `Harmony.PatchAll` processes the `[HarmonyPatch]`
  classes one by one and stops at the first exception, so classes patched earlier stay patched. The
  exception leaves `Patcher.Patch` before `RootNavigationTargets.PatchesInstalled = true`, and because it
  is caught in `OnImmediateLoad`, the fonts, JSON data and logo that are loaded after `Patcher.Patch` are
  skipped too ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1235-L1265)). The window then reports that root navigation
  targeting is unavailable.
- **The presence matcher depends on compiler output.** It recognizes only `brtrue`/`brfalse` or
  `ldnull` + `ceq`/`cgt.un` right after the getter. A game build compiled differently (for example a
  pattern match that emits another sequence) changes the count and makes the patch throw.
- **`GetNavigationTarget` returns `object`.** It is only valid where the value is compared with null;
  the matcher guarantees that, and any value use of `Target` still sees the native target only.
- **Private targets.** `UpdateNavBallMarkers`, `UpdateNavballData` and the `IsDisabled` overload are
  resolved by name (and argument type for the overload); a rename silently becomes a patch failure at
  load.
- **What stays disabled.** Rendezvous planning, docking alignment and the native Target Track window
  still need a native orbiting target.[^cmp-readme-md]

## See also

- [Navigation-only root targets](root-navigation-targets.md)
- [Flight-computer attitude tracking against a root target](flight-computer-root-attitude-tracking.md)
- [Closest approach to a root target](root-closest-approach-search.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Transpile NavigationTarget.Create call sites and target-presence checks for the navball](../../../howto/flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/synthesize-a-navigation-target-for-the-flight-computer-2.md) - how-to article demonstrating this feature
- [Match the instruction that consumes the result](../../../howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-4.md) - how-to article demonstrating this feature
- [Wider signature with an extra ldarg.0 appended](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-2.md) - how-to article demonstrating this feature

[^cmp-readme-md]: Compendium: README.md
[^ksa-navigationtarget]: KSA.NavigationTarget (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^cmp-roottargetpatches]: Compendium: src/RootTargetPatches.cs
[^cmp-roottargetflightcomputerpatches]: Compendium: src/RootTargetFlightComputerPatches.cs
[^cmp-rootnavigationtargets]: Compendium: src/RootNavigationTargets.cs
[^ksa-vehiclereferenceframeex]: KSA.VehicleReferenceFrameEx (decompiled, build 2026.10.10.5554)
[^ksa-stellarbody]: KSA.StellarBody (decompiled, build 2026.10.10.5554)
[^ksa-barycenter]: KSA.Barycenter (decompiled, build 2026.10.10.5554)
[^ksa-fixedstar]: KSA.FixedStar (decompiled, build 2026.10.10.5554)
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonnavballmode]: KSA.GaugeButtonNavBallMode (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-gaugebundle]: KSA.GaugeBundle (decompiled, build 2026.10.10.5554)
[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-patcher]: Compendium: src/Patcher.cs
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
