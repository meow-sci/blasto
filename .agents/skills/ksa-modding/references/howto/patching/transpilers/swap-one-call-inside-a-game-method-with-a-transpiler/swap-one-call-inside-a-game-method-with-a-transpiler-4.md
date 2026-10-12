---
type: How-To
title: Gate helper that skips the call for the mod's own condition
description: Turn a callvirt Draw into a static gate that forwards to Draw only while a flag is clear, and redirect IsLargeEnoughToRender and MeanRadius reads in two Vehicle methods to scale-aware mirrors, with a guarded count check on each and a reason a getter patch would be wrong
category: patching
group: transpilers
topic: swap-one-call-inside-a-game-method-with-a-transpiler
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/humble-arteest-kitten-visor-toggle
  - mods/unscience/features/godzilla-visual-scaling
techniques: [harmony-transpiler, call-site-rewrite, gate]
hooks:
  - harmony-transpiler KSA.KittenRenderable.UpdateRenderData
  - harmony-transpiler KSA.Vehicle.UpdateRenderData
  - harmony-transpiler KSA.Vehicle.GetWorldMatrix
lifecycle: [startup:all-mods-loaded, frame:render, frame:draw-ui]
game_types: [KSA.KittenRenderable, KSA.CharacterAvatar, KSA.StaticMeshRenderable, KSA.Vehicle]
tags: [harmony, transpiler, call-site, gate, draw, culling, visual-scale]
---

Two unscience features swap a call for a helper that decides whether, or on what, to act. The visor toggle turns `callvirt StaticMeshRenderable::Draw(ViewHandle)`
into a static call to `DrawVisor(visor, view)` that forwards to `Draw` only while a `Hidden` flag is false. Godzilla redirects the one-pixel cull
(`Vehicle.IsLargeEnoughToRender`) and the radius read in `GetWorldMatrix` (`get_MeanRadius`) to mirrors that use a visual size, so a vessel scaled
ten million times is not culled. Each rewrite is exactly-one guarded, and the articles explain why a getter patch would be wrong.

Prerequisites: a StarMap mod with `0Harmony` and the same-signature swap from [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md). Demonstrated in
[Hide kitten helmet visors with a draw-call transpiler](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md) and [Render-only vessel scaling with draw-matrix and cull-radius patches](../../../../mods/unscience/features/godzilla-visual-scaling.md).

## Background: how the game does it

`KittenRenderable.UpdateRenderData` draws the helmet shell and, one line later, the visor, with the same method on different receivers. Since build 5482 a
`ViewHandle` local is loaded between the field load and the call (meshes are bucketed per view):

```text
// KSA.dll IL via ilspycmd -il: KSA.KittenRenderable::UpdateRenderData (parameter lists shortened)
IL_03ae: ldarg.0
IL_03af: ldfld class KSA.CharacterAvatar KSA.KittenRenderable::_characterAvatar
IL_03b4: ldflda valuetype KSA.CharacterAvatar/CharacterAttachments KSA.CharacterAvatar::Attachments
IL_03b9: ldflda valuetype KSA.CharacterAvatar/Helmet KSA.CharacterAvatar/CharacterAttachments::Helmet
IL_03be: ldfld class KSA.StaticMeshRenderable KSA.CharacterAvatar/Helmet::HelmetMesh
IL_03c3: ldloc.s 5
IL_03c5: callvirt instance void KSA.StaticMeshRenderable::Draw(...)
IL_03ca: nop
IL_03cb: ldarg.0
IL_03cc: ldfld class KSA.CharacterAvatar KSA.KittenRenderable::_characterAvatar
IL_03d1: ldflda valuetype KSA.CharacterAvatar/CharacterAttachments KSA.CharacterAvatar::Attachments
IL_03d6: ldflda valuetype KSA.CharacterAvatar/Helmet KSA.CharacterAvatar/CharacterAttachments::Helmet
IL_03db: ldfld class KSA.StaticMeshRenderable KSA.CharacterAvatar/Helmet::VisorMesh
IL_03e0: ldloc.s 5
IL_03e2: callvirt instance void KSA.StaticMeshRenderable::Draw(...)
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

For a hidden visor the shader cannot help: `ModelTranslucent.frag` hard-codes `opacity = 0.75` for the non-eye branch, so material alpha has no effect
([current/Content/Core/Shaders/Mesh/ModelTranslucent.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelTranslucent.frag#L68-L81)[^ksa-modeltranslucent]).

For scaling, the cull and the radius read live in `Vehicle`:

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

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)[^ksa-vehicle]

and in the IL, `UpdateRenderData` calls `IsLargeEnoughToRender` once and `GetWorldMatrix` reads `get_MeanRadius` once (`callvirt Astronomical::get_MeanRadius`, since
`Vehicle` derives from `Astronomical`).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Transpilers applied | `[StarMapAllModsLoaded]`, unscience `Patcher.Patch()` then `TryApply` | `startup:all-mods-loaded` |
| Visor toggle | Kitten Color panel button in `[StarMapAfterGui]` | `frame:draw-ui` |
| Patched draw and cull run | Inside `Program.Render`: ordinary vessels in `UpdateRenderingResources`; kittens per viewport in `RenderViewport` and `RenderGame` | `frame:render` |

Kittens are not updated in `UpdateRenderingResources`; their render data is built per viewport at the start of command recording, and `Draw(view)` only enqueues
instances into that view's mesh buckets, which the later passes consume. A toggle set in the GUI phase therefore applies to the same frame, in every view
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4495)[^ksa-program], [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4690-L4702)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Match the receiver, not just the method.** The helmet shell is drawn with the same `Draw(ViewHandle)` one line earlier, so the visor match is a
   three-instruction window: `ldfld VisorMesh`, any `ldloc`, a call to `Draw`. Anything other than exactly one match throws:

```csharp
// humble-arteest.lib/KittenVisorPatches.cs L53-L76
private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
{
    var code = new List<CodeInstruction>(instructions);
    var visorField = AccessTools.Field(typeof(CharacterAvatar.Helmet), nameof(CharacterAvatar.Helmet.VisorMesh));
    // KSA 5482 draws per render view: VisorMesh.Draw(view), with the view loaded between.
    var draw = AccessTools.Method(typeof(StaticMeshRenderable), nameof(StaticMeshRenderable.Draw),
        new[] { typeof(ViewHandle) });
    var replacement = AccessTools.Method(typeof(KittenVisorPatches), nameof(DrawVisor));
    int matchIndex = -1;
    int matches = 0;

    // Match the receiver, not just Draw(view): the adjacent helmet draw must stay stock.
    for (int i = 2; i < code.Count; i++)
    {
        if (code[i - 2].opcode == OpCodes.Ldfld && Equals(code[i - 2].operand, visorField)
            && code[i - 1].IsLdloc() && code[i].Calls(draw))
        {
            matchIndex = i;
            matches++;
        }
    }

    if (matches != 1)
        throw new InvalidOperationException($"Expected one VisorMesh.Draw call, found {matches}.");
```

Source: [humble-arteest.lib/KittenVisorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenVisorPatches.cs#L53-L76)[^unscience-kittenvisorpatches]

2. **Swap opcode and operand on the existing instruction.** Only the opcode (`callvirt` becomes `call`, because the replacement is static) and the operand
   change, on the same `CodeInstruction`, so its labels and blocks stay valid. The stack contract is unchanged: it consumes the mesh reference and the view and
   returns void:

```csharp
// humble-arteest.lib/KittenVisorPatches.cs L77-L88

    // Same stack effect: consume the mesh reference and view, return void. Preserve labels/blocks.
    code[matchIndex].opcode = OpCodes.Call;
    code[matchIndex].operand = replacement;
    return code;
}

private static void DrawVisor(StaticMeshRenderable visor, ViewHandle view)
{
    if (!Hidden)
        visor.Draw(view);
}
```

Source: [humble-arteest.lib/KittenVisorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenVisorPatches.cs#L77-L88)

3. **Redirect the cull call.** `UpdateRenderData` makes one `call Vehicle.IsLargeEnoughToRender(Camera)`; the transpiler replaces it with
   `IsLargeEnoughToRenderScaled(Vehicle, Camera)`, a static with the instance as the first parameter (the stack already has `this` then the camera):

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

Source: [godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs#L67-L92)[^unscience-visualscalepatches]

4. **Redirect only the radius read in `GetWorldMatrix`.** The instance getter `get_MeanRadius` becomes `RenderRadius(Vehicle)`, a static with the same stack effect
   (instance in, `double` out). The match accepts `call` or `callvirt` and a declaring type assignable from `Vehicle`:

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

Source: [godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs#L94-L117)

5. **Guard the count; let one failure roll back the whole channel.** Both transpilers throw unless exactly one call matched, and `Apply` removes every patch it
   installed on a failure. 

## Complete example

| File | Role |
|---|---|
| [humble-arteest.lib/KittenVisorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenVisorPatches.cs) | Visor gate transpiler and `DrawVisor` |
| [godzilla.lib/VisualScalePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/godzilla.lib/VisualScalePatches.cs) | Cull and radius redirects, world-matrix postfix, part-tree prefix and finalizer |

## Variations and gotchas

- **Why not patch the getter.** `Vehicle.MeanRadius` is the bounding-sphere radius and feeds bubble sizing, camera distance and terrain logic; overriding the
  getter globally would leak the visual size into physics. Redirecting the call site inside `GetWorldMatrix` changes one reader.
- **Why not patch `IsLargeEnoughToRender`.** It is a tiny public method whose only caller is `UpdateRenderData` (L3716); the JIT may inline it, and any
  future caller must keep native culling. Redirecting the call site is selective and robust to inlining.
- **A regression that the count caught.** When 5482 moved the cull from `UpdateRenderData` into `IsLargeEnoughToRender`, the old radius transpiler found zero
  reads, threw, and rolled back all five patches, so the visual and collider-only modes were silently off until the call-site redirect replaced it. The guarded count
  is what made the break loud.
- **Interaction.** I Feel Seen's `UpdateRenderData` prefix skips the original for tracked vessels, so the cull transpiler does not run for them; its replacement
  still calls `PartTree.UpdateRenderData`, so the draw scaling still applies.
- **A simpler alternative for the visor.** `StaticMeshRenderable` has a public `Visible` property that `Draw` checks; setting it on each kitten's avatar would also
  work but requires walking every kitten and catching new spawns. The transpiler gates all of them with one static flag.

## See also

- [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md), [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md), [Replacement that does mod work, then calls the original](swap-one-call-inside-a-game-method-with-a-transpiler-3.md), [Adapter that fixes up arguments and converts the result back](swap-one-call-inside-a-game-method-with-a-transpiler-5.md)
- [Swapping one call inside a game method with a transpiler](overview.md)
- [Hide kitten helmet visors with a draw-call transpiler](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md), [Render-only vessel scaling with draw-matrix and cull-radius patches](../../../../mods/unscience/features/godzilla-visual-scaling.md)
- [Changing behaviour of a game method marked AggressiveInlining](../../internals/patch-around-aggressive-inlining/overview.md)
- Related tasks: scale-a-vessel-or-its-parts-at-runtime, keep-vessels-rendered-at-any-distance

[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^ksa-modeltranslucent]: current/Content/Core/Shaders/Mesh/ModelTranslucent.frag (build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^unscience-kittenvisorpatches]: unscience/humble-arteest.lib/KittenVisorPatches.cs
[^unscience-visualscalepatches]: unscience/godzilla.lib/VisualScalePatches.cs
