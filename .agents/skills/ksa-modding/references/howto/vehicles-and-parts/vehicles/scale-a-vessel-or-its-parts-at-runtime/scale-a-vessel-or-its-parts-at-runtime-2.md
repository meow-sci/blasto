---
type: How-To
title: Scale a vessel with a recursive Part.Scale write and an avatar scalar
description: Set Part.Scale on every part and sub-part, and Core.Scale on a kitten's avatar, as the simplest visual scale that also changes what the game believes about the part
category: vehicles-and-parts
group: vehicles
topic: scale-a-vessel-or-its-parts-at-runtime
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/scale-and-force-render-cheats]
techniques: [reflection]
hooks: [reflection KSA.KittenRenderable._characterAvatar]
lifecycle: [frame:draw-ui]
game_types: [KSA.Part, KSA.Vehicle, KSA.KittenEva, KSA.PartTree]
tags: [scale, cheat, kitten, avatar, visual]
---
Outcome: one call resizes a vessel (or an EVA kitten) to a uniform factor. gatOS exposes it as a file write
(`vessels/by-id/<id>/scale`), but the recipe stands alone and has no Harmony patch at all. It is the cheapest
route, with caveats: it is visual and transform-only, compounds on sub-parts, and never goes back by itself.

Prerequisite: StarMap. Demonstrated by [gatos/scale-and-force-render-cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md). If you need exact restore, no
compounding or physics, use article 1; for non-uniform axes use article 3.

## Background: how the game does it

- `Part.Scale` is a `double3` whose setter stores the value and calls `ResetCachedPosMatrixValues()` only
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L830)[^ksa-part]. It never calls `RefreshScale()`, so no `IRescale` module (mass, tanks, colliders,
  nozzles) is touched [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1693).
- `Part.ScaleTotal` multiplies a sub-part's `Scale` by its parent's `ScaleTotal` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L806-L817).
  A write that recurses into every sub-part with the same factor therefore draws a sub-part at `factor^depth`.
  gatOS's own anchor notes this since KSA 5348: a sub-part's total is now `factor^2` where it was `~2*factor`.
- An EVA kitten is drawn from a character avatar: `KittenRenderable.ModelToBodyMatrix()` is
  `CreateScale(_characterAvatar.Core.Scale) * RotationX * RotationZ`, and `Core.Scale` defaults to `0.01f`
  (1:1) [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L115)[^ksa-kittenrenderable].
- Newer KSA builds apply a saved top-level `Scale` through `RefreshScale()` on load, per gatOS's anchor
  (5438), so a saved cheat scale can turn physical after a reload.

## Lifecycle and timing

gatOS runs the write on the main thread from its frame-phase command drain. Because the setter touches only
cached matrices and the tree's render transforms, it does not need the settled window the way a physical rescale
does ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)); the cost is that physics keeps the original sizes. Any hook that
runs on the main thread (a button in `frame:draw-ui`, `StarMapAfterOnFrame`) is fine.

## Steps

1. Write `Part.Scale` for every full part and, recursively, every sub-part:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs L48-63
    private static void Apply(Vehicle vehicle, double factor)
    {
        foreach (var part in vehicle.Parts.Parts)
            SetPartScaleRecursive(part, factor);

        // KittenEva renders via CharacterAvatar.Core.Scale (Core.Scale 0.01 == 1:1).
        if (vehicle.GetType().Name == "KittenEva")
            TryScaleAvatar(vehicle, factor);
    }

    private static void SetPartScaleRecursive(Part part, double factor)
    {
        part.Scale = new double3(factor, factor, factor);
        foreach (var sub in part.SubParts)
            SetPartScaleRecursive(sub, factor);
    }
```

2. For a `KittenEva`, scale the avatar. The path is `KittenEva._renderable` -> `KittenRenderable._characterAvatar`
   -> `Core` -> `Scale` (float, 0.01 is 1:1). `Core` may be a struct, so write the boxed value back:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs L98-124
    private static void TryScaleAvatar(Vehicle vehicle, double factor)
    {
        try
        {
            if (ResolveAvatarCore(vehicle) is not var (coreField, avatar, core))
                return;

            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var scaleField = core.GetType().GetField("Scale", all);
            var scaleProp = core.GetType().GetProperty("Scale", all);

            if (scaleField is not null && scaleField.FieldType == typeof(float))
            {
                scaleField.SetValue(core, (float)(factor * 0.01));
                coreField.SetValue(avatar, core); // Core may be a struct: write the box back.
            }
            else if (scaleProp is not null && scaleProp.PropertyType == typeof(float))
            {
                scaleProp.SetValue(core, (float)(factor * 0.01));
                coreField.SetValue(avatar, core);
            }
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn($"scale: KittenEva avatar scale failed: {ex.Message}");
        }
    }
```

3. Validate the factor (finite and greater than zero) before writing; gatOS answers `Invalid` otherwise:

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs L20-27
    internal static CommandResult Set(Vehicle vehicle, double factor)
    {
        if (!ScaleRules.IsValid(factor))
            return new CommandResult(CommandOutcome.Invalid, "scale must be a finite value > 0");

        Apply(vehicle, factor);
        return CommandResult.Ok;
    }
```

4. To undo, write the factor `1.0` back (or keep your own baselines, article 3). There is no automatic restore.

## Complete example

- [gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs)[^gatos-scaleactuator] - the whole actuator (read-back included)

## Variations and gotchas

- **Do not recurse blindly into sub-parts.** Because of `ScaleTotal`, the sample's recursive write scales
  nested sub-parts by more than the requested factor; write full parts only if you want exactly `factor`
  (article 3 does).
- **Nothing physical happens.** Mass, colliders, tank volumes and nozzle areas stay put until something calls
  `RefreshScale()` (the editor, or a load of a vessel with a saved scale).
- **Hidden at distance.** A scaled-up vessel is still culled by its true `MeanRadius`; to keep it drawn use
  article 1's cull redirect or `keep-vessels-rendered-at-any-distance`.
- **Reflection.** The `GetType().Name == "KittenEva"` gate and the field walk are brittle; each hop is
  null-checked and failures are logged, and the parts were already scaled.

## See also

- [Overview](overview.md), [Article 1](scale-a-vessel-or-its-parts-at-runtime-1.md), [Article 3](scale-a-vessel-or-its-parts-at-runtime-3.md)
- [gatos/scale-and-force-render-cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md)
- `keep-vessels-rendered-at-any-distance`

[^ksa-kittenrenderable]: current/decomp/KSA/KittenRenderable.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^gatos-scaleactuator]: gatOS: gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs
