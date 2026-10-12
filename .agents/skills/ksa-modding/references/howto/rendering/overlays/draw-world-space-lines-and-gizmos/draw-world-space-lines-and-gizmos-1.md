---
type: How-To
title: Submit spheres and lines from the StarMap GUI postfix between reset and render
description: Draw radar cones, track markers, trails and placement markers with GizmosRenderer.DrawSphere and DrawLine from StarMapAfterGui, anchored to the engine's render position of the platform
category: rendering
group: overlays
topic: draw-world-space-lines-and-gizmos
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/gizmo-world-overlays]
techniques: [gizmo-draw]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:scene-opaque]
game_types: [KSA.GizmosRenderer, KSA.Program, KSA.Camera]
tags: [gizmos, debug-draw, spheres, lines, depth-tested, overlay]
---

Outcome: depth-tested, scene-scaled world overlays (radar cones, track spheres, threat lines, round trails, loaded-tube markers, placement markers) with no Vulkan code, no asset, mesh or shader of the mod's own: two calls, `DrawSphere` and `DrawLine`, in the camera-relative Ego frame. The
part worth copying is the placement: every overlay is drawn relative to an anchor the engine itself reports for the platform, plus ecliptic offsets captured at the same instant as the geometry. Demonstrated by KSArmory ([World-anchored overlays through KSA's gizmo renderer](../../../../mods/ksarmory/features/gizmo-world-overlays.md)). Prerequisites: StarMap.

## Background: how the game does it

`GizmosRenderer` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs) keeps CPU arrays of sphere and line instances: `DrawSphere(double3 positionEgo, float radiusMeters, float4 color)` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L181) narrows the position to `float3` and appends a `GenericGizmo`; `DrawLine(double3 startEgo, double3 endEgo, float4 color)` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L217) appends two line vertices;
extras beyond the arrays' capacity are silently dropped. `ResetInstances()` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L471) zeroes both counts and is called once per frame by `Program.OnFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260). `Render(cmd, viewport, frameIndex)` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L338) copies the arrays into the frame's host-visible buffers and draws instanced spheres
and a line list, reverse-Z depth-tested with depth write and alpha blending. In `RenderGame` it is called inside the opaque offscreen scope, right after `SuperMeshRenderSystem.RenderMainPass`:

```csharp
// current/decomp/KSA/Program.cs L4837-L4848
SuperMeshRenderSystem.SetCamMatrices(_cameraData[shaderSlot].View, _cameraData[shaderSlot].Projection);
Profiler.MainThread.Begin(Profiler.Tag.RecordSuperMeshMainPass);
SuperMeshRenderSystem.RenderMainPass(RenderedViewport, commandBuffer2);
Profiler.MainThread.End();
_volumetricExhaustRenderer.RenderDebugWireFrame(commandBuffer2, RenderedViewport, resourceFrameIndex);
GizmosRenderer.Render(commandBuffer2, RenderedViewport, resourceFrameIndex);
if (IsRaytracingActive && MainViewport.Mode == CameraMode.IVA)
{
	RaytracingRenderer?.RasterizerOpaque.RenderRasterization(commandBuffer2, RenderedViewport, resourceFrameIndex);
}
StaticObjectRenderer.WriteCommandsBlended(commandBuffer2, RenderedViewport, resourceFrameIndex);
_offscreenTarget.EndRendering(commandBuffer2, ImageBarrierInfo.Presets.ColorAttachmentWrite, ImageBarrierInfo.Presets.DepthStencilAttachmentWrite);
```

That is `render:scene-opaque`, not the later `GizmoPass.Run` (step 19, `render:gizmos`), which is a different renderer. Consequences: depth-tested against terrain and craft; everything recorded after (atmosphere, translucency, compute before bloom, bloom, tonemap) is applied on top of them.

## Lifecycle and timing

| Line | Call | Relevance |
|---|---|---|
| L2260 | `GizmosRenderer.ResetInstances()` | Everything submitted earlier is discarded |
| L2263 | `OnFrameViewports` | This frame's cameras are computed |
| L2265-L2269 | `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }` | `[StarMapAfterGui]` runs here: step the simulation, then the panel, then overlays |
| L2324 | `Render` then `RenderGame` | Command recording; `GizmosRenderer.Render` at L4842 |

Submit between `ResetInstances` and `Render`. From `[StarMapAfterOnFrame]` nothing ever appears (it lands after the render and is wiped by the next reset). A prefix on `OnFrameCelestials` also works and is not gated on `DrawUI`; KSArmory deliberately draws only from the GUI hook, so its overlays vanish with F2 and from screenshots like the rest of the HUD.
See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the anchor** from the render camera: `DrawAnchor(camera.GetPositionEgo(platform), platformEcl)`; return false (draw nothing) if the craft is dead or the anchor is not finite.

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L3438-L3470
   public static bool BeginDraw(Vehicle anchor, double3 anchorEcl)
   {
       _anchored = false;
       if (!IsAlive(anchor)) return false;

       try
       {
           // The camera the frame will actually be rendered with, not the main viewport's.
           Camera camera = Program.GetRenderCamera() ?? Program.GetMainCamera();
           if (camera is null) return false;

           // See DrawAnchor for why these are sampled at different instants, and why
           // collapsing them into one puts the whole overlay beside the craft.
           //
           // GetPositionEgo, not EclToEgo. Its branching on what the camera follows is the
           // engine answering correctly per case — exact for the followed craft, physics-based
           // for others in its bubble — and it is the same call KSA renders vehicles with.
           // EclToEgo instead measures against camera.PositionEcl, which only agrees with the
           // rendered scene when the followed craft's analytic and physics positions coincide.
           // That holds for a landed launcher and fails once the camera follows something in
           // flight, which is when anything drawn to a vehicle stops lining up.
           _anchor = new DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl);

           if (!_anchor.IsValid) return false;

           _anchored = true;
           return true;
       }
       catch
       {
           return false;
       }
   }
   ```

2. **Convert and submit.** `DrawLineEcl`/`DrawSphereEcl` convert through the anchor (`anchor.Ego + (posEcl - anchor.Ecl)`); rounds stored as offsets from the platform draw straight from `AnchorEgo + OffsetFromPlatform`.

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L3605-L3620
   public static void DrawSphereEgo(double3 positionEgo, float radiusMetres, float4 colour)
   {
       Program.GizmosRenderer?.DrawSphere(positionEgo, radiusMetres, colour);
   }

   public static void DrawLineEgo(double3 startEgo, double3 endEgo, float4 colour)
   {
       Program.GizmosRenderer?.DrawLine(startEgo, endEgo, colour);
   }

   public static void DrawSphereEcl(double3 positionEcl, float radiusMetres, float4 colour)
   {
       if (Program.GizmosRenderer is null) return;
       if (!TryEclToEgo(positionEcl, out double3 ego)) return;
       Program.GizmosRenderer.DrawSphere(ego, radiusMetres, colour);
   }
   ```

3. **Compose shapes from spheres and lines.** The renderer has no filled primitive; a "solid" ring is 16 to 160 overlapping spheres each snapped to the terrain beneath it. A shell streak is one line along the local (not ecliptic) velocity, never longer than the round has flown:

   ```csharp
   // src/KSArmory/Ksa/Visuals.cs L104-L134
   public static void DrawShellStream(WeaponSystem system)
   {
       if (ShellTracers.Painting) return;
       if (system.Rounds.Count == 0 || system.Platform is not { } platform) return;
       if (!KsaWorld.BeginDraw(platform, system.PlatformEcl)) return;

       foreach (IProjectile round in system.Rounds)
       {
           // Negative tube numbers mark the cannon; the magazine owns zero and up, and a missile
           // has a real subpart body of its own. So may a shell, and then the body is the round.
           if (!RoundLabel.IsGunRound(round.Tube) || round.State != RoundState.Flying
               || system.ShellDrawnAsBody(round))
           {
               continue;
           }

           // Local, never VelocityEcl: the latter carries 29.8 km/s of ecliptic motion and would
           // lay every streak along the same direction whatever the gun did.
           double3 along = Vec.Unit(round.VelocityLocal);
           if (!Vec.IsFinite(along)) continue;

           // Never longer than the round has actually flown. The streak is drawn backwards from
           // the shell, so a full-length one on a round that has just left the barrel starts 14 m
           // behind the muzzle, which is inside the mount and out the other side of it.
           double streak = Math.Min(ShellStreakMetres, Vec.Len(round.TravelSinceLaunch));
           if (streak < 0.1) continue;

           double3 nose = KsaWorld.AnchorEgo + round.OffsetFromPlatform;
           KsaWorld.DrawLineEgo(nose - (along * streak), nose, ShellColour);
       }
   }
   ```

4. **Gate and degrade.** Each overlay sits behind its own config switch; a streak is skipped when the compute tracer pass is painting.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Visuals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Visuals.cs) | Per-system overlay drawing |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3438-L3470) | Anchor and helpers |
| [src/KSArmory/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DrawAnchor.cs) | Game-free anchor math |

## Variations and gotchas

- **Main view only.** `RenderViewport` (secondary camera windows) never calls `GizmosRenderer.Render`; it runs `GizmoPass`, so none of these overlays appear in a camera window.
- **Ego positions are narrowed to `float`:** draw short shapes near the camera; the search cone is drawn at a capped display range.
- **Silent capacity** (655,360 spheres and 327,680 lines per frame at this build); `Render` copies the whole CPU array every frame regardless of count.
- **Spheres and lines only.** `GizmoType` is `Sphere`, `Line`, `Num`.
- **Never difference Ecl samples from different instants:** the overlay drifts by the frame's ecliptic motion.

## See also

- [Overview](overview.md)
- [Approach 2: gizmos next to a moving craft with a frame-epoch anchor](draw-world-space-lines-and-gizmos-2.md)
- [World-anchored overlays through KSA's gizmo renderer](../../../../mods/ksarmory/features/gizmo-world-overlays.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../choose-a-render-frame-and-anchor-effects/overview.md), [Draw markers, brackets and measurement annotations on world objects](../draw-markers-and-annotations-on-world-objects/overview.md)
