---
type: Mod Feature
title: Non-cumulative per-axis part scaling with captured baselines
description: Garry's Torch multiplies each full part's captured Part.Scale by independent XYZ factors, lets sub-parts inherit, restores the originals on unweld, extends kitten avatars to XYZ with a Harmony postfix and excludes Godzilla through VehicleScaleOwnership
mod: unscience
submod: garrys-torch
category: vehicles-and-parts
techniques: [harmony-postfix, reflection]
hooks:
  - harmony-postfix KSA.KittenRenderable.ModelToBodyMatrix (private, by name)
  - reflection KSA.KittenRenderable._characterAvatar
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, UI scale edits)
  - harmony-transpiler KSA.Program.PrepareFrame (shared PhysicsFrameHook, animated scale edits)
lifecycle: [frame:draw-ui, frame:prepare, frame:render]
game_types: [KSA.Part, KSA.PartTree, KSA.Vehicle, KSA.KittenEva, KSA.KittenRenderable, KSA.CharacterAvatar]
tags: [scaling, part-scale, subparts, kitten, avatar, ownership, harmony, reflection]
---

A weld can resize its source vessel along three independent axes (for example 1 x 3 x 0.5), from
the UI, the public API, presets or a queued animation, and unwelding returns every part to exactly
the size it had before, including craft built with non-default part scales. EVA kittens, whose
character model only has a scalar size, get the same XYZ behaviour.[^garrys-torch-lib-readme]

Mechanism in one line: a per-vessel `WeldScaleSnapshot` (held in a `ConditionalWeakTable`) records
every full part's `Part.Scale` once, and each edit writes `original * factor` to full parts only,
invalidating the cached matrices of the whole sub-part subtree; kittens get `CharacterCore.Scale *
factor.X` plus a Harmony postfix on `KittenRenderable.ModelToBodyMatrix` for the remaining axis ratios.

## How it works

**Capture** ([garrys-torch.lib/WeldScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScaleSnapshot.cs#L24-L34)). `CreateWeld` calls
`WeldEngine.CaptureVehicleScale(source)` even at identity, so unwelding restores this instance rather
than template defaults. The snapshot stores `Part.Scale` for every entry of `vehicle.Parts.Parts`
(full parts only; in KSA sub-parts are not in `PartTree.Parts`). For a `KittenEva` it reads the
private `KittenRenderable._characterAvatar` field by reflection and stores `Core.Scale`; a kitten
whose avatar is not ready yet makes weld creation fail with a retryable message.

**Apply** ([garrys-torch.lib/WeldScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScaleSnapshot.cs#L36-L61)). For each full part:
`part.Scale = original * factor` per axis, skipping unchanged values, then `InvalidateSubtree(part)`.
Parts that joined the vessel during the weld (docking) enter the session at their current scale.
Only full parts are written because KSA builds a sub-part's transform from its parent's:
`Part.ScaleTotal` multiplies `Scale` up the `PartParent` chain and the asm matrices compose
`CreateScale(Scale)` per level, so multiplying children too would apply the factor once per nesting
level. Sub-parts keep their authored and animated local scale, position and rotation.[^ksa-part]
Full-part positions are not touched, so spacing between parts stays fixed (parts grow in place).

**Cache invalidation**. The native `Part.Scale` setter calls `ResetCachedPosMatrixValues()` on that
part only (which also calls `Tree?.RenderData.InvalidateTransforms()`), while sub-parts keep cached
`_matrixAsmb2VehicleAsmb` and position values derived from the old parent scale; the snapshot walks
the sub-part tree and resets each.[^ksa-part]

**Restore** ([garrys-torch.lib/WeldScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScaleSnapshot.cs#L63-L79)). Only parts still in the
source and marked modified are written back; detached or destroyed parts, which may now belong to
another vessel, are left alone. Disposed sources are skipped entirely. `WeldEngine.RestoreVehicleScale`
then drops the snapshot ([garrys-torch.lib/WeldEngine.Scaling.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.Scaling.cs#L10-L33)).

**Kitten XYZ** ([garrys-torch.lib/KittenScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/KittenScalePatches.cs)). KSA renders the avatar with
`ModelToBodyMatrix() = CreateScale(Core.Scale) * RotationX * RotationZ`. The snapshot sets the scalar to
`capturedScale * factor.X` and registers a correction `(1, Y/X, Z/X)` for that `KittenRenderable`
in a weak table; the postfix pre-multiplies `CreateScale(correction)` into the returned matrix, i.e.
in the character model's own axes before the model-to-body rotation. Non-welded kittens miss the table
lookup and pay only that cost. The correction also reaches `GetBoneAsmbOffset`, which IVA seats and the
crew portrait use.[^ksa-kitten-renderable]

**Ownership** ([ksa-abstractions.lib/VehicleScaleOwnership.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleScaleOwnership.cs)). Garry's Torch and
Godzilla both rewrite `Part.Scale` from their own captured baselines, so two live sessions would
corrupt each other's "original". `VehicleScaleOwnership` is a `ConditionalWeakTable<Vehicle, Owner>`:
`TryAcquire(vehicle, name)` succeeds if the vessel is free or already owned by the same name,
`Release` only clears the caller's own claim. `CreateWeld` refuses a source owned by Godzilla ("Restore
Godzilla scaling ... before welding it"); Godzilla refuses a vessel welded as a source. A Godzilla-scaled
vessel can still be a weld target. Every removal path (unweld, Delete All, auto-unweld, reset, dispose)
restores scale and releases ownership.

**Validation** ([garrys-torch.lib/WeldScale.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScale.cs)): any positive finite axis is valid; the
0.05 to 20 range is only the drag-widget range. Legacy uniform overloads expand a `float` to `(f, f, f)`.

## Lifecycle and timing

| Entry point | Thread and phase | Notes |
|---|---|---|
| Weld panel scale fields (`RenderWeldSection`) | Main thread, `[StarMapAfterGui]` (`frame:draw-ui`) | Applied immediately when the value changes, while the next vehicle step is already running |
| `CreateWeld` / `ModifyWeld` (UI button or API caller) | Caller's thread, normally the GUI phase | Same immediate write |
| Weld animations | `PhysicsFrameHook.BeforePhysics` (`frame:prepare`) | `WeldAnimation.Update` calls `ApplyVehicleScale` before the weld teleport |
| `ModelToBodyMatrix` postfix | Main thread during `Program.RenderGame` / `RenderViewport` (kitten render data, `frame:render`), plus IVA and portrait UI | Read-only weak-table lookup |
| Scene load | Save replay (`ImportSavedScale`) | Native save already holds effective full-part scales; only the original baseline and kitten correction are restored |

Scale edits do not have to wait for the handoff the way teleports do because they only touch
part-transform and render caches on the main thread: Garry's Torch never calls `Part.RefreshScale`,
`RecomputeAllDerivedData` or `UpdateAfterPartTreeModification`, so the shapes, mass and module state that
the vehicle worker owns are not resized (welded sources also have collisions off by default). The cost
is that physics keeps the original sizes; compare
[Godzilla's physics channel](./godzilla-vessel-scaling.md), which does rescale modules and therefore
queues every edit to the handoff.

Kitten avatars are drawn from `KittenEva.UpdateRenderData`, which `Program` calls from `RenderViewport`
and `RenderGame` (not from `UpdateRenderingResources` like other vessels), so the postfix runs during
command recording for each viewport.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [garrys-torch.lib/WeldScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScaleSnapshot.cs) | Capture, apply, restore, subtree invalidation |
| [garrys-torch.lib/WeldEngine.Scaling.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldEngine.Scaling.cs) | Weak per-vessel snapshot table and public `ApplyVehicleScale`/`RestoreVehicleScale` |
| [garrys-torch.lib/KittenScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/KittenScalePatches.cs) | `ModelToBodyMatrix` postfix and per-renderable correction table (shared with Godzilla) |
| [ksa-abstractions.lib/VehicleScaleOwnership.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleScaleOwnership.cs) | Exclusive scale ownership between Garry's Torch and Godzilla |
| [garrys-torch.lib/WeldScale.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScale.cs) | Validation and uniform helpers |
| [garrys-torch.lib/WeldScaleSnapshot.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/WeldScaleSnapshot.Persistence.cs) | Baseline save/import so reloads do not compound scale |
| [garrys-torch.lib/GarrysTorchSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/GarrysTorchSubmod.cs#L536-L596) | `CreateWeld`: ownership acquire, capture, initial apply |
| [garrys-torch.tests/ScaleChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.tests/ScaleChecks.cs) | Managed checks: nested inheritance, non-cumulative edits, kitten Harmony patch |
| [garrys-torch.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/README.md) | Authors' notes on scale preservation and saves |

## Snippets

Full parts only, from the captured baseline, with subtree cache invalidation:

```csharp
// garrys-torch.lib/WeldScaleSnapshot.cs L36-L61
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

Kitten XYZ correction on top of the scalar avatar scale:

```csharp
// garrys-torch.lib/KittenScalePatches.cs L51-L66
    /// <summary>Set the character axis correction; the caller owns the scalar avatar scale.</summary>
    public static void SetScale(KittenRenderable renderable, float3 scale)
    {
        _corrections.Remove(renderable);

        var correction = new float3(1f, scale.Y / scale.X, scale.Z / scale.X);
        if (!WeldScale.Equals(correction, WeldScale.Identity))
            _corrections.Add(renderable, new ScaleCorrection(correction));
    }

    private static void ModelToBodyMatrixPostfix(
        KittenRenderable __instance, ref float4x4 __result)
    {
        if (_corrections.TryGetValue(__instance, out var correction))
            __result = float4x4.CreateScale(correction.Value) * __result;
    }
```

Scale ownership shared with Godzilla:

```csharp
// ksa-abstractions.lib/VehicleScaleOwnership.cs L6-L22
/// <summary>Prevents two live tools from overwriting the same vessel's scale.</summary>
public static class VehicleScaleOwnership
{
    private sealed record Owner(string Name);
    private static readonly ConditionalWeakTable<Vehicle, Owner> Owners = new();
    public static string? GetOwner(Vehicle vehicle) => Owners.TryGetValue(vehicle, out var owner) ? owner.Name : null;
    public static bool TryAcquire(Vehicle vehicle, string name)
    {
        if (Owners.TryGetValue(vehicle, out var owner)) return owner.Name == name;
        Owners.Add(vehicle, new(name));
        return true;
    }
    public static void Release(Vehicle vehicle, string name)
    {
        if (GetOwner(vehicle) == name) Owners.Remove(vehicle);
    }
}
```

Why only full parts are written: KSA composes sub-part scale from the parent chain (build 5554):

```csharp
// current/decomp/KSA/Part.cs L806-L831
	public double3 ScaleTotal
	{
		get
		{
			if (!IsSubPart)
			{
				return Scale;
			}
			double3 scaleTotal = PartParent.ScaleTotal;
			return new double3(Scale.X * scaleTotal.X, Scale.Y * scaleTotal.Y, Scale.Z * scaleTotal.Z);
		}
	}

	public double3 Scale
	{
		[CompilerGenerated]
		get
		{
			return _003CScale_003Ek__BackingField;
		}
		set
		{
			_003CScale_003Ek__BackingField = value;
			ResetCachedPosMatrixValues();
		}
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Part.Scale` (setter resets cached matrices) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L831) | The per-axis write |
| `Part.ScaleTotal`, `Part.SubParts`, `Part.IsSubPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L806-L817) | Inheritance model |
| `Part.MatrixAsmb2ParentAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L784-L798) | `CreateScale(Scale) * rotation * translation` per level |
| `Part.ResetCachedPosMatrixValues()` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249) | Subtree cache invalidation |
| `PartTree.Parts` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L136) | Full parts only |
| `KittenRenderable.ModelToBodyMatrix()` (private) | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L115) | Postfix target; also used by `GetBoneAsmbOffset` |
| `KittenRenderable._characterAvatar` (private field) | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13) | Reflected avatar handle |
| `CharacterAvatar.Core`, `CharacterCore.Scale` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L30-L34) | Scalar avatar size (default 0.01) |
| `KittenEva.Renderable` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L59) | Typed access to the renderable |
| `KittenEva.UpdateRenderData` call sites in `Program` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4495) | Kitten render data is built during render recording |

## Pitfalls and version notes

- Physics does not follow: colliders, mass and other `IRescale` modules keep their pre-weld size
  because `Part.RefreshScale` is never called. Do not expect a welded giant to collide like one.
- UI and API scale edits mutate parts during the GUI phase while the vehicle worker runs; they are
  limited to transform caches, but any future change that makes the worker read `Part.Scale` would
  turn this into a race. Animation-driven edits already run at the handoff.
- `_characterAvatar` is a string-named private field and `ModelToBodyMatrix` a string-named private
  method: renames fail at patch or capture time, not at compile time. Both exist unchanged in 5554
  (`KittenRenderable.cs` L13 and L107).
- The kitten correction is relative to X: the scalar carries X and the postfix only applies the ratios,
  so a zero or non-finite X is rejected earlier by `WeldScale.IsValid`.
- `SetPartScaleRecursive` is an old absolute override kept for compatibility; it writes every sub-part
  and does not capture originals. Welding does not use it.
- Ownership is advisory: only Garry's Torch and Godzilla consult `VehicleScaleOwnership`; Don't Stifle
  Me or other mods writing `Part.Scale` are not excluded.

## See also

- [Garry's Torch vehicle welding](./garrys-torch-vehicle-welding.md)
- [Weld animation and presets](./garrys-torch-weld-animation-and-presets.md)
- [Godzilla vessel scaling](./godzilla-vessel-scaling.md) (the other owner, which also reuses `KittenScalePatches`)
- [Don't Stifle Me editor free scaling](./dont-stifle-me-editor-free-scaling.md)
- [Garry's Torch sub-mod](../submods/garrys-torch.md) and [mod overview](../overview.md)
- [Scale parts per axis with captured baselines](../../../howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-3.md) - how-to article demonstrating this feature

[^garrys-torch-lib-readme]: garrys-torch.lib/README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-kitten-renderable]: KSA.KittenRenderable (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
