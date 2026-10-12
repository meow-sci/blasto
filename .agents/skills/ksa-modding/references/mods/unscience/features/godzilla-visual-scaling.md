---
type: Mod Feature
title: Render-only vessel scaling with draw-matrix and cull-radius patches
description: Godzilla's visual channel scales a vessel's drawn parts around its live centre of mass and enlarges only its render cull radius, using part-tree draw-matrix patches, a GetWorldMatrix postfix and two guarded call-site transpilers
mod: unscience
submod: godzilla
category: rendering
techniques: [harmony-transpiler, harmony-prefix, harmony-postfix, harmony-finalizer]
hooks:
  - harmony-transpiler KSA.Vehicle.UpdateRenderData (IsLargeEnoughToRender call redirect)
  - harmony-transpiler KSA.Vehicle.GetWorldMatrix (MeanRadius read redirect)
  - harmony-postfix KSA.Vehicle.GetWorldMatrix
  - harmony-prefix KSA.PartTree.UpdateRenderData
  - harmony-finalizer KSA.PartTree.UpdateRenderData
lifecycle: [frame:render, frame:prepare]
game_types: [KSA.Vehicle, KSA.PartTree, KSA.KittenEva, KSA.Camera, KSA.Program]
tags: [scaling, render, culling, transpiler, finalizer, kitten, harmony]
---

With "Scale physics" off, Godzilla changes only what is drawn: a vessel can look ten million times
larger while its mass, aero, physics bubble, picking and camera targeting stay at the real size, and
it does not disappear when its true size would fall under one pixel. EVA kittens, their helmets,
fur and MMU scale with them.[^godzilla-lib-readme]

Mechanism in one line: five Harmony patches read a per-vessel factor from a `ConditionalWeakTable`:
the part-tree draw matrix is pre-multiplied by `T(-COM) * S * T(COM)` for the duration of
`PartTree.UpdateRenderData`, the kitten world matrix is scaled in a `GetWorldMatrix` postfix, and the
two pixel-size culls are redirected to a mirror that uses `MeanRadius * max(factor)`.

## How it works

Patches installed by `VisualScalePatches.Apply` ([godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs#L20-L37));
any failure removes all of them, and `IsApplied` gates the channel:

| Target | Kind | Effect |
|---|---|---|
| `Vehicle.UpdateRenderData(IViewport, int)` | Transpiler | Rewrites exactly one `call Vehicle.IsLargeEnoughToRender(Camera)` to `IsLargeEnoughToRenderScaled(Vehicle, Camera)` |
| `Vehicle.GetWorldMatrix(Camera)` | Transpiler | Rewrites exactly one `get_MeanRadius` call to `RenderRadius(Vehicle)` |
| `Vehicle.GetWorldMatrix(Camera)` | Postfix | `__result = CreateScale(factor) * __result` when a factor is registered |
| `PartTree.UpdateRenderData(ref readonly double4x4, bool, IViewport, int)` | Prefix | Saves the caller's matrix in `__state` and replaces it with `T(-pivot) * S(factor) * T(pivot) * M`, pivot = live `Vehicle.CenterOfMassAsmb` |
| same | Finalizer | Writes the saved matrix back, also when the original throws |

Details:

- **Lookup** ([godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs#L114-L137)). The part-tree prefix finds the
  vessel through the public `PartTree.OwningVehicle` field; trees without an owner or without a
  registered factor return immediately. `RenderRadius` returns plain `MeanRadius` for unregistered vessels.
- **Pivot**. KSA's assembly-to-ego matrix already subtracts the live centre of mass; scaling about the
  same pivot keeps the vessel where physics says it is, including fuelled, asymmetric craft.
- **XYZ** is applied along the vessel's assembly axes to the whole craft, spacing included, which differs
  on purpose from physical Basic mode (absolute per-part scales).
- **Why a finalizer on a readonly argument**. The parameter is `ref readonly double4x4`; the caller
  passes its own local. Harmony lets the prefix take it by `ref` and overwrite it, so the finalizer must
  restore it, otherwise any later use of that local in the caller (or in another mod's prefix that calls
  the method) would see the scaled matrix.

## Lifecycle and timing

| Patched code | Called from (build 5554) | Phase |
|---|---|---|
| `Vehicle.UpdateRenderData` for ordinary vessels | `Program.UpdateRenderingResources` per visible viewport, `Program.cs` L4355 | `frame:render` (`Program.Render`, before command recording) |
| `KittenEva.UpdateRenderData` (calls `base.UpdateRenderData`, then `GetWorldMatrix`) | `Program.RenderViewport` L4491 and `Program.RenderGame` L4698 | `frame:render`, before the passes are recorded |
| `PartTree.UpdateRenderData` | `Vehicle.UpdateRenderData`; I Feel Seen's replacement body | Same as its caller |
| `SetScale` / `ClearScale` registrations | Godzilla's queued `ApplyNow`/`Restore` at the `PhysicsFrameHook` handoff | `frame:prepare` |

All of it is main-thread render preparation: the patches only read the registration table and the
vessel's committed `CenterOfMassAsmb`, and never touch simulation state, so they need no worker
synchronisation. `PartTree.UpdateRenderData` composes the part, dynamic and glass draws from the ego
matrix every frame (`RenderData.Compose*`), so substituting the input scales every draw without
invalidating cached render data.[^ksa-part-tree] The physical bubble envelope keeps using
`BoundingSphereRadiusBody`, which these patches never change.

Why call-site transpilers instead of a getter patch (from the code comments, confirmed in 5554):

- `Vehicle.MeanRadius` is `BoundingSphereRadiusBody` and feeds bubble sizing, camera distance and
  terrain logic; overriding the getter globally would leak the visual size into physics.
- KSA 5482 moved the one-pixel cull out of `UpdateRenderData` into the public, tiny
  `Vehicle.IsLargeEnoughToRender(Camera)`; its only caller is `UpdateRenderData` (L3716). Patching the
  helper risks missing JIT-inlined call sites and would change culling for any future caller, so the
  call site is redirected instead. Both transpilers throw unless they find exactly one match, which
  rolls back the whole channel rather than half-installing it.[^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs) | All five patches, registration table, cull mirror |
| [godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L81-L89) | Switch to the visual channel (restore physical edits first) |
| [garrys-torch.lib/KittenScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/garrys-torch.lib/KittenScalePatches.cs) | Avatar XYZ correction used when physics is on (visual-only kittens rely on `GetWorldMatrix`) |
| [godzilla.tests/VisualScaleChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/VisualScaleChecks.cs) | Managed checks: 10,000,000x rendering, both culls, COM/XYZ, exception restore, external prefix coexistence |
| [godzilla.tests/Fixture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/Fixture.cs) | Draw fixture mirroring the 5482 `UpdateRenderData` -> `IsLargeEnoughToRender` shape |
| [godzilla.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/README.md) | Authors' notes on visual-only limits and the 5482 cull change |

## Snippets

Redirecting the single cull call in `UpdateRenderData`:

```csharp
// godzilla.lib/VisualScalePatches.cs L67-L92
    // KSA 5482 moved UpdateRenderData's pixel cull into Vehicle.IsLargeEnoughToRender. Redirect that
    // one call site instead of patching the tiny public helper: the JIT may inline it, and any future
    // caller must keep native culling.
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

    /// <summary>Mirrors <c>Vehicle.IsLargeEnoughToRender</c> using the visual render radius.</summary>
    private static bool IsLargeEnoughToRenderScaled(Vehicle vehicle, Camera camera)
    {
        double3 positionEgo = camera.GetPositionEgo(vehicle);
        return !(camera.GetObjectDiameterPixels(2.0 * RenderRadius(vehicle), positionEgo.Length()) < 1.0);
    }
```

Scaling the draw input around the live centre of mass and restoring the caller's matrix:

```csharp
// godzilla.lib/VisualScalePatches.cs L119-L143
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

The game methods being rewritten (build 5554):

```csharp
// current/decomp/KSA/Vehicle.cs L3694-L3726
	public float4x4? GetWorldMatrix(Camera camera)
	{
		double3 vector = camera.GetPositionEgo(this);
		double objectDiameterPixels = camera.GetObjectDiameterPixels(2.0 * MeanRadius, vector.Length());
		if (objectDiameterPixels < 1.0)
		{
			return null;
		}
		float4x4 float4x5 = float4x4.CreateTranslation(float3.Pack(in vector));
		float4x4 float4x6 = float4x4.CreateFromQuaternion(floatQuat.Pack(Body2Cce));
		return float4x6 * float4x5;
	}

	public bool IsLargeEnoughToRender(Camera inCamera)
	{
		double3 positionEgo = inCamera.GetPositionEgo(this);
		return !(inCamera.GetObjectDiameterPixels(2.0 * MeanRadius, positionEgo.Length()) < 1.0);
	}

	public virtual void UpdateRenderData(IViewport viewport, int inFrameIndex)
	{
		Camera camera = viewport.GetCamera();
		if (IsLargeEnoughToRender(camera))
		{
			double4x4 matrixAsmb2Ego = GetMatrixAsmb2Ego(camera);
			Parts.UpdateRenderData(in matrixAsmb2Ego, IsEditedVehicle, viewport, inFrameIndex);
			if (viewport.IsMain() && Program.ShowPhysicsDebug && IsInSameBubbleAs(Program.ControlledVehicle) && _showTerrain)
			{
				SubmitTerrainDebugOverlay(viewport);
				SubmitClutterTerrainDebugOverlay(viewport);
			}
		}
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Vehicle.UpdateRenderData(IViewport, int)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726) | Cull call-site transpiler |
| `Vehicle.IsLargeEnoughToRender(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3711) | Mirrored, not patched |
| `Vehicle.GetWorldMatrix(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3705) | Radius transpiler and scaling postfix |
| `Vehicle.MeanRadius` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L535) | `=> BoundingSphereRadiusBody`; replaced only at the one read |
| `Vehicle.CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L565) | Draw pivot |
| `PartTree.UpdateRenderData(...)`, `PartTree.OwningVehicle` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1175) | Draw-matrix substitution |
| `KittenEva.UpdateRenderData` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Kitten path through `GetWorldMatrix` |
| `Camera.GetObjectDiameterPixels`, `Camera.GetPositionEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236) | Pixel-size cull mirror |
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4340-L4358) | Where ordinary vessels' render data is built |

## Pitfalls and version notes

- Visual only means visual: picking, camera targeting, exhaust plumes, parachute cloth and physical
  contacts stay at the physical size and position.
- Shadows, LOD and clipping at planet scale were not verified in game by the authors.
- I Feel Seen's `UpdateRenderData` prefix skips the original for tracked vessels, so the cull transpiler
  does not run for them; its replacement still calls `PartTree.UpdateRenderData`, so Godzilla's draw
  scaling still applies. On `GetWorldMatrix`, Harmony still runs Godzilla's postfix after I Feel Seen's
  skipping prefix, so tracked kittens are also scaled.
- 5482 regression worth remembering: when the cull moved into `IsLargeEnoughToRender`, the old radius
  transpiler on `UpdateRenderData` found zero reads, threw, and rolled back all five patches, which
  silently disabled the visual and collider-only modes until the call-site redirect replaced it. The
  guarded-count pattern is what made the break loud.
- `KittenEva.UpdateRenderData` overrides the virtual method and calls `base` first, so kitten parts go
  through the same transpiled body before the avatar uses `GetWorldMatrix`.

## See also

- [Godzilla vessel scaling](./godzilla-vessel-scaling.md) and [Godzilla collider scaling](./godzilla-collider-scaling.md)
- [I Feel Seen render distance override](./i-feel-seen-render-distance-override.md)
- [Per-axis part scaling](./garrys-torch-per-axis-part-scaling.md)
- [Godzilla sub-mod](../submods/godzilla.md) and [mod overview](../overview.md)
- [Gate helper that skips the call for the mod's own condition](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-4.md) - how-to article demonstrating this feature
- [Enlarge the cull radius with call-site transpilers so a scaled vessel is culled sanely](../../../howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-4.md) - how-to article demonstrating this feature
- [Scale a whole vessel with separate render, physics and collider channels](../../../howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-1.md) - how-to article demonstrating this feature

[^godzilla-lib-readme]: godzilla.lib/README.md
[^ksa-part-tree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
