---
type: How-To
title: Adapter that fixes up arguments and converts the result back
description: Turn the one GetInitialKinematicStateForLocation call in Vehicle.TeleportToLocation into an adapter that receives the eight original arguments plus the vehicle, substitutes head-up proxy bounds for an active Iron Man kitten, calls the native helper and converts attitude and rates back
category: patching
group: transpilers
topic: swap-one-call-inside-a-game-method-with-a-transpiler
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/iron-man-surface-teleport-correction]
techniques: [harmony-transpiler, call-site-rewrite, adapter]
hooks: [harmony-transpiler KSA.Vehicle.TeleportToLocation]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:input]
game_types: [KSA.Vehicle, KSA.KittenEva, KSA.InputEvents]
tags: [harmony, transpiler, call-site, adapter, teleport, arguments, result-conversion]
---

Some calls need their inputs translated before and their outputs translated after. KSA's surface placement assumes a rocket: body +X points at the sky and
clearance is measured below the -X face. A kitten's body frame differs (+X is the chest, -Z the head), so an Iron Man kitten teleported with stock logic would
land on its back. The unscience mod rewrites the single call to the static `Vehicle.GetInitialKinematicStateForLocation` inside `Vehicle.TeleportToLocation`
into an adapter that takes the same eight arguments plus the vehicle, re-expresses the bounds in a head-up proxy frame for the native helper, and converts
the returned attitude and body rates back. Every other caller and every other kitten keeps stock behaviour.

Prerequisites: a StarMap mod with `0Harmony` and the swaps from [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md) and [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md). Demonstrated by unscience's Iron Man in
[Upright surface teleport for Iron Man kittens by adapting one placement call](../../../../mods/unscience/features/iron-man-surface-teleport-correction.md).

## Background: how the game does it

`TeleportToLocation(Celestial, lat, lon)` builds mass-centred bounds from `MassToGeometryAsmb` and the bounding-box half extents, calls the static helper with
eight arguments, and enqueues the result as an `InputEvents.TeleportInputData` into `InputEvents.TeleportInputBuffer`. There is no kitten branch:

```csharp
// current/decomp/KSA/Vehicle.cs L4219-L4233
public void TeleportToLocation(Celestial celestial, double lat, double lon)
{
	double3 double5 = new double3(_props.BoundingBoxAsmb.HalfWidth, _props.BoundingBoxAsmb.HalfHeight, _props.BoundingBoxAsmb.HalfLength);
	double3 boundsMinAsmb = MassToGeometryAsmb - double5;
	double3 boundsMaxAsmb = MassToGeometryAsmb + double5;
	InitialKinematicState initialKinematicStateForLocation = GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon, boundsMinAsmb, boundsMaxAsmb, double3.Zero, Orbit.OrbitLineColor);
	InputEvents.TeleportInputData data = new InputEvents.TeleportInputData
	{
		Vehicle = this,
		Orbit = initialKinematicStateForLocation.Orbit,
		Body2Cce = initialKinematicStateForLocation.Body2Cce,
		BodyRates = initialKinematicStateForLocation.BodyRates
	};
	InputEvents.TeleportInputBuffer.Add(data);
}
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233)[^ksa-vehicle]

```csharp
// current/decomp/KSA/Vehicle.cs L4135
public static InitialKinematicState GetInitialKinematicStateForLocation(Celestial celestial, UniverseTime time, double latitude, double longitude, double3 boundsMinAsmb, double3 boundsMaxAsmb, double3 centerMassAsmb, byte4 orbitLineColor)
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4135)

The IL of the call. Eight values are on the stack when the `call` executes (`celestial` is `ldarg.1`, then the time, `lat`, `lon`, the two bounds,
`double3.Zero`, the orbit line colour); `this` (`ldarg.0`) is not among them, which is why the transpiler appends it:

```text
// KSA.dll IL via ilspycmd -il: KSA.Vehicle::TeleportToLocation (parameter lists shortened)
IL_0055: ldarg.1
IL_0056: call valuetype KSA.SimStep KSA.Universe::GetNextSimStep(...)
IL_005b: stloc.s 5
IL_005d: ldloca.s 5
IL_005f: call instance valuetype KSA.UniverseTime KSA.SimStep::get_NextTime(...)
IL_0064: ldarg.2
IL_0065: ldarg.3
IL_0066: ldloc.1
IL_0067: ldloc.2
IL_0068: call valuetype [Brutal.Core.Numerics]Brutal.Numerics.double3 [Brutal.Core.Numerics]Brutal.Numerics.double3::get_Zero(...)
IL_006d: ldarg.0
IL_006e: call instance class KSA.Orbit KSA.Vehicle::get_Orbit(...)
IL_0073: ldfld valuetype [Brutal.Core.Numerics]Brutal.Numerics.byte4 KSA.Orbit::OrbitLineColor
IL_0078: call valuetype KSA.Vehicle/InitialKinematicState KSA.Vehicle::GetInitialKinematicStateForLocation(...)
IL_007d: stloc.3
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

The request and the application happen in different frames and phases:

| Step | Phase | Call chain (build 5554) |
|---|---|---|
| 1. Button or map double-click in the physics debug window | `frame:draw-ui`, frame N | `Program.OnDrawUiThreadSafe` then `Universe.OnDrawUi` then `Vehicle.OnDrawUi` then the debug window (controlled vehicle only) |
| 2. Placement (patched) | `frame:draw-ui`, frame N | `Vehicle.TeleportToLocation` then the adapter then the native helper; result added to `TeleportInputBuffer` |
| 3. Application | `frame:input`, frame N+1 | `InputEvents.ApplyInputEvents` then `TeleportInputData.Apply` then `Vehicle.Teleport(orbit, body2Cce, bodyRates)` |
| 4. Iron Man mode switches | `frame:prepare`, frame N+1 | queued with `PhysicsFrameHook.Enqueue`, run at the `GetJobSimStep` handoff after step 3 |

Eligibility is decided in step 2 and baked into the queued request. Step 3 runs on the main thread after `PrepareFrame` has waited for all solvers and applied their
results, which is the stock teleport's own timing; the mod adds nothing there. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Write the adapter with the helper's eight parameters plus the vehicle.** It forwards untouched arguments to the native helper unless all of these hold: the patch
   is still marked applied, the receiver is a non-disposed `KittenEva`, and Iron Man is enabled for that kitten (a volatile snapshot, so by object identity). Otherwise
   stock behaviour is preserved exactly. For an eligible kitten it applies an exact signed axis permutation to the bounds (no general rotation of an axis-aligned box),
   calls the helper, and converts only attitude and rates back:

```csharp
// iron-man.lib/IronManTeleportPatches.cs L37-L62
private static Vehicle.InitialKinematicState PlaceAtLocation(Celestial celestial, UniverseTime time,
    double latitude, double longitude, double3 boundsMinAsmb, double3 boundsMaxAsmb,
    double3 centerMassAsmb, byte4 orbitLineColor, Vehicle vehicle)
{
    if (!_applied || vehicle is not KittenEva kitten || kitten.IsDisposed
        || IronManSubmod.Instance?.IsEnabled(kitten) != true)
        return Vehicle.GetInitialKinematicStateForLocation(celestial, time, latitude, longitude,
            boundsMinAsmb, boundsMaxAsmb, centerMassAsmb, orbitLineColor);

    // Native placement assumes +X is the nose and samples the minimum-X footprint.
    // Express the complete bounds in that convention first, so both terrain sampling
    // and the height of boots/accessories are correct. This exact signed permutation
    // avoids rounding the axis-aligned bounds with a general quaternion rotation.
    var min = new double3(-boundsMaxAsmb.Z, boundsMinAsmb.Y, boundsMinAsmb.X);
    var max = new double3(-boundsMinAsmb.Z, boundsMaxAsmb.Y, boundsMaxAsmb.X);
    var center = new double3(-centerMassAsmb.Z, centerMassAsmb.Y, centerMassAsmb.X);
    var state = Vehicle.GetInitialKinematicStateForLocation(celestial, time, latitude, longitude,
        min, max, center, orbitLineColor);

    // The orbit already includes correct clearance, launchpad elevation and surface
    // velocity. Convert only the proxy orientation/body-rate coordinates back to EVA.
    var rocketToBody = IronManOrientation.RocketCtrl2Body;
    state.Body2Cce = doubleQuat.Concatenate(doubleQuat.Inverse(rocketToBody), state.Body2Cce);
    state.BodyRates = double3.Transform(state.BodyRates, rocketToBody);
    return state;
}
```

Source: [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L37-L62)[^unscience-ironmanteleportpatches]

   The proxy bounds map the kitten's feet plane to the helper's min-X face:

   | Native input | Proxy value passed to the helper |
   |---|---|
   | `boundsMin` | `(-max.Z, min.Y, min.X)` |
   | `boundsMax` | `(-min.Z, max.Y, max.X)` |
   | `centerMass` | `(-center.Z, center.Y, center.X)` |

2. **Rewrite the call into `ldarg.0` plus the adapter call.** `ReplacePlacementCall` copies every instruction (`new CodeInstruction(instruction)`), turns the match into
   `ldarg.0` (the vehicle) and appends a new `call PlaceAtLocation`. Branch labels and opening exception markers stay on the `ldarg.0`; closing exception markers move to the new
   call. Anything other than exactly one match throws, and `Apply` removes every Iron Man patch:

```csharp
// iron-man.lib/IronManTeleportPatches.cs L64-L92
internal static IEnumerable<CodeInstruction> ReplacePlacementCall(IEnumerable<CodeInstruction> instructions)
{
    var result = new List<CodeInstruction>();
    MethodInfo placement = Placement();
    int replaced = 0;
    foreach (var instruction in instructions)
    {
        var code = new CodeInstruction(instruction);
        if (code.Calls(placement))
        {
            // Append this after the original eight arguments. Branch labels and opening
            // exception boundaries must enter before this new receiver load; an ending
            // boundary still follows the actual call.
            code.opcode = OpCodes.Ldarg_0;
            code.operand = null;
            var call = new CodeInstruction(OpCodes.Call, Own(nameof(PlaceAtLocation)));
            foreach (var block in code.blocks)
                if (block.blockType == ExceptionBlockType.EndExceptionBlock) call.blocks.Add(block);
            code.blocks.RemoveAll(block => block.blockType == ExceptionBlockType.EndExceptionBlock);
            result.Add(code);
            result.Add(call);
            replaced++;
        }
        else result.Add(code);
    }
    if (replaced != 1)
        throw new InvalidOperationException($"Iron Man expected one surface placement call, found {replaced}.");
    return result;
}
```

Source: [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L64-L92)

3. **Resolve both ends with exact parameter arrays.** A renamed member or a reordered parameter list fails the lookup instead of mis-binding the stack:

```csharp
// iron-man.lib/IronManTeleportPatches.cs L94-L101
private static MethodInfo Target() => AccessTools.DeclaredMethod(typeof(Vehicle), nameof(Vehicle.TeleportToLocation),
    new[] { typeof(Celestial), typeof(double), typeof(double) })
    ?? throw new MissingMethodException(typeof(Vehicle).FullName, nameof(Vehicle.TeleportToLocation));

private static MethodInfo Placement() => AccessTools.DeclaredMethod(typeof(Vehicle), nameof(Vehicle.GetInitialKinematicStateForLocation),
    new[] { typeof(Celestial), typeof(UniverseTime), typeof(double), typeof(double),
        typeof(double3), typeof(double3), typeof(double3), typeof(byte4) })
    ?? throw new MissingMethodException(typeof(Vehicle).FullName, nameof(Vehicle.GetInitialKinematicStateForLocation));
```

Source: [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L94-L101)

4. **Install transactionally.** `Apply` patches the transpiler, sets `_applied`, and on any failure calls `Remove` and rethrows; the `_applied` flag also makes the adapter fall
   back to native placement if it is ever reached after `Remove`:

```csharp
// iron-man.lib/IronManTeleportPatches.cs L16-L35
public static void Apply(Harmony harmony)
{
    if (_applied) return;
    try
    {
        harmony.Patch(Target(), transpiler: new HarmonyMethod(Own(nameof(ReplacePlacementCall))));
        _applied = true;
    }
    catch
    {
        Remove(harmony);
        throw;
    }
}

public static void Remove(Harmony harmony)
{
    _applied = false;
    harmony.Unpatch(Target(), Own(nameof(ReplacePlacementCall)));
}
```

Source: [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L16-L35)

5. **Test the adapter.** The mod's managed checks assert the signed axis permutation of the bounds, that the original bounds are not mutated, and the transpiler's
   label bookkeeping ([iron-man-flight.tests/TeleportChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/TeleportChecks.cs#L105-L125)[^unscience-teleportchecks]).

## Complete example

| File | Role |
|---|---|
| [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs) | Adapter, transpiler, lookups, transactional install |
| [iron-man-flight.tests/TeleportChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/TeleportChecks.cs) | Checks for the rewrite |

## Variations and gotchas

- **Why this call and not `Vehicle.Teleport`.** `Teleport` receives only an orbit and a quaternion; the bounds that decide clearance exist only at the
  `TeleportToLocation` call. Patching there leaves every other `Teleport` caller untouched.
- **Request-time isolation.** Eligibility is decided at request time and baked into the queued data; a mode switch clicked in the same frame runs after the
  teleport was applied with the old mode's pose.
- **The guard requires exactly one helper call.** KSA adding a second call (or inlining the helper) fails installation; the transactional `Apply` then reports the whole
  Iron Man integration unavailable rather than half-installed.
- **Pattern.** "Push extra argument, call adapter": move labels and exception-block starts onto the first inserted instruction and block ends onto the last.
- **Coarse on slopes.** The native helper's four-corner terrain sampling is still a coarse estimate; attitude holds active on the kitten may turn it afterwards.

## See also

- [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md), [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md), [Replacement that does mod work, then calls the original](swap-one-call-inside-a-game-method-with-a-transpiler-3.md), [Gate helper that skips the call for the mod's own condition](swap-one-call-inside-a-game-method-with-a-transpiler-4.md)
- [Swapping one call inside a game method with a transpiler](overview.md)
- [Upright surface teleport for Iron Man kittens by adapting one placement call](../../../../mods/unscience/features/iron-man-surface-teleport-correction.md)
- [Move labels and begin-exception markers onto the inserted instruction](../insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md)
- Related task: synthesize-a-navigation-target-for-the-flight-computer

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^unscience-ironmanteleportpatches]: unscience/iron-man.lib/IronManTeleportPatches.cs
[^unscience-teleportchecks]: unscience/iron-man-flight.tests/TeleportChecks.cs
