---
type: Mod Feature
title: Upright surface teleport for Iron Man kittens by adapting one placement call
description: Iron Man transpiles the one GetInitialKinematicStateForLocation call in Vehicle.TeleportToLocation into an adapter that gives native placement head-up proxy bounds for active Iron Man kittens and converts the attitude and rates back
mod: unscience
submod: iron-man
category: kittens
techniques: [harmony-transpiler]
hooks: ["harmony-transpiler KSA.Vehicle.TeleportToLocation(Celestial, double, double)"]
lifecycle: [frame:draw-ui, frame:input]
game_types:
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.Vehicle.InitialKinematicState
  - KSA.InputEvents
  - KSA.InputEvents.TeleportInputData
  - KSA.Celestial
  - KSA.Universe
  - KSA.Program
tags: [eva, kitten, teleport, surface-placement, terrain-clearance, frames, quaternion, transpiler, harmony]
---

The physics debug window's **Teleport To...** map and its named surface destinations (LC-39A, VSFB,
South Pole, Apollo 11, Mars) drop the controlled vessel onto the surface. KSA's placement assumes a
rocket: body +X points at the sky and clearance is measured below the -X face. A kitten's body frame
is different (+X is the chest, -Z the head, +Z the feet), so a teleported Iron Man kitten would land
on its back with its rocket boots buried. Iron Man corrects this for exactly one case: a live kitten
that is in active Iron Man mode when the teleport is requested. It then lands head-up with its
lowest feet or equipment resting on the terrain.[^iron-man-readme]

Mechanism in one line: a transpiler on `Vehicle.TeleportToLocation` rewrites its one call to the static
`Vehicle.GetInitialKinematicStateForLocation` into a call to a mod adapter that receives the same
eight arguments plus the vehicle, re-expresses the bounds in a head-up proxy frame for the native
helper, and converts the returned attitude and body rates back to the kitten's own frame.

## How it works

**Native path.** `TeleportToLocation(Celestial, lat, lon)` builds mass-centered bounds from
`MassToGeometryAsmb` and the bounding-box half extents, calls
`GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon,
min, max, double3.Zero, Orbit.OrbitLineColor)` and enqueues the result as an
`InputEvents.TeleportInputData` into `InputEvents.TeleportInputBuffer`. The helper orients the body
with `ComputeBody2Cce(radialUp, tangent)` (body +X radially out), samples terrain height at the four
corners of the min-X face, and places the center of mass at
`(center.X - min.X) + MeanRadius + terrain + launch pad height` with the surface rotation velocity.
There is no kitten branch.[^ksa-vehicle]

**Call-site rewrite** ([iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L64-L92)).
`ReplacePlacementCall` copies every instruction; the one that `Calls` the eight-parameter helper
becomes `ldarg.0` (the vehicle `this`) and a new `call PlaceAtLocation` is appended. The eight
original arguments are already on the stack, so the adapter's signature is the helper's plus a
trailing `Vehicle`. Branch labels and opening exception-block markers stay on the `ldarg.0` so jumps
still execute the receiver load; closing exception markers move to the new call. Anything other than
exactly one match throws, and `IronManPatches.Apply` then removes every Iron Man patch.[^iron-man-patches]

**Eligibility** ([iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L37-L44)).
The adapter forwards the untouched arguments to the native helper unless all hold: the patch is
still marked applied, the receiver is a non-disposed `KittenEva`, and
`IronManSubmod.Instance.IsEnabled(kitten)` is true. `IsEnabled` reads a volatile snapshot array of
active kittens, so it is object identity, not vehicle id.[^iron-man-submod] Default EVA kittens,
kittens configured only for editing, ordinary vessels, other placement callers (the launch menu,
editor launch, situation spawning) and every other `Vehicle.Teleport` path keep stock behaviour.

**Head-up proxy bounds.** `IronManOrientation.RocketCtrl2Body` is a +90 degree rotation about Y that
maps rocket +X (nose) to kitten -Z (head), +Y to +Y and +Z to +X (face).[^iron-man-orientation]
Its inverse is an exact signed axis permutation, so the adapter applies it to the bounds component
by component instead of rotating an axis-aligned box:

| Native input | Proxy value passed to the helper | Effect |
|---|---|---|
| `boundsMin` | `(-max.Z, min.Y, min.X)` | Proxy min-X face is the kitten's +Z (feet and boots) plane |
| `boundsMax` | `(-min.Z, max.Y, max.X)` | Full body plus equipment still enclosed |
| `centerMass` | `(-center.Z, center.Y, center.X)` | Clearance becomes `max.Z - center.Z` |

The native helper therefore samples terrain under the feet footprint and lifts the center of mass by
the real feet-plus-equipment depth, with launch-pad elevation and surface velocity untouched.

**Back to the EVA frame.** The helper returns a proxy-frame state. The adapter keeps its `Orbit`
object and converts only attitude and rates:
`Body2Cce = Concatenate(Inverse(Q), native.Body2Cce)` (body to proxy, then proxy to CCE) and
`BodyRates = Transform(native.BodyRates, Q)`, so the head points along the native surface-up axis and
the world-space angular velocity of the rotating planet is preserved. An explicitly selected control
part does not change the physical placement.[^plan-surface-teleport]

## Lifecycle and timing

The request and the application happen in different frames and different phases:[^ksa-program][^ksa-input-events]

| Step | Frame phase | Call chain (build 5554) |
|---|---|---|
| 1. Button or map double-click | `frame:draw-ui`, frame N | `Program.OnDrawUiThreadSafe` (L2272) -> `Universe.OnDrawUi(MainViewport)` (L3231) -> `CelestialSystem.OnDrawUi` -> `Vehicle.OnDrawUi` -> `DrawPhysicsDebugWindow` (only for `Program.ControlledVehicle` with `Program.ShowPhysicsDebug`) |
| 2. Placement (patched) | `frame:draw-ui`, frame N | `Vehicle.TeleportToLocation` -> `PlaceAtLocation` adapter -> native helper; result added to `TeleportInputBuffer` |
| 3. Application | `frame:input`, frame N+1 | `Program.PrepareFrame` L2181 `InputEvents.ApplyInputEvents` -> `TeleportInputBuffer.ApplyAll` -> `TeleportInputData.Apply` -> `Vehicle.Teleport(orbit, body2Cce, bodyRates)` |
| 4. Iron Man mode switches | `frame:prepare`, frame N+1 | Queued with `PhysicsFrameHook.Enqueue` from the panel and run at the `Universe.GetJobSimStep` handoff (L2216), after step 3 |

What this implies:

- **Request-time mode isolation.** Eligibility is decided in step 2 and baked into the queued
  `TeleportInputData`. A mode switch clicked in the same frame runs in step 4, after the teleport was
  applied with the old mode's pose, and nothing rewrites an already queued request. The README tells
  players to wait for a pending switch before teleporting.[^iron-man-readme]
- **Why the request site, not `Vehicle.Teleport`.** `Teleport` receives only an orbit and a
  quaternion; the bounds that decide clearance exist only at the `TeleportToLocation` call. Patching
  there also leaves Garry's Torch, Dent Wizard and every other `Teleport` caller untouched.
- **Safety of the mutation.** Step 3 runs on the main thread after `Program.PrepareFrame` has waited
  for all solvers and applied their results and before the next step is dispatched, which is the
  stock teleport's own timing; the mod adds nothing there.
- **Not HUD-gated by the mod.** The patch is passive; it fires only when the stock debug window does,
  which itself requires the HUD to be visible.

## Key code

| Path | Role |
|---|---|
| [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs) | Typed target lookups, one-site transpiler, eligibility gate, bounds permutation, frame and rate conversion |
| [iron-man.lib/IronManOrientation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManOrientation.cs) | `RocketCtrl2Body` shared with the control-frame and RCS patches |
| [iron-man.lib/IronManPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManPatches.cs#L22) | Transactional install (last in the list) and removal (first) |
| [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs#L23-L27) | `IsEnabled` snapshot read |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L96) | Supermod applies `IronManPatches` |
| [iron-man-flight.tests/TeleportChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/TeleportChecks.cs) | Managed checks: all eight corners clear, four terrain probes on the feet plane, rates, queue, isolation, IL metadata |
| [iron-man-flight.tests/TeleportFixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/TeleportFixture.cs) | Native-free `Vehicle.TeleportToLocation` / helper stand-ins the production transpiler patches |
| [plans/iron-man/SURFACE_TELEPORT.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/plans/iron-man/SURFACE_TELEPORT.md) | Authors' derivation of the permutation and queue timing |

## Snippets

The adapter that replaces the native placement call:

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

The call-site swap with exception-block bookkeeping:

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

The patched game method: placement is computed at request time and only queued (build 5554):

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

Why the proxy bounds work: the native helper's min-X footprint and clearance (build 5554):

```csharp
// current/decomp/KSA/Vehicle.cs L4149-L4184
		double num = double.NegativeInfinity;
		for (int i = 0; i < 2; i++)
		{
			for (int j = 0; j < 2; j++)
			{
				double3 value2 = new double3(boundsMinAsmb.X, (i == 0) ? boundsMinAsmb.Y : boundsMaxAsmb.Y, (j == 0) ? boundsMinAsmb.Z : boundsMaxAsmb.Z) - centerMassAsmb;
				double3 positionDirCcf = (celestial.MeanRadius * dirCcfFromLatLon + value2.Transform(rotation)).Normalized();
				num = double.Max(num, celestial.GetTerrainHeightFromDirCcf(positionDirCcf));
			}
		}
		// ...
		double num2 = centerMassAsmb.X - boundsMinAsmb.X;
		double num3 = num2 + celestial.MeanRadius + num + GetLaunchPadHeightAtDirCcf(celestial, dirCcfFromLatLon);
		double3 double6 = (num3 * dirCcfFromLatLon).Transform(ccf2Cci);
		double3 velocityCci = double3.Cross(angularVelocityCci, double6);
		Orbit orbit = KSA.Orbit.CreateFromStateCci(celestial, time, double6, velocityCci, orbitLineColor);
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Vehicle.TeleportToLocation(Celestial, double, double)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233) | Transpiler target (resolved with `AccessTools.DeclaredMethod` and an exact parameter list) |
| `Vehicle.GetInitialKinematicStateForLocation(...)` (static, 8 parameters) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4135-L4191) | Native placement reused with proxy bounds; IL anchor |
| `Vehicle.InitialKinematicState` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L148-L155) | Mutable `Orbit` / `Body2Cce` / `BodyRates` result |
| `Vehicle.ComputeBody2Cce(double3, double3)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3195-L3199) | Why native placement points body +X skyward |
| `Vehicle.DrawPhysicsDebugWindow` / `Vehicle.OnDrawUi` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3885-L3888) | The stock callers (named buttons L4004-L4026, map double-click L4088) |
| `Vehicle.Teleport(Orbit?, doubleQuat?, double3?)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2241) | Applies the queued pose; not patched |
| `InputEvents.TeleportInputData.Apply`, `TeleportInputBuffer` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L407-L423) | Deferred application (`ApplyAll` at L1138) |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2179-L2216) | Input application (L2181) before the mod's mode-switch handoff (L2216) |
| `CelestialSystem.OnDrawUi` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L364) | Per-vehicle UI dispatch in the draw-UI phase |
| `Universe.GetNextSimStep` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2387) | Placement time passed through unchanged |

## Pitfalls and version notes

- The guard requires exactly one helper call in `TeleportToLocation`. KSA adding a second call (or
  inlining the helper) fails installation, and because `IronManPatches.Apply` is transactional the
  whole Iron Man integration is then reported unavailable rather than half-installed.[^iron-man-patches]
- Both lookups are typed (`nameof` plus explicit parameter arrays), so a renamed member fails at
  `Apply`; a reordered parameter list would also fail the lookup instead of mis-binding the stack.
- When transpiling a call into "push extra argument, call adapter", move labels and
  exception-block starts onto the first inserted instruction and block ends onto the last. The managed
  checks assert this bookkeeping and that the original `CodeInstruction` is not mutated.[^teleport-checks]
- The `_applied` flag makes the adapter fall back to native placement if it is ever reached after
  `Remove`.
- The correction only covers placement. Attitude holds or autopilot commands active on the kitten may
  turn it afterwards, and the native helper's four-corner terrain sampling is still a coarse estimate
  on slopes.[^plan-surface-teleport]
- Native acceptance with an equipped kitten on terrain and on a launch pad was still open when the
  authors wrote their notes.[^scope-iron-man]

## See also

- [Fly one EVA kitten as an ordinary rocket vessel](./iron-man-kitten-rocket-flight.md) (the mode this gates on and the shared `RocketCtrl2Body`)
- [Open the stock vehicle editor on a live EVA kitten](./iron-man-kitten-vehicle-editing.md)
- [Physics frame hook](./lib-physics-frame-hook.md) (where mode switches run)
- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md) (a mod-driven `Vehicle.Teleport` caller left untouched)
- [Iron Man sub-mod](../submods/iron-man.md) and [mod overview](../overview.md)
- [Adapter that fixes up arguments and converts the result back](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-5.md) - how-to article demonstrating this feature
- [Transpile the GetInitialKinematicStateForLocation call for head-up bounds](../../../howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-2.md) - how-to article demonstrating this feature

[^iron-man-readme]: iron-man/README.md
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^iron-man-patches]: iron-man.lib/IronManPatches.cs
[^iron-man-submod]: iron-man.lib/IronManSubmod.cs
[^iron-man-orientation]: iron-man.lib/IronManOrientation.cs
[^plan-surface-teleport]: plans/iron-man/SURFACE_TELEPORT.md (authors' design note, 5402)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-input-events]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^teleport-checks]: iron-man-flight.tests/TeleportChecks.cs
[^scope-iron-man]: scope/iron-man.md (authors' hook inventory, build 5482)
