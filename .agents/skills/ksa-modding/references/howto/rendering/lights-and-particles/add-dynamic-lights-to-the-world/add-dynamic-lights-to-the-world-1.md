---
type: How-To
title: Postfix PartTree.UpdateRenderData per craft and submit spot lights with CreateSpotLight
description: Light the road with headlamps and tail lamps by creating Light.CreateSpotLight instances for each craft and viewport in a postfix on PartTree.UpdateRenderData, after the frame's lights were cleared and before they are written to the GPU
category: rendering
group: lights-and-particles
topic: add-dynamic-lights-to-the-world
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/headlights-as-spotlights]
techniques: [harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.PartTree.UpdateRenderData, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:lights, frame:render, frame:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.Program
  - KSA.Part
  - KSA.PowerConsumer
  - KSA.Rendering.Lighting.Light
  - KSA.Rendering.Lighting.ILightSystem
  - KSA.Rendering.Lighting.ELightFlags
  - KSA.IViewport
tags: [lights, spotlight, headlights, per-craft, per-viewport, light-switch]
---
Outcome: real KSA spot lights (lit ground, lit parts, optional soft shadows) attached to a craft, switchable with the part's own light key, for every viewport that draws the craft. KSACars uses it for two warm headlamp beams and a small red tail spill that brightens when braking.

Demonstrated by KSACars ([Headlamps and tail lamps as KSA spotlights](../../../../mods/ksacars/features/headlights-as-spotlights.md)). Prerequisites: StarMap, Harmony. The same technique serves any lamp tied to a vessel's transform.

## Background: how the game does it

KSA empties its whole light list every frame and refills it while it builds render data. `Program.OnFrame` calls `LightSystem.ClearLights()` and `LightSystem.OnFrame(...)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2317-L2318)); `ClusteredLightSystem.ClearLights` empties the clustered list and every viewport's forward list. A light is added with `ILightSystem.CreateLightInstance(Light, IViewport)`:

```csharp
// current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs L897-L934
public void CreateLightInstance(Light inLight, IViewport inViewport)
{
	if (inLight.Range.IsNearlyZero() || inViewport.LightMode == ViewportLightMode.None)
	{
		return;
	}
	NativeList<Light> nativeList = _forwardLightCollections[inViewport.ShaderSlot];
	if ((int)nativeList.Count < 256)
	{
		nativeList.Add(inLight);
	}
	if (inViewport.LightMode != ViewportLightMode.Forward && inViewport.IsMain() && (int)_lightCollection.Count < MaxLights)
	{
		_lightCollection.Add(inLight);
		switch (inLight.Type)
		{
		case ELightType.Point:
			_pointLightCount++;
			break;
		case ELightType.Spot:
			_spotLightCount++;
			break;
		}
	}
}

public void ClearLights()
{
	_pointLightCount = 0;
	_spotLightCount = 0;
	_shaderLightData.Clear();
	_lightCollection.Clear();
	NativeList<Light>[] forwardLightCollections = _forwardLightCollections;
	foreach (NativeList<Light> nativeList in forwardLightCollections)
	{
		nativeList.Clear();
	}
}
```

Lights whose range is zero, or in a viewport with `LightMode.None`, are ignored; each viewport has a forward list (cap 256); only the main viewport also feeds the clustered list (up to `MaxLights`). KSA's own lamps are submitted by `LightModule.UpdateRenderData`, which `PartTree.UpdateRenderData` calls for every light module of the tree, per viewport ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1192), [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L105-L125)). The spot light is built by `Light.CreateSpotLight`, which swaps inner and outer angle if reversed and clamps the outer below 90 degrees:

```csharp
// current/decomp/KSA.Rendering.Lighting/Light.cs L54-L79
public static Light CreateSpotLight(double3 inPosition, double3 inDirection, float inRange, float inOuterAngle, float inInnerAngle, float3 inColor, float inIntensity = 1f, ELightFlags inFlags = ELightFlags.None)
{
	if (inInnerAngle > inOuterAngle)
	{
		float num = inOuterAngle;
		inOuterAngle = inInnerAngle;
		inInnerAngle = num;
	}
	inOuterAngle = float.Clamp(inOuterAngle, 1E-05f, 1.5697963f);
	inInnerAngle = float.Clamp(inInnerAngle, 0f, inOuterAngle);
	return new Light
	{
		Type = ELightType.Spot,
		Flags = inFlags,
		NearPlane = inRange * 0.01f,
		ShadowIndex = uint.MaxValue,
		ShadowTileSize = 0u,
		Position = inPosition,
		Direction = inDirection.Normalized(),
		Range = inRange,
		Color = inColor,
		Intensity = inIntensity,
		OuterAngle = inOuterAngle,
		InnerAngle = inInnerAngle
	};
}
```

The switch is a `<PowerConsumer LightSwitch="true">` module: `Part.LightSwitch` resolves it, `Vehicle.ToggleLights` (the light key) flips every part's `LightIsActive`, and `Part.IsLightSwitchedOff()` is true when the switch is off or unpowered ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1461-L1473), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1396-L1409)).

## Lifecycle and timing

| Moment | Where | Thread |
|---|---|---|
| Lights list emptied | `Program.OnFrame` then `LightSystem.ClearLights()` (`frame:lights`), after the GUI pass and `ImGui.Render` | main |
| Car lamps submitted | `Program.Render` then `UpdateRenderingResources`, per visible viewport, per vehicle: `Vehicle.UpdateRenderData` then `PartTree.UpdateRenderData` then your **postfix** (`frame:render`) | main |
| KSA's own lamps | same `PartTree.UpdateRenderData`, just before your postfix | main |
| Lights written to the GPU | after the vehicle loop: `WriteForwardLights`, `UpdateRenderResources` | main |
| Switch reconciled | `[StarMapAfterGui]` (`frame:draw-ui`) | main |

A light created from `StarMapAfterGui` is cleared before it is used; one created from `StarMapAfterOnFrame` is cleared at the start of the next frame's light pass. The postfix on `PartTree.UpdateRenderData` is the only window that works: after `ClearLights`, with the right `asmb2Ego` and viewport in hand, before the upload. See rows 19 and 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence). `Vehicle.UpdateRenderData` skips a craft too small to render ([keep vessels rendered at any distance](../../meshes/keep-vessels-rendered-at-any-distance/overview.md)), so a distant car's lamps are not submitted either.

## Steps

1. **Give the part a light switch** in its game data, so KSA's light key, its part window and the save agree with your panel:

   ```xml
<!-- src/KSACars/KSACarsGameData.xml L33-L39 -->
<!--
  The lamps' switch: KSA lights the emissive lenses while it is on. Next to nothing is drawn,
  because nothing on a car charges the battery and a flat one puts the lamps out.
-->
<PowerConsumer LightSwitch="true">
  <Consumed W="0.001" />
</PowerConsumer>
```

2. **Install the postfix** from `[StarMapAllModsLoaded]` on the `ref readonly` overload. Reflection needs `typeof(double4x4).MakeByRefType()`, the postfix declares the matrix as `ref`, and the whole body is wrapped so it can never throw into the engine's render preparation:

   ```csharp
// src/KSACars/Ksa/LightsHook.cs L28-L52
public static void Install()
{
    if (Installed) return;

    try
    {
        MethodInfo? target = AccessTools.Method(typeof(PartTree), nameof(PartTree.UpdateRenderData),
            [typeof(double4x4).MakeByRefType(), typeof(bool), typeof(IViewport), typeof(int)]);
        if (target is null)
        {
            Log.Warn("KSA has no PartTree.UpdateRenderData to hook; the cars' headlamps stay dark");
            return;
        }

        MethodInfo postfix = typeof(LightsHook).GetMethod(nameof(AfterRenderData),
                                                          BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        Installed = true;
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the part lights; the cars' headlamps stay dark ({e.Message})");
    }
}
```

   ```csharp
// src/KSACars/Ksa/LightsHook.cs L69-L89
// Inside the engine's render-data pass: nothing here may throw.
private static void AfterRenderData(PartTree __instance, ref double4x4 matrixAsmb2Ego, IViewport viewport)
{
    try
    {
        Buggies.LightLamps(__instance, in matrixAsmb2Ego, viewport);
    }
    catch (Exception e)
    {
        if (_complained) return;
        _complained = true;
        Log.Error("lighting a car's headlamps failed", e);
    }
}

// Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
private static void PinTheSignature(PartTree parts, IViewport viewport)
{
    double4x4 matrix = default;
    parts.UpdateRenderData(in matrix, isEditedVehicle: false, viewport, 0);
}
```

3. **Find the car, then place and submit the lamps.** Match the `PartTree` against each car's `Craft.Parts`; stop if the beam is off or `IsLightSwitchedOff()`. The lamp position is the part-frame position transformed to Ego by the same matrix KSA is drawing the craft with, so the beam sits exactly on the lamp as drawn:

   ```csharp
// src/KSACars/Ksa/Buggies.cs L172-L209
public static void LightLamps(PartTree parts, in double4x4 asmb2Ego, IViewport viewport)
{
    if (Active.Count == 0) return;
    foreach (Entry e in Active.Values)
    {
        if (!ReferenceEquals(e.Craft.Parts, parts)) continue;
        // A flat battery puts the lamps out with the lenses, which KSA darkens itself.
        if (Headlights.Shape(e.Beam) is not { } beam || e.Part.IsLightSwitchedOff()) return;

        doubleQuat part2Asmb = e.Part.Asmb2VehicleAsmb;
        double3 up = part2Asmb * new double3(1, 0, 0);
        double3 forward = part2Asmb * new double3(0, 1, 0);
        Shine(e, e.Drive.Profile.HeadLamps, Headlights.Aim(up, forward, beam.DipRad), beam, Headlights.Colour,
              beam.Intensity, ELightFlags.CastsShadows | ELightFlags.SoftShadows, in asmb2Ego, viewport);

        BeamShape tail = Headlights.Tail;
        bool braking = Headlights.Braking(e.Input.Throttle, e.Drive.ForwardSpeed);
        float brake = braking ? Headlights.BrakeBoost : 1f;
        Shine(e, e.Drive.Profile.TailLamps, Headlights.Aim(up, -forward, tail.DipRad), tail, Headlights.TailColour,
              tail.Intensity * brake, ELightFlags.None, in asmb2Ego, viewport);
        return;
    }
}

private static void Shine(Entry e, double3[] lamps, double3 d, BeamShape beam, float3 colour, float intensity,
                          ELightFlags flags, in double4x4 asmb2Ego, IViewport viewport)
{
    double3 aim = new((d.X * asmb2Ego.M11) + (d.Y * asmb2Ego.M21) + (d.Z * asmb2Ego.M31),
                      (d.X * asmb2Ego.M12) + (d.Y * asmb2Ego.M22) + (d.Z * asmb2Ego.M32),
                      (d.X * asmb2Ego.M13) + (d.Y * asmb2Ego.M23) + (d.Z * asmb2Ego.M33));
    foreach (double3 lamp in lamps)
    {
        double3 at = (e.Part.PositionVehicleAsmb + (e.Part.Asmb2VehicleAsmb * lamp)).Transform(asmb2Ego);
        Program.LightSystem.CreateLightInstance(
            Light.CreateSpotLight(at, aim, beam.Range, beam.OuterAngle, beam.InnerAngle, colour, intensity, flags),
            viewport);
    }
}
```

   Headlamps cast soft shadows (`ELightFlags.CastsShadows | SoftShadows`); tail lamps cast none. Numbers used: dipped 45 m / intensity 60 / cone 0.60 to 1.20 rad / dip 0.14 rad; main 160 m / 400 / 0.14 to 0.40 / 0.02; tail 7 m / 6, times 4 when braking.

4. **Reconcile your own setting with the part switch every frame**, from `[StarMapAfterGui]`. If the switch differs from what it read last frame, KSA moved it and wins; otherwise your beam setting drives the switch:

   ```csharp
// src/KSACars/Sim/Headlights.cs L48-L60
/// <summary>
/// Keeps the panel's setting and the part's own light switch in step, whichever was moved.
/// <paramref name="seen"/> is what the switch read last frame; a switch that differs from it was
/// thrown in KSA's part window, and wins.
/// </summary>
public static (BeamSetting Beam, bool Switch) Reconcile(BeamSetting beam, bool switchNow, bool seen)
{
    if (switchNow != seen)
    {
        return (switchNow ? (beam == BeamSetting.Off ? BeamSetting.Low : beam) : BeamSetting.Off, switchNow);
    }
    return (beam, beam != BeamSetting.Off);
}
```

5. **Pin the signature** so a KSA change breaks the build instead of silently darkening the lamps:

   ```csharp
// src/KSACars/Ksa/LightsHook.cs L84-L89
// Never called: puts the patched method in this assembly's metadata, and so in docs/KSA-API-SURFACE.md.
private static void PinTheSignature(PartTree parts, IViewport viewport)
{
    double4x4 matrix = default;
    parts.UpdateRenderData(in matrix, isEditedVehicle: false, viewport, 0);
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/LightsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/LightsHook.cs) | Installs the postfix on the `ref readonly` overload; never throws; pins the signature |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L168-L209) | `LightLamps` and `Shine`: aim, transform to Ego, `CreateSpotLight`, `CreateLightInstance` |
| [src/KSACars/Sim/Headlights.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Headlights.cs) | Beam shapes, colours, braking boost, `Reconcile`, `Aim` |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L33-L39) | The `<PowerConsumer LightSwitch="true">` |

## Variations and gotchas

- **Too early or too late is invisible.** Lights from any StarMap hook never reach the GPU.
- **Runs per viewport.** With a camera window open the lamps are submitted once for each; `CreateLightInstance` keeps a separate forward list per viewport shader slot.
- **Budget.** At most 256 forward lights per viewport; `WriteForwardLights` keeps only the top 8 by influence for the main (clustered) viewport and 1 to 16 (by pixel count: 8 for a 500 x 500 camera window) for a forward-mode camera window, so most of a large set never reaches a camera window.
- **Angles.** KSA takes both cone angles as the whole cone, edge to edge; `CreateSpotLight` only orders and clamps them.
- **A light is not a glowing lens.** A spotlight placed at the lens lights the bodywork around it as a blob; the lenses glow through the emissive map instead.
- **Battery.** Nothing on a car charges the battery, so the switch draws almost nothing; a flat battery puts the lamps out with the lenses.

## See also

- [Overview](overview.md)
- [Approach 2](add-dynamic-lights-to-the-world-2.md), [approach 3](add-dynamic-lights-to-the-world-3.md)
- [Headlamps and tail lamps as KSA spotlights](../../../../mods/ksacars/features/headlights-as-spotlights.md)
- [Control part lights from code](../control-part-light-color-and-state/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
