---
type: How-To
title: Scale parts per axis with captured baselines
description: Record each full part's Part.Scale once, write original times a per-axis factor to full parts only, invalidate sub-part caches, and restore exactly
category: vehicles-and-parts
group: vehicles
topic: scale-a-vessel-or-its-parts-at-runtime
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/garrys-torch-per-axis-part-scaling]
techniques: [harmony-postfix, reflection]
hooks: [harmony-postfix KSA.KittenRenderable.ModelToBodyMatrix]
lifecycle: [frame:draw-ui, frame:prepare, frame:render]
game_types: [KSA.Part, KSA.Vehicle, KSA.KittenEva, KSA.PartTree, KSA.ScaleFactors, KSA.IRescale]
tags: [scale, per-axis, baseline, restore, kitten]
---
Outcome: a vessel is resized along three independent axes (for example 1 x 3 x 0.5) and returned to exactly the
size it had before, even when it was built with non-default part scales. EVA kittens, whose character model
only has a scalar size, get the same XYZ behaviour through a render postfix. This is unscience's Garry's Torch
weld-scaling, but it is independent of welds.

Prerequisites: StarMap; Harmony for the kitten axis. Demonstrated by
[unscience/garrys-torch-per-axis-part-scaling](../../../../mods/unscience/features/garrys-torch-per-axis-part-scaling.md).

## Background: how the game does it

- Write full parts only. `Part.ScaleTotal` multiplies `Scale` up the `PartParent` chain
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L806-L817)[^ksa-part] and the matrices compose `CreateScale(Scale)` per level, so scaling sub-parts too
  would apply the factor once per nesting level. Sub-parts keep their authored and animated local transforms.
- The `Scale` setter resets only that part's cached matrices; sub-parts keep cached values derived from the old
  parent scale, so walk the subtree and call `ResetCachedPosMatrixValues()` on each
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249).
- Physics sees one number: `RefreshScale()` builds `new ScaleFactors(ScaleTotal)`, and
  `ScaleFactors(double3)` takes the largest axis [current/decomp/KSA/ScaleFactors.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScaleFactors.cs#L6-L21)[^ksa-scalefactors] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1693).
  This recipe never calls `RefreshScale`, so mass, colliders, tanks and nozzles keep their original sizes and
  only the drawn shape follows the axes. In the stock editor a non-uniform scale on connectors, mass and
  `IRescale` modules would therefore collapse to its largest axis.
- A kitten renders as `CreateScale(Core.Scale) * RotationX * RotationZ` [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110)[^ksa-kittenrenderable];
  `Core.Scale` is a single float, so the other two axis ratios need a matrix correction.

## Lifecycle and timing

| Entry point | Thread and phase | Notes |
|---|---|---|
| UI scale fields | main, `StarMapAfterGui` (`frame:draw-ui`) | applied immediately when the value changes, while the next vehicle step runs |
| weld animations | the `PrepareFrame` handoff (`frame:prepare`) | the animation calls `ApplyVehicleScale` before the teleport |
| `ModelToBodyMatrix` postfix | main, during `RenderGame` / `RenderViewport` (`frame:render`), plus IVA and portrait UI | read-only weak-table lookup |
| scene load | save replay | the native save already holds effective full-part scales; only the baseline and kitten correction are restored |

A scale write does not have to wait for the settled window like a teleport because it only touches part
transform and render caches on the main thread: nothing the worker reads (shapes, mass, module state) is
resized. The cost is that physics keeps the original sizes. Compare article 1's physical channel, which does
rescale modules and therefore queues to the handoff. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. Capture every full part's `Scale` once, in a per-vessel snapshot (a `ConditionalWeakTable` keyed by vessel).
   Capture even at identity so restore returns this instance, not template defaults. For a kitten read
   `Core.Scale` through the private `KittenRenderable._characterAvatar` field and fail with a retryable message
   if the avatar is not ready:

```csharp
// garrys-torch.lib/WeldScaleSnapshot.cs L24-34
    public WeldScaleSnapshot(Vehicle vehicle)
    {
        _vehicle = vehicle;
        foreach (var part in vehicle.Parts.Parts) _originals.Add(part, new(part.Scale));
        if (vehicle is KittenEva kitten)
        {
            _avatar = ReflectionHelpers.GetFieldValue<CharacterAvatar>(kitten.Renderable, "_characterAvatar")
                ?? throw new InvalidOperationException("The kitten's character is not ready. Try again after it has spawned.");
            _avatarScale = _avatar.Core.Scale;
        }
    }
```

2. Apply: write `original * factor` per axis to full parts, skip unchanged values, invalidate the subtree. Parts
   that join later (docking) enter the session at their current scale:

```csharp
// garrys-torch.lib/WeldScaleSnapshot.cs L36-61
    public void Apply(float3 factor)
    {
        // Only full parts: KSA composes each subpart's matrix with its parent's matrix.
        // Multiplying child scales too would apply the factor again at every nesting level.
        foreach (var part in _vehicle.Parts.Parts)
        {
            // Parts added during a weld enter the session at their current local scale.
            if (!_originals.TryGetValue(part, out var original))
                _originals.Add(part, original = new(part.Scale));
            var scale = new double3(original.Scale.X * factor.X,
                original.Scale.Y * factor.Y, original.Scale.Z * factor.Z);
            if (part.Scale == scale) continue;
            original.Modified = true;
            part.Scale = scale;
            InvalidateSubtree(part);
        }

        if (_avatar != null && _vehicle is KittenEva kitten)
        {
            // Preserve nonstandard starting avatar sizes too; 0.01 is only the stock default.
            if (!_avatarModified && WeldScale.Equals(factor, WeldScale.Identity)) return;
            _avatarModified = true;
            _avatar.Core.Scale = _avatarScale * factor.X;
            KittenScalePatches.SetScale(kitten.Renderable, factor);
        }
    }
```

3. Restore only parts still on the vessel and marked modified; detached parts may belong to another vessel now:

```csharp
// garrys-torch.lib/WeldScaleSnapshot.cs L63-86
    public void Restore()
    {
        if (_vehicle.IsDisposed) return;
        // Only visit parts still in this source's full-part list. Detached/destroyed parts
        // may now belong to another vehicle and must not be mutated by this weld.
        foreach (var part in _vehicle.Parts.Parts)
        {
            if (!_originals.TryGetValue(part, out var original) || !original.Modified) continue;
            part.Scale = original.Scale;
            InvalidateSubtree(part);
        }
        if (_avatarModified && _avatar != null && _vehicle is KittenEva kitten)
        {
            _avatar.Core.Scale = _avatarScale;
            KittenScalePatches.SetScale(kitten.Renderable, WeldScale.Identity);
        }
    }

    private static void InvalidateSubtree(Part part)
    {
        // The Scale setter only invalidates its own matrices, not descendant world caches.
        part.ResetCachedPosMatrixValues();
        foreach (var child in part.SubParts) InvalidateSubtree(child);
    }
```

4. For kitten XYZ, store a correction `(1, Y/X, Z/X)` per `KittenRenderable` in a weak table and pre-multiply
   `CreateScale(correction)` into the returned `ModelToBodyMatrix` in a postfix. Because `GetBoneAsmbOffset`
   also uses that matrix, seats and portraits follow.
5. Guard against two scalers: if another feature also rewrites `Part.Scale` from its own baseline, refuse to
   start while it owns the vessel (unscience uses `VehicleScaleOwnership`, a weak table of owner names).

## Complete example

- [garrys-torch.lib/WeldScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScaleSnapshot.cs)[^unscience-weldscalesnapshot] - snapshot, apply, restore
- [garrys-torch.lib/KittenScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/KittenScalePatches.cs)[^unscience-kittenscalepatches] - the kitten postfix

## Variations and gotchas

- **Positions are untouched.** Parts grow in place, so spacing between parts stays fixed; article 1's Smart
  mode also moves parts about the centre of mass.
- **Validation.** Any positive finite axis is valid; the 0.05 to 20 range in the UI is a widget range, not a
  game limit.
- **Edits never accumulate.** Always compute from the baseline.
- **Saves.** The native save stores the part scales; Garry's Torch restores only its baseline and the kitten
  correction on load. When a saved vessel is rebuilt, `Part.RegenerateFromPartInstance` calls `RefreshScale()`
  on every part that has a parent [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2426-L2433), which is the physical path and takes the largest
  axis, so a reloaded per-axis scale can change colliders and mass of those parts.

## See also

- [Overview](overview.md), [Article 1](scale-a-vessel-or-its-parts-at-runtime-1.md), [Article 4](scale-a-vessel-or-its-parts-at-runtime-4.md)
- [unscience/garrys-torch-per-axis-part-scaling](../../../../mods/unscience/features/garrys-torch-per-axis-part-scaling.md)
- [weld-one-vessel-to-another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md)

[^ksa-kittenrenderable]: current/decomp/KSA/KittenRenderable.cs (decompiled, build 2026.10.10.5554)
[^unscience-kittenscalepatches]: unscience: garrys-torch.lib/KittenScalePatches.cs
[^unscience-weldscalesnapshot]: unscience: garrys-torch.lib/WeldScaleSnapshot.cs
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-scalefactors]: current/decomp/KSA/ScaleFactors.cs (decompiled, build 2026.10.10.5554)
