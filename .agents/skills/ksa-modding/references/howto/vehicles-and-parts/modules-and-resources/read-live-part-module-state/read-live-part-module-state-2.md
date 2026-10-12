---
type: How-To
title: Read a tank's shape and liquid level
description: Reach the private Tank._template by reflection, build a tank shape, sum liquid mass and volume from the mole states, and find the fill surface against the acceleration the craft feels
category: vehicles-and-parts
group: modules-and-resources
topic: read-live-part-module-state
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/holed-tank-leaks]
techniques: [reflection]
hooks: [reflection KSA.Tank._template]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Tank
  - KSA.Mole
  - KSA.MoleState
  - KSA.AsmbTankTemplate
  - KSA.SphericalTankTemplate
  - KSA.CylindricalTankTemplate
  - KSA.ConicalTankTemplate
  - KSA.Vehicle
tags: [tank, shape, liquid, reflection, propellant]
---
Outcome: for any tank you can answer "where does the liquid stand?", not just "how much is in it?". KSArmory needs
it so a hole below the surface leaks and a hole above does not. This article covers the reads; the write side
(taking mass off) is [Refill, drain or top up a vehicle's propellant and batteries](../refill-or-drain-resources/overview.md) article 3 and [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)
article 6.

Prerequisites: StarMap, reflection. Demonstrated by [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md). Basic mass reads are in
[article 1](read-live-part-module-state-1.md).

## Background: how the game does it

- A `Tank` has a private readonly `AsmbTankTemplate _template` [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L82)[^ksa-tank] with subclasses
  `SphericalTankTemplate`, `CylindricalTankTemplate` and `ConicalTankTemplate` (radius, length, wall thickness,
  `DomeHeightFraction`, `LocationAsmb`) [current/decomp/KSA/AsmbTankTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AsmbTankTemplate.cs#L7)[^ksa-asmbtanktemplate] [current/decomp/KSA/SphericalTankTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SphericalTankTemplate.cs#L5)[^ksa-sphericaltanktemplate]
  [current/decomp/KSA/CylindricalTankTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CylindricalTankTemplate.cs#L6)[^ksa-cylindricaltanktemplate] [current/decomp/KSA/ConicalTankTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConicalTankTemplate.cs#L6)[^ksa-conicaltanktemplate]. `_template.GetTankPropertiesAsmb()` feeds the
  tank's volume [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L118-L130); `Tank.StorageVolume` is public.
- Each `Mole` has `Liquid` (the liquid phase, or null) with `StorageDensity`; the mass in it is
  `MoleState.Mass` in `PartTree.Moles` [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L15-L20)[^ksa-mole].
- KSA models no tank pressure or sloshing, so the level is the mod's: a settled level against the acceleration the craft
  feels, or floating in a coast.

## Lifecycle and timing

Read in the mod's step on the main thread (`StarMapAfterGui`, `frame:draw-ui`); the cached shape and point cloud
per tank live in a `ConditionalWeakTable<Tank, Form>` so a destroyed tank's entry goes with it. The felt
acceleration is lagged with a 1.5 s time constant so an RCS pulse does not slosh the level. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Resolve the private field once, verify its type, and warn if it moved**; fall back to the cylinder filling the
   part's box when it did:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2852-2868
    private static FieldInfo? TankTemplateField
    {
        get
        {
            if (_tankTemplateProbed) return _tankTemplateField;

            _tankTemplateProbed = true;
            _tankTemplateField = typeof(Tank).GetField("_template", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_tankTemplateField is null || !typeof(AsmbTankTemplate).IsAssignableFrom(_tankTemplateField.FieldType))
            {
                Log.Warn("Tank._template has moved - leaking tanks are taken as the cylinder filling their box");
                _tankTemplateField = null;
            }

            return _tankTemplateField;
        }
    }
```

2. **Turn the template into a shape**: the three template kinds, with the inner shape grown inside the wall:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2807-2850
    public static bool TryTankShape(Tank tank, out TankShape outside, out TankShape inside, out double3 origin,
                                    out double3 axisX, out double3 axisY, out double3 axisZ)
    {
        outside = inside = default;
        origin = axisX = axisY = axisZ = default;

        try
        {
            if (TankTemplateField?.GetValue(tank) is not AsmbTankTemplate template) return false;

            double wall;
            switch (template)
            {
                case SphericalTankTemplate sphere:
                    outside = TankShape.Sphere(sphere.OuterRadius);
                    wall = sphere.WallThickness;
                    break;
                case CylindricalTankTemplate cylinder:
                    outside = TankShape.Conical(cylinder.Length, cylinder.OuterRadius, cylinder.OuterRadius,
                                                cylinder.DomeHeightFraction);
                    wall = cylinder.WallThickness;
                    break;
                case ConicalTankTemplate cone:
                    outside = TankShape.Conical(cone.Length, cone.RadiusBase, cone.RadiusTop, cone.DomeHeightFraction);
                    wall = cone.WallThickness;
                    break;
                default:
                    return false;
            }

            inside = outside.Inside(wall);
            origin = template.LocationAsmb;
            floatQuat paf2Asmb = template.GetPaf2Asmb();
            axisX = double3.Unpack(new float3(1f, 0f, 0f).Transform(paf2Asmb));
            axisY = double3.Unpack(new float3(0f, 1f, 0f).Transform(paf2Asmb));
            axisZ = double3.Unpack(new float3(0f, 0f, 1f).Transform(paf2Asmb));

            return outside.Extent.Radius > 0.0 && Vec.IsFinite(origin) && Vec.IsFinite(axisX);
        }
        catch
        {
            return false;
        }
    }
```

3. **Sum the liquid** held in the tank's moles that are liquids with positive storage density; volume is mass over
   density:

```csharp
// src/KSArmory/Ksa/Leaks.cs L236-246
        ReadOnlySpan<MoleState> states = craft.Parts.Moles.States;
        double mass = 0.0, volume = 0.0;
        foreach (Mole mole in tank.Moles)
        {
            if (mole.Liquid is not { } liquid || !(liquid.StorageDensity > 0f)) continue;

            double held = states[mole.StatesIdx].Mass;
            mass += held;
            volume += held / liquid.StorageDensity;
        }

```

4. **Find the level.** Scatter 256 Halton-sequence points through the tank's inner shape, project them on "up" (the
   direction opposite the felt acceleration), sort, and read the surface where the fill fraction of them lies below.
   This handles tilted cylinders with domed ends, which no closed form does. Floating (felt acceleration under about
   0.05 m/s2) has no surface.
5. **Felt acceleration** in the craft's assembly frame, what an accelerometer reads (thrust, drag and the ground's
   push, no gravity):

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2786-2797
    public static double3 FeltAccelerationAsmb(Vehicle v)
    {
        try
        {
            double3 felt = v.Asmb2Ego.Inverse() * v.AccelerationBody.Transform(v.Body2Cce);
            return Vec.IsFinite(felt) ? felt : Vec.Zero;
        }
        catch
        {
            return Vec.Zero;
        }
    }
```

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2807-L2868)[^ksarmory-ksaworld] - shape and the field probe
- [src/KSArmory/Ksa/Leaks.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Leaks.cs#L232-L343)[^ksarmory-leaks] - liquid, level, flow
- [src/KSArmory/Sim/TankLeak.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TankLeak.cs)[^ksarmory-tankleak] - the game-free level and flow maths

## Variations and gotchas

- **Unknown shapes.** A tank template of another kind (or a build that moved the field) returns false and the
  caller takes the cylinder filling the box; do not throw into the frame.
- **Gas moles are ignored**; only liquids have a level.
- **Holes in fairings.** A hole must be inside the shape grown by a small skin share to count as being in the tank.
- **Use the tank's frame.** `tank.Parent` (the part) and `GetPaf2Asmb()` give the tank's own axes inside the part.
- **Updates.** One private field and three public template types; both are verified at runtime.

## See also

- [Overview](overview.md), [Article 1](read-live-part-module-state-1.md)
- [ksarmory/holed-tank-leaks](../../../../mods/ksarmory/features/holed-tank-leaks.md)
- [Refill, drain or top up a vehicle's propellant and batteries](../refill-or-drain-resources/overview.md), `get-part-points-in-vehicle-and-world-frames`

[^ksa-tank]: current/decomp/KSA/Tank.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-tankleak]: KSArmory: src/KSArmory/Sim/TankLeak.cs
[^ksa-asmbtanktemplate]: current/decomp/KSA/AsmbTankTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-cylindricaltanktemplate]: current/decomp/KSA/CylindricalTankTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-sphericaltanktemplate]: current/decomp/KSA/SphericalTankTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-ksaworld]: KSArmory: src/KSArmory/Ksa/KsaWorld.cs
[^ksa-conicaltanktemplate]: current/decomp/KSA/ConicalTankTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-mole]: current/decomp/KSA/Mole.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-leaks]: KSArmory: src/KSArmory/Ksa/Leaks.cs
