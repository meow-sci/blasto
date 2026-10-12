---
type: How-To
title: Body-fixed mesh with Ccf-to-Cce rotation and body position at draw time
description: Store course pieces and a ball in the planet's body-fixed frame and turn them into per-viewport ego transforms inside a draw hook after ClearBuckets
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/mod-mesh-draw-hook]
techniques: [runtime-mesh-draw, reference-frames]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets]
lifecycle: [startup:all-mods-loaded, frame:render, render:scene-opaque, render:shadows, render:translucency]
game_types: [KSA.StaticMeshRenderable, KSA.SuperMeshRenderSystem, KSA.Celestial, KSA.IViewport]
tags: [mesh, staticmeshrenderable, ccf, body-fixed, clearbuckets, anchor]
---

Outcome: lit, shadowed, depth-tested meshes of the mod's own (a golf ball, course pieces) that stay put on a rotating planet. The mod keeps their state in the planet's body-fixed frame and, at draw time, composes the body's `Ccf2Cce` rotation with the translation to the
viewport's camera. Because a `StaticMeshRenderable.Draw` from a StarMap hook is wiped by the per-view bucket clear, the draw runs in a postfix on `SuperMeshRenderSystem.ClearBuckets`. Demonstrated by KSAGolf ([Drawing a mod's static mesh into every viewport](../../../../mods/ksagolf/features/mod-mesh-draw-hook.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

`StaticMeshRenderable.Draw(ViewHandle)` [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L56) records into the view's mesh buckets. `SuperMeshRenderSystem.ClearBuckets(IViewport)` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546) empties those buckets (and the shared shadow pool) for each viewport immediately before the game fills them for real;
`ViewForViewport(IViewport)` [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L237) gives the handle. `RenderGame` calls it inside `RenderViewport` for every visible secondary viewport and then for the main view; `RenderEditor` calls it too. `Celestial.GetCcf2Cce()` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532) is the body-fixed to
body-centred-ecliptic rotation. `StaticMeshRenderable.Transform` is in the ego frame of the view being drawn, in metres.

## Lifecycle and timing

| Step | Where |
|---|---|
| Install the Harmony postfix | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), after the props XML is loaded |
| Postfix runs, delegate draws | `frame:render`, inside `Program.Render` -> `RenderGame` (or `RenderEditor`), once per rendered viewport, secondary views first, before `PrePassRenderer.Render` |
| Submitted instances are consumed | prepass and main opaque pass of that view (`render:scene-opaque`), the main view's cascaded shadow pass (`render:shadows`), the translucent pass for a see-through piece (`render:translucency`) |

Earlier is too early: `OnPreRender` and `UpdateRenderingResources` happen before `RenderGame`, and a draw from either (or any StarMap hook) is wiped by the clear. State is settled by the postfix: the step already moved the ball in the GUI pass and every camera was posed in
`OnFrameViewports`. See [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. Patch `ClearBuckets(IViewport)` with a postfix that invokes one delegate per rendered viewport, inside a `try`; on the first exception null the delegate and log once, so a broken draw costs the mesh for the session and never the frame.

   ```csharp
   // src/KSAGolf/Ksa/ViewDrawHook.cs L37-L48
   MethodInfo? target = typeof(SuperMeshRenderSystem).GetMethod(nameof(SuperMeshRenderSystem.ClearBuckets),
                                                                BindingFlags.Public | BindingFlags.Instance, [typeof(IViewport)]);
   if (target is null)
   {
       Log.Warn("the ball cannot be drawn: KSA has no SuperMeshRenderSystem.ClearBuckets(IViewport)");
       return;
   }

   MethodInfo postfix = typeof(ViewDrawHook).GetMethod(nameof(AfterClearBuckets), BindingFlags.NonPublic | BindingFlags.Static)!;
   _harmony = new Harmony(HarmonyId);
   _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
   Installed = true;
   ```

   ```csharp
   // src/KSAGolf/Ksa/ViewDrawHook.cs L74-L88
   private static void AfterClearBuckets(IViewport viewport)
   {
       try
       {
           _draw?.Invoke(viewport);
       }
       catch (Exception e)
       {
           _draw = null;

           if (_complained) return;
           _complained = true;
           Log.Error("drawing into the world failed; the ball is no longer drawn this session", e);
       }
   }
   ```

2. Store the object in the body-fixed frame of the planet under it. At draw time compute the ego translation against **the viewport passed in**: `positionEcl - viewport.GetCamera().PositionEcl`, skip beyond a draw distance, compose the model matrix (scale, spin composed with `body.GetCcf2Cce()`, translation), and submit into that viewport's view:

   ```csharp
   // src/KSAGolf/Ksa/Golf.cs L441-L455
   /// <summary>Draws the ball into one viewport, as its viewport is rendered, and in first person the club.</summary>
   public void Draw(IViewport viewport)
   {
       DrawViewClub(viewport);
       if (_ball is null || _ballBody is null || _ballMesh is null) return;

       double3 ego = BallEcl() - viewport.GetCamera().PositionEcl;
       if (!Vec.IsFinite(ego) || Vec.Len(ego) > 5000.0) return;

       doubleQuat turned = doubleQuat.Concatenate(_ball.Spin, _ballBody.GetCcf2Cce());
       _ballMesh.Transform = float4x4.CreateScale(0.01f)
                             * float4x4.CreateFromQuaternion(floatQuat.Pack(turned))
                             * float4x4.CreateTranslation(float3.Pack(ego));
       _ballMesh.Draw(Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport));
   }
   ```

3. Decide per viewport what draws: the ball in every viewport, a first-person club only in `Program.MainViewport`, a see-through "ghost" built on the glass technique so it lands in the translucent pass. Only draws made into the main view cast shadows (the shadow pool is cleared by every view's clear and the cascaded pass is recorded for the main view after its own clear).

4. Build the renderable when the object is created; the mod builds some lazily on first draw, relying on constructing a `StaticMeshRenderable` (which registers mesh buckets) in the middle of recording.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/ViewDrawHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/ViewDrawHook.cs) | The postfix and install/remove |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L441-L455) | Ball draw with the Ccf rotation and ego translation |

## Variations and gotchas

- **Silent failure.** A draw made at the wrong time counts as made: nothing throws or logs; the mesh is simply missing.
- **Every viewport.** The postfix runs for secondary viewports and the vehicle editor too; the delegate decides per view.
- **Ego is per camera.** Compute the translation from the viewport passed in; a position taken against the main camera and drawn into a secondary view is off by the distance between the two cameras.
- **Pinned by name only.** `nameof` emits no member reference, so API-surface tooling will not flag a changed signature; a changed parameter list makes `GetMethod` return null at runtime and the mod logs one line.
- **Mesh units.** The ball is authored in centimetres for use as a kitten attachment, hence the 0.01 scale.

## See also

- [Overview](overview.md)
- [Approach 1: two-instant draw anchor](choose-a-render-frame-and-anchor-effects-1.md)
- [Drawing a mod's static mesh into every viewport](../../../../mods/ksagolf/features/mod-mesh-draw-hook.md)
- [Draw your own meshes through the engine's mesh renderers](../../meshes/draw-mod-meshes-with-the-engine-renderers/overview.md)
