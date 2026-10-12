---
type: How-To
title: Draw the avatar from a ClearBuckets postfix and re-base the editor camera upright
description: Submit the kitten right after RenderEditor clears the buckets, pass the assembly-to-ego matrix unmodified, and rotate only the editor camera basis and scroll pan so the kitten stands upright
category: kittens
group: eva
topic: open-the-vehicle-editor-on-a-live-eva-kitten
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, open-the-vehicle-editor-on-a-live-eva-kitten-1]
demonstrated_by: [mods/unscience/features/iron-man-kitten-vehicle-editing]
techniques: [harmony-postfix, harmony-transpiler, reflection]
hooks:
  - harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets
  - harmony-postfix KSA.OrbitController.GetFrame2Ecl (private, by name)
  - harmony-transpiler KSA.OrbitController.EditorOnScroll (private, by name)
lifecycle: [editor:render, frame:viewports, frame:input]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.KittenRenderable
  - KSA.KittenEva
  - KSA.VehicleEditingSpace
  - KSA.OrbitController
  - KSA.Program
tags: [kitten, vehicle-editor, avatar-rendering, camera, transpiler, iron-man]
---
Once [approach 1](open-the-vehicle-editor-on-a-live-eva-kitten-1.md) has the stock editor open on a kitten, the player is editing an invisible body: the editor only draws part-tree geometry, and the kitten is not part geometry. And even when drawn, the editor camera is built for rockets, so a kitten stands on its side. This article fixes both with three patches: a postfix on `SuperMeshRenderSystem.ClearBuckets` that submits the kitten's own `KittenRenderable`, a postfix on `OrbitController.GetFrame2Ecl` that re-bases the camera upright, and a transpiler on `OrbitController.EditorOnScroll` so panning follows the same axis. Prerequisites: StarMap, Harmony and the editor session from approach 1; see [transpile a method safely](../../../patching/transpilers/index.md) for IL-patching background. Demonstrated by Unscience's Iron Man: [Open the stock vehicle editor on a live EVA kitten](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md).

## Background: how the game does it

**Why the kitten is not drawn.** Flight rendering draws kittens in the per-viewport loops of `RenderGame` and `RenderViewport`, guarded by `Editor == null`. `UpdateRenderingResources` skips `KittenEva` in its vehicle loop and, when an editor exists, uploads only the editing space's parts instead:

```csharp
// current/decomp/KSA/Program.cs L4347-L4368
			if (Editor == null)
			{
				ReadOnlySpan<Vehicle> vehiclesInFrame = VehiclesInFrame;
				for (int k = 0; k < vehiclesInFrame.Length; k++)
				{
					Vehicle vehicle = vehiclesInFrame[k];
					if (!(vehicle is KittenEva))
					{
						vehicle.UpdateRenderData(viewport2, frameIndex);
					}
				}
			}
			else
			{
				double4x4 matrixAsmb2Ego = Editor.EditingSpace.GetMatrixAsmb2Ego(MainViewport.GetCamera());
				Editor.EditingSpace.Parts?.UpdateRenderData(in matrixAsmb2Ego, isEditedVehicle: true, viewport2, frameIndex);
				foreach (PartTree unattachedPartTree in Editor.UnattachedPartTrees)
				{
					unattachedPartTree.UpdateRenderData(in matrixAsmb2Ego, isEditedVehicle: true, viewport2, frameIndex);
				}
				Editor.UpdateRenderData(in matrixAsmb2Ego, viewport2, frameIndex);
			}
```

`RenderEditor` then clears the main viewport's buckets and immediately records the prepass. The clear also empties `ShadowRenderablePool`, so anything submitted earlier in the frame is lost:

```csharp
// current/decomp/KSA/Program.cs L4984-L4987
		SuperMeshRenderSystem.ClearBuckets(RenderedViewport);
		_offscreenTarget.ClearDepthImages(commandBuffer2);
		SuperMeshRenderSystem.SetCamMatrices(_cameraData[shaderSlot].View, _cameraData[shaderSlot].Projection);
		PrePassRenderer.Render(commandBuffer2, RenderedViewport, resourceFrameIndex, SuperMeshRenderSystem, RaytracingRenderer);
```

```csharp
// current/decomp/KSA/SuperMeshRenderSystem.cs L546-L554
	public void ClearBuckets(IViewport viewport)
	{
		ViewHandle view = ViewForViewport(viewport);
		PassBucketSystem.Clear(view, MeshPass.OpaquePrePass);
		PassBucketSystem.Clear(view, MeshPass.TranslucentPrePass);
		PassBucketSystem.Clear(view, MeshPass.MainOpaque);
		PassBucketSystem.Clear(view, MeshPass.Translucency);
		ShadowRenderablePool.Clear();
	}
```

**Why the kitten lies down.** The stock editor camera frame for a `VehicleEditingSpace` maps camera +Z onto assembly +X, which is a rocket's up. A kitten is authored with feet at the origin and up along -Z (the game's own comment in `PartGameData.xml`):

```csharp
// current/decomp/KSA/OrbitController.cs L290-L293
		else if (focused is VehicleEditingSpace vehicleEditingSpace)
		{
			result = doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitY, Math.PI / 2.0), vehicleEditingSpace.Asmb2Ecl);
		}
```

`OrbitController.EditorOnScroll` ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L433)) pans `VehicleEditor.CameraOffset` along assembly +X, with bounds on `CameraOffset.X`, which is also a rocket-shaped assumption.

## Lifecycle and timing

| Patch | Runs from | Lifecycle |
|---|---|---|
| `ClearBuckets` postfix | `Program.RenderEditor`, between `ClearBuckets` and `PrePassRenderer.Render` | `editor:render` |
| `GetFrame2Ecl` postfix | `OrbitController.OnFrame` from viewport `OnFrame` in `OnFrameViewports` | `frame:viewports` |
| `EditorOnScroll` transpiler | rewritten once at patch time; the new code runs in the GLFW scroll callback | `frame:input` |

The avatar must be submitted after `ClearBuckets` and before `PrePassRenderer.Render`: a submission from `StarMapAfterGui` would be erased by the clear (the mod's own code comment says exactly this). `ClearBuckets` is also called per viewport from `RenderViewport` and `RenderGame`, so the postfix filters on the renderer, the viewport and the editor session. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the render order.

## Steps

1. **Patch `ClearBuckets` with a postfix.** Bind only `__instance`, so the patch survives the signature change that added the `IViewport` parameter. Return early unless this is the main `SuperMeshRenderSystem`, the main viewport, and the editor is editing this kitten's tree. `GetKitten(editor)` is the guard from [approach 1](open-the-vehicle-editor-on-a-live-eva-kitten-1.md).

2. **Submit the kitten with the assembly-to-ego matrix.** Pass `EditingSpace.GetMatrixAsmb2Ego` unmodified: `KittenEva.UpdateRenderData` subtracts the flight centre of mass from its own world matrix, which the editor frame must not do again. Pass `dt = 0` so the animation does not advance, a default `KittenAnimInputs`, and force `HideHead` off for the call (an IVA seat may have set it) and restore it afterwards. Catch exceptions and log once, because this runs inside the render.

```csharp
// iron-man.lib/IronManEditorPatches.cs L131-L153
    // Program.RenderEditor clears the immediate character buckets after ordinary part render
    // data is prepared. Submit here, before the prepass; submitting in AfterGui would be erased.
    private static void RenderCharacter(SuperMeshRenderSystem __instance)
    {
        VehicleEditor? editor = Program.Editor;
        KittenEva? kitten = GetKitten(editor);
        if (kitten == null || !ReferenceEquals(__instance, Program.Instance.SuperMeshRenderSystem)
            || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport)
            || !ReferenceEquals(editor!.EditingSpace.Parts, kitten.Parts)) return;
        try
        {
            IViewport viewport = Program.RenderedViewport;
            float4x4 transform = float4x4.Pack(editor.EditingSpace.GetMatrixAsmb2Ego(viewport.GetCamera()));
            LocomotionState locomotion = kitten.LocomotionState;
            KittenAnimInputs inputs = default;
            bool hideHead = kitten.Renderable.HideHead;
            try
            {
                kitten.Renderable.HideHead = false;
                kitten.Renderable.UpdateRenderData(viewport, Program.Instance.ResourceFrameIndex, 0.0,
                    transform, double3.Zero, double3.Zero, in locomotion, in inputs);
            }
            finally { kitten.Renderable.HideHead = hideHead; }
```

`KittenRenderable.UpdateRenderData` and `HideHead` are in [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311); the flight-time call that this replaces is [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078).

3. **Re-base the camera with a postfix on `GetFrame2Ecl`.** Target the private method by name and parameter types. For the configured editing space and `CameraReferenceFrame.Editor` only, replace the result with a half-turn about X composed with `Asmb2Ecl`. Only the camera basis changes, so geometry, connectors, picking and gizmos rotate together:

```csharp
// iron-man.lib/IronManEditorOrientationPatches.cs L59-L67
    private static void FramePostfix(IFollowable focused, CameraReferenceFrame referenceFrame, ref doubleQuat __result)
    {
        var space = GetEditingSpace();
        if (space == null || !ReferenceEquals(focused, space) || referenceFrame != CameraReferenceFrame.Editor) return;
        // Stock rotates camera-frame +Z to rocket assembly +X. A kitten's up is -Z.
        // Changing only this camera basis rotates the complete viewed assembly together;
        // Asmb2Ecl and every body, equipment, connector, picking and gizmo transform stay intact.
        __result = doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, Math.PI), space.Asmb2Ecl);
    }
```

4. **Transpile `EditorOnScroll`.** The handler calls `double3.UnitX` twice and reads `CameraOffset.X` twice. Replace each `UnitX` getter call with a helper returning `-UnitZ` (the kitten's up) when the editor is on a configured kitten, and each `ldflda CameraOffset; ldfld X` pair with `ldfld CameraOffset; call PanCoordinate`, which projects the offset onto the same axis. Keep the instruction objects so labels and exception blocks stay valid, and throw unless exactly two of each were found:

```csharp
// iron-man.lib/IronManEditorOrientationPatches.cs L69-L76
    private static double3 PanAxisBody() => GetEditingSpace() == null ? double3.UnitX : -double3.UnitZ;

    private static double PanCoordinate(double3 offset)
    {
        var space = GetEditingSpace();
        if (space == null) return offset.X;
        double3 axis = (-double3.UnitZ).Transform(space.Asmb2Ecl);
        return offset.X * axis.X + offset.Y * axis.Y + offset.Z * axis.Z;
```

```csharp
// iron-man.lib/IronManEditorOrientationPatches.cs L79-L109
    private static IEnumerable<CodeInstruction> ScrollTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(code => new CodeInstruction(code)).ToList();
        var unitX = AccessTools.PropertyGetter(typeof(double3), nameof(double3.UnitX));
        var offset = AccessTools.Field(typeof(VehicleEditor), nameof(VehicleEditor.CameraOffset));
        var x = AccessTools.Field(typeof(double3), nameof(double3.X));
        var axisHelper = AccessTools.Method(typeof(IronManEditorOrientationPatches), nameof(PanAxisBody));
        var coordinateHelper = AccessTools.Method(typeof(IronManEditorOrientationPatches), nameof(PanCoordinate));
        int axes = 0;
        int coordinates = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(unitX))
            {
                codes[i].operand = axisHelper;
                axes++;
            }
            if (i + 1 < codes.Count && codes[i].opcode == OpCodes.Ldflda && Equals(codes[i].operand, offset)
                && codes[i + 1].opcode == OpCodes.Ldfld && Equals(codes[i + 1].operand, x))
            {
                // Replace CameraOffset.X with projection onto the same up axis used to pan.
                // Keep instruction objects (including labels/exception blocks) in place.
                codes[i].opcode = OpCodes.Ldfld;
                codes[i + 1].opcode = OpCodes.Call;
                codes[i + 1].operand = coordinateHelper;
                coordinates++;
            }
        }
        if (axes != 2 || coordinates != 2)
            throw new InvalidOperationException($"iron-man: unexpected editor pan layout ({axes} axes, {coordinates} bounds); expected two of each.");
        return codes;
```

5. **Install in one transaction.** Resolve both targets before patching, patch inside a `try`, and unpatch on failure so a half-installed camera never ships:

```csharp
// iron-man.lib/IronManEditorOrientationPatches.cs L19-L37
    public static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _frameTarget = AccessTools.Method(typeof(OrbitController), "GetFrame2Ecl",
            new[] { typeof(IFollowable), typeof(CameraReferenceFrame) })
            ?? throw new MissingMethodException("OrbitController.GetFrame2Ecl(IFollowable, CameraReferenceFrame)");
        _scrollTarget = AccessTools.Method(typeof(OrbitController), "EditorOnScroll")
            ?? throw new MissingMethodException("OrbitController.EditorOnScroll");
        try
        {
            harmony.Patch(_frameTarget, postfix: new HarmonyMethod(typeof(IronManEditorOrientationPatches), nameof(FramePostfix)));
            harmony.Patch(_scrollTarget, transpiler: new HarmonyMethod(typeof(IronManEditorOrientationPatches), nameof(ScrollTranspiler)));
            _applied = true;
        }
        catch
        {
            Remove(harmony);
            throw;
        }
```

6. **Check the result.** In the editor the kitten stands upright, orbit and pan feel natural, and returning to flight restores the flight camera. In the log, nothing: a transpiler mismatch throws at install time, not per frame.

## Complete example

- [iron-man.lib/IronManEditorOrientationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorOrientationPatches.cs) - the camera basis postfix and the scroll transpiler.
- [iron-man.lib/IronManEditorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorPatches.cs) - `RenderCharacter`, the `ClearBuckets` postfix.
- Feature page: [Open the stock vehicle editor on a live EVA kitten](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md).

## Variations and gotchas

- **`ClearBuckets` changed shape.** It took no arguments before rev 5474 and is `ClearBuckets(IViewport)` in build 2026.10.10.5554; the postfix binds only `__instance`, so it survived. It is called per viewport, hence the filters.
- **IL-shape matching.** The transpiler counts two `UnitX` getters and two `ldflda CameraOffset; ldfld X` pairs. A recompiled `EditorOnScroll` with a different shape makes installation fail rather than misbehave.
- **The pose is the last flight pose.** `dt = 0` and a default `KittenAnimInputs` mean the kitten shows whatever pose the previous flight frame left.
- **Only the camera basis moves.** `Asmb2Ecl`, connector frames and picking are untouched, so exported blueprints are not rotated.
- **The clear also empties the shadow pool.** `ClearBuckets` calls `ShadowRenderablePool.Clear()`, so anything you submit earlier in the frame is lost.

## See also

- [Approach 1: open the editor and guard the body](open-the-vehicle-editor-on-a-live-eva-kitten-1.md), [approach 3: private attachment nodes](open-the-vehicle-editor-on-a-live-eva-kitten-3.md), [topic overview](overview.md)
- Mod feature: [Iron Man kitten vehicle editing](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md)
- [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [Hide a kitten's body from one viewport](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
