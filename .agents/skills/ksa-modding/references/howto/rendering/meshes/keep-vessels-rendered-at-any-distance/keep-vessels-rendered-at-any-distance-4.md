---
type: How-To
title: Enlarge the cull radius with call-site transpilers so a scaled vessel is culled sanely
description: Redirect the single IsLargeEnoughToRender call and the single MeanRadius read in the vessel draw path to a scaled-radius mirror, guarded by exactly-one-match checks, while the draw matrix is scaled around the centre of mass
category: rendering
group: meshes
topic: keep-vessels-rendered-at-any-distance
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/godzilla-visual-scaling]
techniques: [harmony-transpiler, harmony-prefix, harmony-postfix, harmony-finalizer]
hooks:
  - harmony-transpiler KSA.Vehicle.UpdateRenderData
  - harmony-transpiler KSA.Vehicle.GetWorldMatrix
  - harmony-postfix KSA.Vehicle.GetWorldMatrix
  - harmony-prefix KSA.PartTree.UpdateRenderData
  - harmony-finalizer KSA.PartTree.UpdateRenderData
lifecycle: [frame:render, frame:prepare]
game_types: [KSA.Vehicle, KSA.PartTree, KSA.KittenEva, KSA.Camera, KSA.Program]
tags: [cull, render-distance, scaling, transpiler, call-site, visual-only]
---
Outcome: a vessel (and an EVA kitten) drawn many times larger than its physical size, without disappearing when its true size would be under a pixel, and without leaking the visual size into physics. The cull helpers stay stock for every other vessel: the transpilers redirect individual call sites to mirrors that use a per-vessel render radius.

Demonstrated by unscience's Godzilla visual-only scale ([Render-only vessel scaling with draw-matrix and cull-radius patches](../../../../mods/unscience/features/godzilla-visual-scaling.md)). Prerequisites: StarMap, Harmony including transpilers, `ConditionalWeakTable` or similar per-vessel storage. This pairs the cull change with the draw-matrix scale; for plain "never cull" see approaches 1 to 3.

## Background: how the game does it

`Vehicle.MeanRadius` is `BoundingSphereRadiusBody` and feeds bubble sizing, camera distance and terrain logic ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L535)), so overriding the getter would leak the visual size into physics. Both pixel culls read it:

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

`PartTree.UpdateRenderData` composes the part, dynamic and glass draws from the assembly-to-ego matrix every frame ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1171)), so substituting its input scales every draw without invalidating cached render data. That matrix already subtracts the live centre of mass (`Vehicle.CenterOfMassAsmb`, [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L565)).

## Lifecycle and timing

| Patched code | Called from | Phase |
|---|---|---|
| `Vehicle.UpdateRenderData` for ordinary vessels | `Program.UpdateRenderingResources` per visible viewport ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4347-L4357)) | `frame:render` |
| `KittenEva.UpdateRenderData` (calls `base.UpdateRenderData`, then `GetWorldMatrix`) | `RenderViewport` L4491 and `RenderGame` L4698 | `frame:render`, before passes are recorded |
| `PartTree.UpdateRenderData` | `Vehicle.UpdateRenderData`, or another mod's replacement body | same as its caller |
| Setting or clearing a factor | Godzilla's queued apply at the physics handoff hook | `frame:prepare` |

All main-thread render preparation: the patches only read the registration table and the vessel's committed `CenterOfMassAsmb`, and never touch simulation state.

## Steps

1. **Store a factor per vessel** (`ConditionalWeakTable<Vehicle, Scale>`), install all five patches together and remove all on any failure:

   ```csharp
// godzilla.lib/VisualScalePatches.cs L13-L65
public static class VisualScalePatches
{
    private sealed record Scale(float3 Factor);
    private static ConditionalWeakTable<Vehicle, Scale> _scales = new();
    private static readonly List<(MethodInfo Target, MethodInfo Patch)> Installed = new();
    public static bool IsApplied { get; private set; }

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

    public static void Remove(Harmony harmony)
    {
        IsApplied = false;
        foreach (var (target, patch) in Installed) harmony.Unpatch(target, patch);
        Installed.Clear();
        _scales = new();
    }

    internal static void SetScale(Vehicle vehicle, float3 factor)
    {
        _scales.Remove(vehicle);
        _scales.Add(vehicle, new(factor));
    }

    internal static void ClearScale(Vehicle vehicle) => _scales.Remove(vehicle);

    private static void Install(Harmony harmony, Type type, string name, string patchName,
        bool transpiler, bool postfix = false, bool finalizer = false)
    {
        var target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        var patch = AccessTools.Method(typeof(VisualScalePatches), patchName);
        Installed.Add((target, patch));
        var method = new HarmonyMethod(patch);
        harmony.Patch(target, prefix: !transpiler && !postfix && !finalizer ? method : null,
            postfix: postfix ? method : null, transpiler: transpiler ? method : null,
            finalizer: finalizer ? method : null);
    }
```

2. **Redirect the one `IsLargeEnoughToRender` call in `UpdateRenderData`** to a mirror using the visual radius. Redirect the call site rather than patching the tiny public helper: the JIT may inline it, and any future caller must keep native culling. Throw unless exactly one call is found, so a change rolls back the whole channel instead of half-installing:

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

3. **Redirect the one `MeanRadius` read in `GetWorldMatrix`**, again guarded by an exact match count:

   ```csharp
// godzilla.lib/VisualScalePatches.cs L94-L117
// Replace only the radius read inside GetWorldMatrix. A global MeanRadius override would
// leak straight back into physics-bubble sizing, camera positioning and terrain logic.
private static IEnumerable<CodeInstruction> RenderRadiusTranspiler(IEnumerable<CodeInstruction> instructions)
{
    int matches = 0;
    foreach (var instruction in instructions)
    {
        if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
            instruction.operand is MethodInfo method && method.Name == "get_MeanRadius" &&
            method.ReturnType == typeof(double) && method.DeclaringType!.IsAssignableFrom(typeof(Vehicle)))
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(VisualScalePatches), nameof(RenderRadius));
            matches++;
        }
        yield return instruction;
    }
    if (matches != 1) throw new InvalidOperationException($"Expected one render radius read; found {matches}.");
}

private static double RenderRadius(Vehicle vehicle) =>
    IsApplied && _scales.TryGetValue(vehicle, out var scale)
        ? vehicle.MeanRadius * Math.Max(scale.Factor.X, Math.Max(scale.Factor.Y, scale.Factor.Z))
        : vehicle.MeanRadius;
```

4. **Scale the kitten's world matrix in a postfix** and **scale the part tree's draw matrix in a prefix with a restoring finalizer** (`PartTree.UpdateRenderData`'s matrix parameter is `ref readonly`; the finalizer puts the caller's local back even if the original throws):

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

## Complete example

| File | Role |
|---|---|
| [godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs) | All five patches, registration table, cull mirror |
| [godzilla.lib/VesselScaleSnapshot.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VesselScaleSnapshot.cs#L81-L89) | Switch to the visual channel |
| [godzilla.tests/VisualScaleChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.tests/VisualScaleChecks.cs) | Checks: 10,000,000x rendering, both culls, coexistence with an external prefix |

## Variations and gotchas

- **Why transpilers.** Patching the getter or the helper changes more than the draw path; the call-site redirect changes exactly the two reads.
- **Guarded counts are what make a break loud.** When the cull moved into `IsLargeEnoughToRender` in an earlier build, the old radius transpiler on `UpdateRenderData` found zero reads, threw and rolled back all patches, silently disabling the mode until the call-site redirect replaced it.
- **Visual only means visual.** Picking, camera targeting, exhaust plumes, parachute cloth and physical contacts stay at physical size. Shadows, LOD and clipping at planet scale were not verified in game by the authors.
- **Coexistence.** I Feel Seen's skipping prefix replaces `UpdateRenderData` for tracked vessels, so the cull transpiler does not run for them; its replacement still calls `PartTree.UpdateRenderData`, so the draw scaling applies, and `GetWorldMatrix`'s postfix still runs after the skipping prefix.
- **Kitten parts.** `KittenEva.UpdateRenderData` calls `base` first, so kitten parts go through the same transpiled body before the avatar uses `GetWorldMatrix`.

## See also

- [Overview](overview.md)
- [Approach 1](keep-vessels-rendered-at-any-distance-1.md), [approach 2](keep-vessels-rendered-at-any-distance-2.md), [approach 3](keep-vessels-rendered-at-any-distance-3.md)
- [Render-only vessel scaling with draw-matrix and cull-radius patches](../../../../mods/unscience/features/godzilla-visual-scaling.md)
- The topic on scaling a vessel or its parts at runtime (vehicles-and-parts category, not yet written)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
