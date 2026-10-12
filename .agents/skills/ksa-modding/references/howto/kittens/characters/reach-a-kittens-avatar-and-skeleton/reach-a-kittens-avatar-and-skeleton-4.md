---
type: How-To
title: Know where and when a kitten is animated and drawn, per viewport
description: An engine explainer for the kitten draw path - viewports record secondary first and the main one last, KittenEva.UpdateRenderData animates and submits per view, dt is zeroed after the first call in a frame, processors are culled by distance times Priority, and Draw fills the shadow pool
category: kittens
group: characters
topic: reach-a-kittens-avatar-and-skeleton
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksastructures/features/first-person-body-hiding-and-shadow
  - mods/unscience/features/humble-arteest-kitten-visor-toggle
  - mods/ksacars/features/seated-crew-of-unflown-craft
  - mods/unscience/features/kitten-animations-forced-clip-playback
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.KittenEva.UpdateRenderData
  - harmony-postfix KSA.AnimatedRenderable.Draw
  - harmony-postfix KSA.Vehicle.UpdateSeatedCrewRenderData
  - harmony-prefix KSA.AnimatedRenderable.UpdateAnimation
lifecycle: [frame:render, render:prepass, render:shadows, frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.AnimatedRenderable
  - KSA.IAnimProcessor
  - KSA.IVASeat
  - KSA.Vehicle
tags: [kitten, render-order, viewports, animation, shadows, background]
---
Most kitten mod bugs are timing bugs: a toggle that applies one frame late, a pose that shows in the camera window but not the main view, a processor that never runs, a shadow that follows the wrong body. All of them follow from the order in which the game animates and draws a kitten. This article assembles that order from four features (first-person body hiding, the visor toggle, seated crew on unflown craft, and forced clip playback) so you can pick the right hook before writing code. It is an explainer rather than a recipe; the techniques it points to have their own topics. Prerequisites: StarMap, and Harmony for the hooks mentioned.

## Background: how the game does it

### 1. Kittens are drawn inside the render, once per viewport

`Program.UpdateRenderingResources` updates vehicle render data for the visible viewports but **skips** `KittenEva` in that loop ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4349-L4357)). Kittens are instead submitted at the start of command recording for each viewport:

```csharp
// current/decomp/KSA/Program.cs L4481-L4496
		SuperMeshRenderSystem.SetViewOptions(viewport);
		SuperMeshRenderSystem.ClearBuckets(RenderedViewport);
		if (Editor == null)
		{
			ReadOnlySpan<Vehicle> vehiclesInFrame = VehiclesInFrame;
			for (int i = 0; i < vehiclesInFrame.Length; i++)
			{
				Vehicle vehicle = vehiclesInFrame[i];
				if (vehicle is KittenEva)
				{
					vehicle.UpdateRenderData(viewport, frameIndex);
				}
				vehicle.UpdateParachuteRenderData(viewport);
			}
			ControlledVehicle?.UpdateSeatedCrewRenderData(viewport, frameIndex);
		}
```

That is `RenderViewport` (secondary and portrait viewports); `RenderGame` runs the same loop for the main viewport ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4687-L4703)). `RenderGame` first calls `RenderViewport` for every visible non-main viewport, then switches to `MainViewport`, so **camera windows and crew portraits record before the main view** ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4672-L4686)). When the vehicle editor is open (`Editor != null`) the loop is skipped, kittens are not drawn this way at all ([editor topic](../../eva/open-the-vehicle-editor-on-a-live-eva-kitten/overview.md)).

### 2. One call animates, skins and submits

`KittenEva.UpdateRenderData` computes the world matrix for the viewport's camera and calls `KittenRenderable.UpdateRenderData`, which does everything in order ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1077), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L406)):

| Order | What | Notes |
|---|---|---|
| 1 | `dt` is set to 0 unless `Program.FrameNumber != _lastAnimAdvanceFrameNumber` | only the **first** call in a frame advances animation time |
| 2 | `UpdateFurShellCount`, `UpdateAnimationLod` | sets `ShouldUpdate` on the model and on each processor by distance |
| 3 | `UpdateLocomotionAnimationState` (or `UpdateSeatedAnimationState`) | the game's clip choice: `SetAnimation` / `PlayAnimation` / `FreezeAnimation` writes |
| 4 | `_catExpressionAnim.ExpressionWeight` damped toward acceleration | the game rewrites this each frame |
| 5 | `CharacterModel.Transform = ModelToBodyMatrix * transform`; `CharacterModel.UpdateAnimation(dt2)` | `dt2` includes simulation speed and playback rate |
| 6 | `HideMaskedMeshes = HideHead`; `CharacterModel.Draw(view)`; fur; helmet, visor; MMU; cosmetic attachments | `Draw` enqueues bucket instances and shadow-pool entries |

### 3. What `UpdateAnimation` does per call

```csharp
// current/decomp/KSA/AnimatedRenderable.cs L145-L179
		int boneCount = Skeleton.BoneCount;
		Span<float4x4> span = MeshMainRenderer.AllocateSkinningMatrices(boneCount, out AnimationIndex);
		if (Program.FrameNumber != _lastPoseUpdateFrameNumber)
		{
			_lastPoseUpdateFrameNumber = Program.FrameNumber;
			if (!FreezeAnimation)
			{
				RuntimeAnim.Update((float)dt);
			}
			if (_processedPose == null || _processedPose.Length < boneCount)
			{
				_processedPose = new TransformTRS[boneCount];
			}
			Span<TransformTRS> span2 = _processedPose.AsSpan(0, boneCount);
			RuntimeAnim.Transforms.AsSpan().CopyTo(span2);
			foreach (IAnimProcessor animProcessor in AnimProcessors)
			{
				if (animProcessor.ShouldUpdate)
				{
					animProcessor.UpdateLocalPose(Transform, Skeleton, span2, (float)dt);
				}
			}
		}
		if (_processedPose != null)
		{
			Skeleton.UpdateLocalTransforms(_processedPose.AsSpan(0, boneCount));
			Skeleton.UpdateWorldTransforms(0);
		}
		foreach (IAnimProcessor animProcessor2 in AnimProcessors)
		{
			if (animProcessor2.ShouldUpdate)
			{
				animProcessor2.UpdateSkeleton(Transform, Skeleton, (float)dt);
			}
		}
```

- The clip is sampled, the processors' `UpdateLocalPose` run, and the pose is stored **once per frame** (guarded by `Program.FrameNumber`). Every later view in the same frame reuses that processed pose.
- The world transforms are rebuilt, and every processor's `UpdateSkeleton` runs again, **on every call, so once per view**. That is the only place a per-view edit (such as folding the body for the main view) can live. Skinning matrices `InverseBindPose[i] * WorldTransform[i]` are computed straight after, in the same call ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L180-L185)).
- `SkinningPoseIsViewportInvariant` would reuse one skin for all views; only parachutes set it ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L140-L144)).

### 4. Distance culling by priority

```csharp
// current/decomp/KSA/KittenRenderable.cs L704-L726
	private void UpdateAnimationLod(AnimatedRenderable animatedRenderable, float3 translation, float animationCullDistance = 45f)
	{
		float num = translation.LengthSquared();
		float num2 = animationCullDistance * animatedRenderable.Priority;
		if (num > num2 * num2)
		{
			animatedRenderable.ShouldUpdate = false;
			return;
		}
		animatedRenderable.ShouldUpdate = true;
		foreach (IAnimProcessor animProcessor in animatedRenderable.AnimProcessors)
		{
			float num3 = animationCullDistance * animProcessor.Priority;
			if (num > num3 * num3)
			{
				animProcessor.ShouldUpdate = false;
			}
			else
			{
				animProcessor.ShouldUpdate = true;
			}
		}
	}
```

`translation` is the kitten's position in the drawing view's ego frame, so the distance is from the camera of the view being drawn. The model culls at 45 m times its `Priority` (default 5, so 225 m); each processor at 45 m times its own. The stock processors use 0.8 (eyes), 1 and 1 (expressions) and 2 (ears), so a custom processor with `Priority` 1 stops 45 m out. The engine **writes** `ShouldUpdate` on your processor every frame, so setting it yourself does not stick.

### 5. The shadow pool

`AnimatedRenderable.Draw` appends one `ShadowRenderable` per shadow-casting mesh to `SuperMeshRenderSystem.ShadowRenderablePool` ([current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L235-L257)). `ClearBuckets` empties the pool, and so does `OnFrameEnd`; `RenderGame` consumes it for the lamp shadow atlas (`SuperMeshShadowCasterProvider`) and then for each sun cascade (`AddPoolToShadowBucket`) ([current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L524-L561), [current/decomp/KSA/SuperMeshShadowCasterProvider.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshShadowCasterProvider.cs#L147-L155), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4756-L4760)). Because the main viewport clears the pool just before it submits its own kittens, what the shadow passes see is **the main view's entries**, whatever the other views drew.

### 6. Seated crew

`Vehicle.UpdateSeatedCrewRenderData` draws the seated kittens of the **controlled** vehicle only (`if (this == Program.ControlledVehicle)`), each through `IVASeat.UpdateSeatedRenderable` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741), [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L105)). IVA seats set `HideHead = IsCameraInThisSeat(viewport)` per viewport, so the stock head hiding is per view too.

## Lifecycle and timing

| Moment | Code that runs | Lifecycle |
|---|---|---|
| UI pass | `StarMapBeforeGui` / `StarMapAfterGui` handlers: set flags, install or remove patches and processors | `frame:draw-ui` |
| Start of each viewport's recording | `ClearBuckets`, then `KittenEva.UpdateRenderData` per kitten, then `UpdateSeatedCrewRenderData` | `frame:render` |
| Inside that call, once per frame | `UpdateLocomotionAnimationState`, `RuntimeAnim.Update`, every processor's `UpdateLocalPose` | `frame:render` |
| Inside that call, per view | `Skeleton` world rebuild, every processor's `UpdateSkeleton`, skin matrices, `Draw` | `frame:render` |
| Later in `RenderGame` | pre-pass, lamp shadow atlas, sun cascades, colour passes consume the buckets and pool | `render:prepass`, `render:shadows` |
| After the frame | `StarMapAfterOnFrame`: safe to mutate `AnimProcessors` | `frame:after-on-frame` |

Everything on this path is the main thread. A toggle written in the UI pass therefore applies to the same frame in every view, because the buckets are filled later in the frame. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

Use this as a checklist when you pick a hook.

1. **To make something view-dependent, learn which view is being drawn.** KSAStructures wraps `KittenEva.UpdateRenderData` in a prefix and postfix and records the kitten only when the viewport is `Program.MainViewport`; the processor reads that flag in `UpdateSkeleton`:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L177-L191
    private static void BeforeUpdateRenderData(KittenEva __instance, IViewport viewport)
    {
        try
        {
            _drawingMain = Body is { } b && ReferenceEquals(__instance, b.Kitten) && ReferenceEquals(viewport, Program.MainViewport)
                ? __instance
                : null;
        }
        catch
        {
            _drawingMain = null;
        }
    }

    private static void AfterUpdateRenderData() => _drawingMain = null;
```

2. **To edit the pose, use an `IAnimProcessor`** and pick the method by cost: `UpdateLocalPose` for the bone-local pose once per frame, `UpdateSkeleton` for world transforms once per view ([pose topic](../../animation/pose-kitten-bones-with-an-anim-processor/overview.md)). Mutate `AnimProcessors` from the UI pass or the after-on-frame hook, not from inside a callback.

3. **To keep a clip playing, patch the consumer, not the producer.** The game chooses the clip in step 3 of the table above and samples it in step 5, so a prefix on `AnimatedRenderable.UpdateAnimation` runs after the game's choice and before sampling. The forced-clip feature relies on the facts above: it multiplies `dt` knowing `dt` was zeroed after the first call and the pose advances on the first call per frame, and secondary viewports record before the main one ([forced clip topic](../../animation/play-a-specific-kitten-animation-clip/overview.md)).

4. **To change what is drawn, patch the single call.** The visor toggle transpiles the one `VisorMesh.Draw` call site in `KittenRenderable.UpdateRenderData` (a gate that forwards or skips), which also covers seated crew because they go through the same method ([appearance topic](../../appearance/tint-and-restyle-kitten-appearance/overview.md)).

5. **To draw a kitten the game does not draw, postfix the seated-crew entry point.** KSACars postfixes `Vehicle.UpdateSeatedCrewRenderData` because it runs after `ClearBuckets` for the viewport and before command recording, the only point where a submitted skinned kitten lands in the current buckets; a StarMap GUI hook runs before the buckets are cleared and a frame hook after the frame is submitted:

```csharp
// src/KSACars/Ksa/SeatedCrewHook.cs L69-L83
    // Inside the engine's render pass: nothing here may throw.
    private static void AfterSeatedCrew(IViewport viewport, int inFrameIndex)
    {
        try
        {
            if (viewport.Is(ViewportType.CharacterPortrait)) return;
            Buggies.DrawCrewOfUnflown(viewport, inFrameIndex);
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("drawing a buggy's crew failed", e);
        }
    }
```

6. **To change a shadow, edit the pool after `Draw`.** KSAStructures notes the pool count in a `Draw` prefix and removes what the call added in a postfix, then appends its own entries ([hide-a-kitten-body-from-one-viewport](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)).

7. **Wrap every hook that runs in the render in try/catch.** `App.Run` calls `OnFrame` with no try/catch around the render, so one exception ends the frame; every snippet above swallows or logs once. See [Contain exceptions in StarMap hooks](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md).

## Complete example

- [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L46-L118) - viewport marker, shadow swap and installation in one class.
- [src/KSACars/Ksa/SeatedCrewHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/SeatedCrewHook.cs#L20-L87) - the seated-crew postfix with its signature pin.
- Feature pages: [First-person body hiding and shadow](../../../../mods/ksastructures/features/first-person-body-hiding-and-shadow.md), [Kitten visor toggle](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md), [Seated crew of unflown craft](../../../../mods/ksacars/features/seated-crew-of-unflown-craft.md), [Forced clip playback](../../../../mods/unscience/features/kitten-animations-forced-clip-playback.md).

## Variations and gotchas

- **A kitten outside `VehiclesInFrame` is not animated at all.** A forced clip on an off-screen kitten simply does not advance, and a processor on it is never called.
- **Per-view skinning is load-bearing.** If a future build caches kitten skinning across viewports (as parachutes do), a fold written for one view reaches all of them.
- **`dt` is simulated time.** `dt2 = dt * Universe.GetSimulationSpeed() * Universe.GetAchivedSpeedFraction() * _groundAnimPlaybackRate`, so a paused game freezes processors that integrate `dt`, and warp speeds them up.
- **The reactive expression weight is overwritten every frame** from acceleration (step 4 of the table), so a value written to it never shows; add your own processor instead ([expression topic](../../animation/make-a-kitten-pull-a-facial-expression/overview.md)).
- **Portrait viewports** (`ViewportType.CharacterPortrait`) run the same loop; skip them when you submit extra kittens.
- **Build notes.** Line numbers cited are from build 2026.10.10.5554; the features were first built against 2026.10.7.5541 and the kitten, animation and SuperMesh files used here did not change between them.

## See also

- [Reach a kitten's avatar, skeleton, bones and animation catalog](overview.md)
- [Approach 1](reach-a-kittens-avatar-and-skeleton-1.md), [approach 2](reach-a-kittens-avatar-and-skeleton-2.md), [approach 3](reach-a-kittens-avatar-and-skeleton-3.md)
- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](../../animation/animate-and-draw-seated-kitten-crew/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
