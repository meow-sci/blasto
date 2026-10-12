---
type: How-To
title: Clone the shared light template per instance and set cone angles
description: Give one light its own LightModule.TemplateData copy before editing brightness, colour or spotlight cone angles, and lower the inner angle with the outer because CreateSpotLight swaps them
category: rendering
group: lights-and-particles
topic: control-part-light-color-and-state
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/light-cone-control]
techniques: [template-unsharing, reflection]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types: [KSA.LightModule, KSA.PowerConsumer, KSA.Vehicle, KSA.Rendering.Lighting.Light, KSA.FloatReference]
tags: [lights, spotlight, cone-angle, template-clone, per-instance]
---
Outcome: change one light's brightness, colour and spotlight cone angles without touching any other light of the same part type, and make a narrow beam actually narrow. The shared template is cloned for the one light being edited; the angle writes follow the engine's swap-and-clamp rules.

Demonstrated by gatOS ([Part light cone control](../../../../mods/gatos/features/light-cone-control.md)), which exposes `lights/<n>/{on,brightness,color,inner_angle,outer_angle}` per vessel. Prerequisites: StarMap (or any main-thread command drain). It builds on [approach 1](control-part-light-color-and-state-1.md).

## Background: how the game does it

`LightModule.Template` is a public, writable reference to a `TemplateData` that is by default shared by every `LightModule` of the part type. `LightModule.UpdateRenderData` builds the light from it every frame:

```csharp
// current/decomp/KSA/LightModule.cs L123-L138
case TemplateData.LightType.Spot:
{
	doubleQuat rotationValue = Template.Transform.RotationValue;
	double3 double5 = double3.UnitX.Transform(rotationValue);
	double5 = double3.Normalize(new double3(double5.X * matrix.M11 + double5.Y * matrix.M21 + double5.Z * matrix.M31, double5.X * matrix.M12 + double5.Y * matrix.M22 + double5.Z * matrix.M32, double5.X * matrix.M13 + double5.Y * matrix.M23 + double5.Z * matrix.M33));
	Light light = Light.CreateSpotLight(Template.Transform.PositionValue.Transform(matrix), double5, Template.Range, Template.OuterAngle, Template.InnerAngle, Template.ColorRgb, Template.Intensity, ELightFlags.CastsShadows | ELightFlags.SoftShadows);
	if (Program.Instance.IsRaytracingActive && GameSettings.Current.Graphics.IVARayTracing && Template.RayTracing && viewport.HasAll(ViewportOptionFlags.UseRaytracing))
	{
		SpotLightsRayTrace.Add(light);
	}
	else
	{
		Program.LightSystem.CreateLightInstance(light, viewport);
	}
	break;
}
```

`Light.CreateSpotLight` first **swaps** inner and outer if inner is larger, then clamps outer to `[1e-5, 1.5697963]` radians and inner to `[0, outer]`:

```csharp
// current/decomp/KSA.Rendering.Lighting/Light.cs L54-L66
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
```

Writing only the outer angle below an untouched inner angle (22.5 degrees on the standard spotlight per gatOS' analysis) makes the swap restore the wide cone. The light pre-pass spot falloff divides by `max(innerAngle - outerAngle, SPOT_DENOM_EPSILON)` where both are the cosines `Light.CreateLightData` writes ([current/Content/Core/Shaders/Lighting/LightPrePass.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Lighting/LightPrePass.comp#L324-L331), `SPOT_DENOM_EPSILON` = 1e-4); the analysis computes that below about 0.81 degrees half-angle the beam dims rather than narrows, so very tight cones need more intensity. Spotlights only light surfaces; there is no volumetric beam.

## Lifecycle and timing

- Writes run in the frame-phase command drain on the main thread (a prefix on the main viewport's frame, or `StarMapBeforeGui`: `frame:viewports` or `frame:draw-ui`).
- `LightModule.UpdateRenderData` reads the template later in the same frame, from `PartTree.UpdateRenderData` inside `Program.UpdateRenderingResources` (`frame:render`), so a write drained in the viewport prefix lights the frame being rendered.
- Switching on is via `PowerConsumer.LightIsActive`, consumed by the worker at the next vehicle step (see approach 1).

## Steps

1. **On, off and master.** Set the part's switch rather than toggling, and replicate `Vehicle.ToggleLights` as a set for the master:

   ```csharp
// gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs L34-L53
internal static CommandResult SetMaster(Vehicle vehicle, bool on)
{
    vehicle.LightsOn = on;
    foreach (var consumer in vehicle.Parts.Modules.Get<PowerConsumer>())
        if (consumer.LightSwitch)
            consumer.LightIsActive = on;
    return CommandResult.Ok;
}

[KsaAnchor("LightModule.Parent.FullPart.LightSwitch.LightIsActive", SourceFile = "KSA/LightModule.cs",
    Verified = "2026-06-12", Risk = ChurnRisk.Medium, Notes = "Per-light on/off via the part's power switch.")]
internal static CommandResult SetOn(Vehicle vehicle, int ordinal, bool on)
{
    if (GetLight(vehicle, ordinal) is not { } light)
        return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
    if (light.Parent.FullPart.LightSwitch is not { } powerSwitch)
        return new CommandResult(CommandOutcome.Unsupported, $"light {ordinal} has no power switch");
    powerSwitch.LightIsActive = on;
    return CommandResult.Ok;
}
```

2. **Unshare before the first write.** Shallow-clone the template and each reference object you will edit (`Intensity`, `ColorRgb`, `OuterAngle`, `InnerAngle`) using a field-by-field copy over the whole type hierarchy, assign the clone to that module, and remember it in a `ConditionalWeakTable`:

   ```csharp
// gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs L127-L150
private static void EnsureUnshared(LightModule light)
{
    if (Unshared.TryGetValue(light, out _))
        return;
    var clone = ShallowClone(light.Template);
    clone.Intensity = ShallowClone(light.Template.Intensity);
    clone.ColorRgb = ShallowClone(light.Template.ColorRgb);
    clone.OuterAngle = ShallowClone(light.Template.OuterAngle);
    clone.InnerAngle = ShallowClone(light.Template.InnerAngle);
    light.Template = clone;
    Unshared.Add(light, Marker);
}

/// <summary>Field-by-field clone across the whole type hierarchy (ctor-agnostic — handles internal refs).</summary>
private static T ShallowClone<T>(T source) where T : class
{
    var type = source.GetType();
    var clone = (T)RuntimeHelpers.GetUninitializedObject(type);
    for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        foreach (var field in current.GetFields(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            field.SetValue(clone, field.GetValue(source));
    return clone;
}
```

3. **Brightness and colour** write to the clone's references. For colour write R, G, B, then `OnDataLoad(null)` to recompute the rendered value:

   ```csharp
// gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs L58-L83
internal static CommandResult SetBrightness(Vehicle vehicle, int ordinal, double value)
{
    if (GetLight(vehicle, ordinal) is not { } light)
        return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
    EnsureUnshared(light);
    light.Template.Intensity.Value = (float)Math.Max(0, value);
    return CommandResult.Ok;
}

[KsaAnchor("LightModule.Template.ColorRgb.{R,G,B} + OnDataLoad (per-instance, after template clone)",
    SourceFile = "KSA/LightModule.cs / KSA/ColorRgbReference.cs", Verified = "2026-06-12", Risk = ChurnRisk.High,
    Notes = "OnDataLoad recomputes the rendered Value from R/G/B (zippo pattern).")]
internal static CommandResult SetColor(Vehicle vehicle, int ordinal, IReadOnlyList<double> rgb)
{
    if (rgb.Count != 3)
        return new CommandResult(CommandOutcome.Invalid, "color expects 'r g b'");
    if (GetLight(vehicle, ordinal) is not { } light)
        return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
    EnsureUnshared(light);
    var color = light.Template.ColorRgb;
    color.R = (float)Math.Clamp(rgb[0], 0, 1);
    color.G = (float)Math.Clamp(rgb[1], 0, 1);
    color.B = (float)Math.Clamp(rgb[2], 0, 1);
    color.OnDataLoad(null!); // recompute the rendered Value from R/G/B (mod arg is unused)
    return CommandResult.Ok;
}
```

4. **Angles in degrees, stored in radians, clamped, inner pulled down with outer.** Keep inner at or below outer so `CreateSpotLight`'s swap cannot fire and pin the cone back to the old inner angle:

   ```csharp
// gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs L92-L119
internal static CommandResult SetOuterAngle(Vehicle vehicle, int ordinal, double degrees)
{
    if (GetLight(vehicle, ordinal) is not { } light)
        return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
    EnsureUnshared(light);
    var outer = (float)Math.Clamp(degrees * DegToRad, MinOuterAngleRad, MaxOuterAngleRad);
    light.Template.OuterAngle.Value = outer;
    // Keep inner <= outer so KSA's CreateSpotLight swap can't fire (which would pin the cone back to
    // the old inner angle and silently undo the narrowing).
    if (light.Template.InnerAngle.Value > outer)
        light.Template.InnerAngle.Value = outer;
    return CommandResult.Ok;
}

[KsaAnchor("LightModule.Template.InnerAngle.Value (per-instance, after template clone)",
    SourceFile = "KSA/LightModule.cs / KSA/FloatReference.cs", Verified = "2026-06-23", Risk = ChurnRisk.High,
    Notes = "Spotlight inner-cone half-angle (full-brightness core). Argument is degrees; stored as "
            + "radians, clamped to [0, current OuterAngle] (KSA clamps inner to [0, outer] and swaps if "
            + "inner > outer). Template is shared; EnsureUnshared clones both angles (red-alert pattern).")]
internal static CommandResult SetInnerAngle(Vehicle vehicle, int ordinal, double degrees)
{
    if (GetLight(vehicle, ordinal) is not { } light)
        return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
    EnsureUnshared(light);
    var inner = Math.Clamp(degrees * DegToRad, 0, light.Template.OuterAngle.Value);
    light.Template.InnerAngle.Value = (float)inner;
    return CommandResult.Ok;
}
```

5. **For a very narrow laser-like beam** also raise the intensity, and see gatOS' pure-XML narrow part example ([docs/examples/CoreLaserAGameData.xml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/examples/CoreLaserAGameData.xml)).

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs) | Switches, per-instance template clone, value writes |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L809-L839) | Light read-back including angles |
| [docs/LIGHT_PART_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/LIGHT_PART_ANALYSIS.md) | The analysis of light parts |

## Variations and gotchas

- **Shared template writes recolour every light of that part type** in the universe; clone first (this approach) or accept the global scope (approach 1).
- **The emissive lens stays white** regardless of light colour; the analysis explains why and gatOS has not implemented lens recolouring.
- **The analysis line numbers refer to an older build**; the members cited here exist unchanged in this build.
- **A clone is per module, not per vessel save.** Nothing here persists the clone; reapply after load.

## See also

- [Overview](overview.md)
- [Approach 1](control-part-light-color-and-state-1.md), [approach 5](control-part-light-color-and-state-5.md)
- [Part light cone control](../../../../mods/gatos/features/light-cone-control.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
