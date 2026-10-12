---
type: How-To
title: Transpile the GetInitialKinematicStateForLocation call for head-up bounds
description: Rewrite the one call to Vehicle.GetInitialKinematicStateForLocation inside TeleportToLocation so a kitten, whose body frame has +X as the chest, lands head-up with its feet on the terrain, by passing head-up proxy bounds and converting the result back
category: physics
group: kinematics
topic: place-a-vessel-on-a-surface-at-latitude-and-longitude
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/iron-man-surface-teleport-correction]
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.Vehicle.TeleportToLocation]
lifecycle: [frame:draw-ui, frame:input, frame:prepare]
game_types: [KSA.Vehicle, KSA.KittenEva, KSA.InputEvents, KSA.Celestial]
tags: [physics, kinematics, teleport, surface, transpiler, kitten, bounds, clearance, unscience]
---

Outcome: a kitten that is in an Iron Man mode (head-up flight) is teleported to a destination in the physics debug window and lands
head-up with its lowest feet or equipment resting on the terrain, instead of on its back with its boots buried. KSA's placement
assumes a rocket: body +X points at the sky and clearance is measured below the -X face. Prerequisites: StarMap and Harmony.

## Background: how the game does it

`TeleportToLocation` builds mass-centred bounds, calls the static `GetInitialKinematicStateForLocation` with eight arguments,
and queues the result:

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

The helper orients the body with `ComputeBody2Cce(radialUp, tangent)` (body +X radially out), samples terrain at the **four corners of
the min-X face**, and puts the centre of mass `(centre.X - min.X)` above the highest of them plus the launch pad height. There is no
kitten branch:

```csharp
// current/decomp/KSA/Vehicle.cs L4135-L4191
	public static InitialKinematicState GetInitialKinematicStateForLocation(Celestial celestial, UniverseTime time, double latitude, double longitude, double3 boundsMinAsmb, double3 boundsMaxAsmb, double3 centerMassAsmb, byte4 orbitLineColor)
	{
		doubleQuat ccf2Cci = celestial.GetCcf2Cci(time);
		doubleQuat cci2Cce = celestial.GetCci2Cce();
		double3 angularVelocityCci = ((IParentBody)celestial).GetAngularVelocityCci();
		double3 dirCcfFromLatLon = celestial.GetDirCcfFromLatLon(latitude, longitude);
		double3 a = double3.UnitZ.Transform(cci2Cce);
		double3 value = angularVelocityCci.Transform(cci2Cce);
		doubleQuat doubleQuat2 = doubleQuat.Concatenate(ccf2Cci, cci2Cce);
		double3 double5 = dirCcfFromLatLon.Transform(doubleQuat2);
		double3 upCce = double3.Cross(double3.Cross(a, double5), double5).Normalized();
		doubleQuat doubleQuat3 = ComputeBody2Cce(double5, upCce);
		double3 bodyRates = value.Transform(doubleQuat3.Inverse());
		doubleQuat rotation = doubleQuat.Concatenate(doubleQuat3, doubleQuat.Inverse(doubleQuat2));
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
		if (!double.IsFinite(num))
		{
			LogCategory log = DefaultCategory.Log;
			LogCategory logCategory = log;
			bool shouldAppend;
			LogString<Error> message = new LogString<Error>(75, 4, log, out shouldAppend);
			if (shouldAppend)
			{
				message.AppendLiteral("terrain height at ");
				message.AppendFormatted(latitude, 0, "F4");
				message.AppendLiteral(", ");
				message.AppendFormatted(longitude, 0, "F4");
				message.AppendLiteral(" on '");
				message.AppendFormatted(celestial.Id);
				message.AppendLiteral("' sampled as ");
				message.AppendFormatted(num);
				message.AppendLiteral(" - placing at the mean radius instead");
			}
			logCategory.Error(message, "GetInitialKinematicStateForLocation", "C:\\prototype-planet\\KSA\\Vehicle.cs", 4247);
			num = 0.0;
		}
		double num2 = centerMassAsmb.X - boundsMinAsmb.X;
		double num3 = num2 + celestial.MeanRadius + num + GetLaunchPadHeightAtDirCcf(celestial, dirCcfFromLatLon);
		double3 double6 = (num3 * dirCcfFromLatLon).Transform(ccf2Cci);
		double3 velocityCci = double3.Cross(angularVelocityCci, double6);
		Orbit orbit = KSA.Orbit.CreateFromStateCci(celestial, time, double6, velocityCci, orbitLineColor);
		return new InitialKinematicState
		{
			Orbit = orbit,
			Body2Cce = doubleQuat3,
			BodyRates = bodyRates
		};
	}
```

A kitten's body frame is different: +X is the chest, -Z the head, +Z the feet.

## Lifecycle and timing

| Step | Frame phase | Call chain |
|---|---|---|
| 1. Button or map double-click | `frame:draw-ui`, frame N | `Program.OnDrawUiThreadSafe` -> `Universe.OnDrawUi` -> `Vehicle.OnDrawUi` -> physics debug window |
| 2. Placement (patched) | `frame:draw-ui`, frame N | `TeleportToLocation` -> adapter -> native helper; result added to `TeleportInputBuffer` |
| 3. Application | `frame:input`, frame N+1 | `PrepareFrame` -> `ApplyInputEvents` -> `TeleportInputData.Apply` -> `Vehicle.Teleport(orbit, body2Cce, bodyRates)` |
| 4. Iron Man mode switches | `frame:prepare`, frame N+1 | Queued from the panel and run at the `GetJobSimStep` handoff, after step 3 |

Eligibility is decided in step 2 and baked into the queued request, so a mode switch clicked in the same frame runs in step 4,
after the teleport was applied with the old mode's pose. Why the request site and not `Vehicle.Teleport`: `Teleport`
receives only an orbit and a quaternion; the bounds that decide clearance exist only at the `TeleportToLocation` call, and patching
there leaves every other `Teleport` caller untouched. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Transpile `Vehicle.TeleportToLocation`: copy every instruction; the one that `Calls` the eight-parameter helper becomes
   `ldarg.0` (the vehicle) and a new `call PlaceAtLocation` is appended. The eight original arguments are already on the stack,
   so the adapter's signature is the helper's plus a trailing `Vehicle`. Move branch labels and opening exception markers onto the
   `ldarg.0` and closing markers onto the new call. Throw unless exactly one call was replaced:

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

2. In the adapter, forward to the native helper unchanged unless the receiver is a live `KittenEva` in the mode. Otherwise
   express the bounds in head-up proxy axes with an exact signed permutation (not a general rotation, which would round the
   axis-aligned bounds), call the native helper, and convert only attitude and rates back to the kitten's frame:

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

   The mapping is `min' = (-max.Z, min.Y, min.X)`, `max' = (-min.Z, max.Y, max.X)`, `centre' = (-centre.Z, centre.Y, centre.X)`,
   so the proxy min-X face is the kitten's +Z (feet and boots) plane and the helper lifts the centre of mass by the real
   feet-plus-equipment depth, with launch-pad elevation and surface velocity untouched.
3. Use object identity for eligibility (a volatile snapshot of active kittens), not the vehicle id.
4. Make installation transactional: if the match fails, remove every patch of the feature and report it unavailable rather than half-installed.

## Complete example

| File | Role |
|---|---|
| [iron-man.lib/IronManTeleportPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManTeleportPatches.cs#L37-L92) | Adapter and the call-site rewrite |
| [iron-man.lib/IronManOrientation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManOrientation.cs) | The rocket-to-kitten rotation |
| [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs) | Mode snapshot used for eligibility |

## Variations and gotchas

- Exactly one helper call must exist in `TeleportToLocation`; a second call or an inlined helper fails installation.
- Both lookups are typed (`nameof` plus explicit parameter arrays), so a renamed or reordered member fails at apply.
- The correction covers placement only; attitude holds or autopilot commands active on the kitten may turn it afterwards.
- The helper's four-corner terrain sampling is a coarse estimate on slopes.
- Native acceptance with an equipped kitten on terrain and on a launch pad was still open when the authors wrote their notes.

## See also

- [Overview and comparison](overview.md)
- [Queue TeleportToLocation once per action](place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md)
- Mod feature: [Iron Man surface teleport correction](../../../../mods/unscience/features/iron-man-surface-teleport-correction.md)
- [Teleport a vessel by building a new orbit](../teleport-a-vessel-by-building-a-new-orbit/overview.md)
