---
type: How-To
title: Scale a whole vessel with separate render, physics and collider channels
description: Capture original part scales once, write physical scale in the settled window with the engine's own refresh chain, and scale only the drawn size through transpilers on the render path
category: vehicles-and-parts
group: vehicles
topic: scale-a-vessel-or-its-parts-at-runtime
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/godzilla-vessel-scaling, mods/unscience/features/godzilla-visual-scaling]
techniques: [harmony-transpiler, harmony-prefix, harmony-finalizer, harmony-postfix]
hooks:
  - harmony-transpiler KSA.Vehicle.UpdateRenderData
  - harmony-transpiler KSA.Vehicle.GetWorldMatrix
  - harmony-postfix KSA.Vehicle.GetWorldMatrix
  - harmony-prefix KSA.PartTree.UpdateRenderData
  - harmony-finalizer KSA.PartTree.UpdateRenderData
lifecycle: [frame:prepare, frame:draw-ui, frame:render]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.IRescale
  - KSA.ColliderModule
  - KSA.KittenEva
  - KSA.PartTreeRenderData
tags: [scale, godzilla, visual, physics, colliders, snapshot]
---
Outcome: a vessel (or an EVA kitten) becomes bigger or smaller at runtime and goes back exactly as it was.
unscience's Godzilla offers two modes (Smart: uniform and spacing-preserving; Basic: raw XYZ on every part) and
two global checkboxes, **Scale physics** and **Scale colliders**; rendering always resizes. That gives purely
visual giants, fully physical giants, and visually normal craft with giant collision shapes.

Prerequisites: StarMap, Harmony transpilers, and a settled-window seam (here unscience's `PhysicsFrameHook`; see
[Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md) for how to build one). Demonstrated by
[unscience/godzilla-vessel-scaling](../../../../mods/unscience/features/godzilla-vessel-scaling.md) and [unscience/godzilla-visual-scaling](../../../../mods/unscience/features/godzilla-visual-scaling.md).

## Background: how the game does it

See the [overview](overview.md). The pieces this recipe relies on:

- `Part.Scale` setter invalidates only that part's cached matrices [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L830)[^ksa-part]; descendants need
  `ResetCachedPosMatrixValues()` too [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249).
- `Part.RefreshScale()` pushes `ScaleFactors(ScaleTotal)` to every `IRescale` module, connector and sub-part
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1674-L1693); `Part.UpdateBounds()` recomputes bounds [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1251).
- `Vehicle.UpdateAfterPartTreeModification()` recomputes mass properties, physics state, collision geometry,
  aero and the flight computer [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1908)[^ksa-vehicle].
- `Vehicle.MeanRadius` feeds physics-bubble sizing, camera distance and terrain logic [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L535),
  so the draw cull must not be fixed by overriding that getter.

## Lifecycle and timing

| Work | Where | State |
|---|---|---|
| UI Apply / Restore / checkboxes | `StarMapAfterGui` (`frame:draw-ui`) | only enqueues; the vehicle worker is stepping |
| physical `ApplyNow` / `Restore` | drained at the `PrepareFrame` handoff (`frame:prepare`), before `ExecuteNextVehicleSolvers` | all workers joined, results applied |
| dirty derived part-tree data | `PartTree.FlushDirtyDerived()` two lines later (`Program.cs` L2218) | recomputed before the snapshot |
| visual patches | `Program.UpdateRenderingResources` and `RenderGame`, `frame:render` | main thread, read-only lookups |

Why physical edits must not run from the UI: `UpdateCollisionGeometry` opens `ConstraintSim.UnlockShapes()`
which throws while the step holds the registry, `ColliderModule.SetScale` uses `UnlockShapesBlocking()` and
would stall the main thread, and `RefreshScale` rescales mass, tank and collider modules that the worker reads
for the step it is computing. The visual channel only reads a weak table and the vessel's committed centre of
mass, so it needs no synchronisation. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Capture once.** Store every full part's and sub-part's `Scale` and `PositionParentAsmb`, the pivot
   (`CenterOfMassAsmb`) and, for a kitten, the avatar scalar read through the private `_characterAvatar` field.
   Every later edit derives from these originals, never from the last applied size, so edits do not compound:

```csharp
// godzilla.lib/VesselScaleSnapshot.cs L24-42
    public VesselScaleSnapshot(Vehicle vehicle)
    {
        Vehicle = vehicle;
        _pivot = vehicle.CenterOfMassAsmb;
        foreach (var part in vehicle.Parts.Parts) Capture(part, true);
        if (vehicle is KittenEva kitten)
        {
            _avatar = ReflectionHelpers.GetFieldValue(kitten.Renderable, "_characterAvatar") as CharacterAvatar
                ?? throw new InvalidOperationException("The kitten's character is not ready. Try again after it has spawned.");
            _avatarScale = _avatar.Core.Scale;
        }
    }

    private void Capture(Part part, bool full)
    {
        _originals.Add(new(part, part.Scale, part.PositionParentAsmb, full));
        _byPart.Add(part, _originals[^1]);
        foreach (var child in part.SubParts) Capture(child, false);
    }
```

2. **Write the physical channel.** Smart moves full parts about the pivot and scales them; Basic writes absolute
   XYZ to every part and sub-part and leaves spacing alone. Then run the refresh chain:

```csharp
// godzilla.lib/VesselScaleSnapshot.cs L75-110
    public void Apply(bool smart, float3 factor, bool scalePhysics, bool scaleColliders)
    {
        ValidateApply(factor, scalePhysics, scaleColliders);
        if (smart) factor = new float3(factor.X);
        bool independent = scaleColliders != scalePhysics;
        ColliderScalePatches.Clear(Vehicle);
        if (!scalePhysics)
        {
            // Return the simulation to its captured size before registering a draw-only multiplier.
            if (_physicalApplied) Restore();
            VisualScalePatches.SetScale(Vehicle, factor);
            if (independent) ColliderScalePatches.Set(new(this, scaleColliders, factor));
            return;
        }
        VisualScalePatches.ClearScale(Vehicle);
        _physicalApplied = true; // Includes partially applied edits so rollback can restore them.
        // Undo Basic's child scales on a mode switch, but do not reset animation-owned child
        // transforms on repeated Smart edits or Smart restore.
        foreach (var original in _originals)
        {
            if (original.FullPart)
            {
                original.Part.PositionParentAsmb = smart
                    ? _pivot + (original.Position - _pivot) * factor.X : original.Position;
                original.Part.Scale = smart ? original.Scale * factor.X : new double3(factor.X, factor.Y, factor.Z);
            }
            else if (!smart)
                original.Part.Scale = new double3(factor.X, factor.Y, factor.Z);
            else if (_basic)
                original.Part.Scale = original.Scale;
        }
        _basic = !smart;
        SetCharacterScale(smart ? new float3(factor.X) : factor);
        Refresh();
        if (independent) ColliderScalePatches.Set(new(this, scaleColliders, factor));
    }
```

```csharp
// godzilla.lib/VesselScaleSnapshot.cs L138-148
    private void Refresh()
    {
        // Parent setters do not invalidate descendant world-matrix caches in KSA.
        var parts = CurrentParts();
        foreach (var part in parts) part.ResetCachedPosMatrixValues();
        foreach (var part in Vehicle.Parts.Parts) part.RefreshScale();
        foreach (var part in parts) part.UpdateBounds();
        Vehicle.Parts.RecomputeAllDerivedData();
        Vehicle.UpdateAfterPartTreeModification();
        foreach (var collider in Vehicle.Parts.Modules.Get<ColliderModule>()) collider.NeedsColliderUpdate = true;
    }
```

3. **Restore** from the same snapshot, skipping parts that left the vessel (staged or destroyed parts now belong
   to another tree):

```csharp
// godzilla.lib/VesselScaleSnapshot.cs L112-129
    public void Restore()
    {
        VisualScalePatches.ClearScale(Vehicle);
        ColliderScalePatches.Clear(Vehicle);
        if (Vehicle.IsDisposed || !_physicalApplied) return;
        var current = CurrentParts();
        foreach (var original in _originals)
        {
            // A staged/destroyed part belongs to a different tree; never dereference its modules.
            if (!current.Contains(original.Part)) continue;
            if (original.FullPart) original.Part.PositionParentAsmb = original.Position;
            if (original.FullPart || _basic) original.Part.Scale = original.Scale;
        }
        SetCharacterScale(new float3(1));
        Refresh();
        _physicalApplied = false;
        _basic = false;
    }
```

4. **Visual channel.** Keep a `ConditionalWeakTable<Vehicle, Scale>`. Install five patches, and roll back all of
   them if any fails:

```csharp
// godzilla.lib/VisualScalePatches.cs L20-37
    public static void Apply(Harmony harmony)
    {
        if (IsApplied) return;
        try
        {
            Install(harmony, typeof(Vehicle), nameof(Vehicle.UpdateRenderData), nameof(RenderCullTranspiler), true);
            Install(harmony, typeof(Vehicle), nameof(Vehicle.GetWorldMatrix), nameof(RenderRadiusTranspiler), true);
            Install(harmony, typeof(Vehicle), nameof(Vehicle.GetWorldMatrix), nameof(WorldMatrixPostfix), false, true);
            Install(harmony, typeof(PartTree), nameof(PartTree.UpdateRenderData), nameof(PartsPrefix), false);
            Install(harmony, typeof(PartTree), nameof(PartTree.UpdateRenderData), nameof(PartsFinalizer), false, finalizer: true);
            IsApplied = true;
        }
        catch
        {
            Remove(harmony);
            throw;
        }
    }
```

5. Redirect the single call site of the pixel cull and the single `MeanRadius` read in `GetWorldMatrix` with
   transpilers that throw unless they find exactly one match. A global `MeanRadius` or helper patch would leak
   the visual size into physics or be skipped by the JIT:

```csharp
// godzilla.lib/VisualScalePatches.cs L70-85
    private static IEnumerable<CodeInstruction> RenderCullTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var cull = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.IsLargeEnoughToRender), new[] { typeof(Camera) });
        int matches = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(cull))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(VisualScalePatches), nameof(IsLargeEnoughToRenderScaled));
                matches++;
            }
            yield return instruction;
        }
        if (matches != 1) throw new InvalidOperationException($"Expected one render cull call; found {matches}.");
    }
```

```csharp
// godzilla.lib/VisualScalePatches.cs L114-117
    private static double RenderRadius(Vehicle vehicle) =>
        IsApplied && _scales.TryGetValue(vehicle, out var scale)
            ? vehicle.MeanRadius * Math.Max(scale.Factor.X, Math.Max(scale.Factor.Y, scale.Factor.Z))
            : vehicle.MeanRadius;
```

6. Scale the draw matrix around the live centre of mass for the duration of `PartTree.UpdateRenderData`, and
   restore the caller's local in a finalizer (the parameter is `ref readonly`):

```csharp
// godzilla.lib/VisualScalePatches.cs L119-143
    private static void WorldMatrixPostfix(Vehicle __instance, ref float4x4? __result)
    {
        if (IsApplied && __result.HasValue && _scales.TryGetValue(__instance, out var scale))
            __result = float4x4.CreateScale(scale.Factor) * __result.Value;
    }

    private static void PartsPrefix(PartTree __instance, ref double4x4 matrixAsmb2Ego, out double4x4? __state)
    {
        __state = null;
        var vehicle = __instance.OwningVehicle;
        if (!IsApplied || vehicle == null || !_scales.TryGetValue(vehicle, out var scale)) return;
        __state = matrixAsmb2Ego;
        // KSA's assembly-to-ego matrix already subtracts the live COM. Scale around that same
        // pivot to keep the vessel's physical position fixed, including asymmetric/fuelled craft.
        var pivot = vehicle.CenterOfMassAsmb;
        var factor = new double3(scale.Factor.X, scale.Factor.Y, scale.Factor.Z);
        matrixAsmb2Ego = double4x4.CreateTranslation(-pivot) * double4x4.CreateScale(factor) *
            double4x4.CreateTranslation(pivot) * matrixAsmb2Ego;
    }

    private static void PartsFinalizer(ref double4x4 matrixAsmb2Ego, double4x4? __state)
    {
        // The game's input is ref readonly; do not leave its caller's local matrix modified.
        if (__state.HasValue) matrixAsmb2Ego = __state.Value;
    }
```

7. Validate before mutating (positive finite factor, unchanged topology, patches available), and if a mutation
   throws, restore the snapshot in the same handoff before the next worker snapshots a half-edited vessel.
8. Check the result: with Scale physics on, `Vehicle.Props` mass and the bounding radius change; with it off only
   the picture does.

## Complete example

- [godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs)[^unscience-vesselscalesnapshot] - physical channel
- [godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs)[^unscience-visualscalepatches] - visual channel
- [godzilla.lib/GodzillaSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/GodzillaSubmod.cs)[^unscience-godzillasubmod] - sessions, validation, rollback
- [godzilla.lib/ColliderScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/ColliderScalePatches.cs)[^unscience-colliderscalepatches] - the independent collider channel (requires authored `ColliderModule`s)

## Variations and gotchas

- **Smart touches only full parts**, because sub-parts inherit through `ScaleTotal`; Basic writes sub-parts too,
  so nested children compound by design.
- **Collider-only scaling** needs authored `ColliderModule`s; vessels with only the fallback box are rejected
  before any mutation.
- **Staging or docking changes the part set.** A session check restores scale when the part set changes;
  detached pieces keep their current size.
- **Ownership.** Two features that rewrite `Part.Scale` from their own baselines (Godzilla and Garry's Torch)
  corrupt each other; unscience uses a `VehicleScaleOwnership` weak table to refuse overlap. Do the same if you
  combine scalers.
- **XYZ on physics.** Physical scale reads the largest axis only (`ScaleFactors`), so Basic mode's non-uniform
  values are visual for colliders, mass and connectors.
- **Updates.** Both transpilers fail loudly on a changed call-site count; the render path moved once already
  (KSA 5482 split the cull into `IsLargeEnoughToRender`).

## See also

- [Overview](overview.md), [Article 2](scale-a-vessel-or-its-parts-at-runtime-2.md), [Article 3](scale-a-vessel-or-its-parts-at-runtime-3.md)
- [unscience/godzilla-vessel-scaling](../../../../mods/unscience/features/godzilla-vessel-scaling.md), [unscience/godzilla-visual-scaling](../../../../mods/unscience/features/godzilla-visual-scaling.md)
- `keep-vessels-rendered-at-any-distance`, [weld-one-vessel-to-another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md)

[^unscience-vesselscalesnapshot]: unscience: godzilla.lib/VesselScaleSnapshot.cs
[^unscience-colliderscalepatches]: unscience: godzilla.lib/ColliderScalePatches.cs
[^unscience-godzillasubmod]: unscience: godzilla.lib/GodzillaSubmod.cs
[^unscience-visualscalepatches]: unscience: godzilla.lib/VisualScalePatches.cs
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
