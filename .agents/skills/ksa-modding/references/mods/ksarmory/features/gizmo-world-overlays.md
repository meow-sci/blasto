---
type: Mod Feature
title: World-anchored overlays through KSA's gizmo renderer
description: Radar cones, track markers, round trails, shell streaks and placement markers drawn as GizmosRenderer spheres and lines from the StarMap GUI postfix, anchored to the engine's own render position for the platform
mod: ksarmory
category: rendering
techniques: [starmap-lifecycle, gizmo-draw, ego-anchor]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:scene-opaque]
game_types: [KSA.GizmosRenderer, KSA.Program, KSA.Camera, KSA.Vehicle]
tags: [gizmos, overlay, debug-draw, ego-frame, draw-anchor, starmap, frame-order]
---

KSArmory's in-world diagnostics (the radar search cone, track spheres, threat lines, round trails
and seeker lines, loaded-tube markers, turret facing and bearing reference) and a few operator
aids (gun-shell streaks when no tracer pass is drawing them, the burst tool's lethal-radius sphere,
the craft mover's draped ring, the designator line, the ICBM arc) are all drawn with the engine's
debug primitive renderer, `KSA.GizmosRenderer`. It needs no asset, mesh or shader of the mod's
own: two calls, `DrawSphere` and `DrawLine`, in the camera-relative Ego frame.

The part worth copying is not the primitive but the placement. Every overlay is drawn relative to
an anchor the engine itself reports for the platform (`Camera.GetPositionEgo`), plus ecliptic
offsets captured at the same instant as the geometry, so lines land on the craft rather than
hundreds of metres beside it.[^ksarmory-ksaworld][^ksarmory-drawanchor]

## How it works

| Piece | What it does |
|---|---|
| `Visuals.Draw(system, config)` | Per weapon system, behind `Config.DrawOverlays`: `BeginDraw`, then search volume, tracks, rounds, tube markers, turret facing and bearing reference, each behind its own switch[^ksarmory-visuals] |
| `Visuals.DrawShellStream(system)` | Always while in flight: one 14 m (or shorter) line per flying gun shell along `VelocityLocal`, skipped when `ShellTracers.Painting` or when the shell is drawn as a part-model body |
| `Visuals.DrawDecoys(decoys)` | Flares and chaff as spheres sized by signature, offset from the dropping craft's engine position |
| `KsaWorld.BeginDraw(anchor, anchorEcl)` | Builds a `DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl)` from `Program.GetRenderCamera()`; returns false (draw nothing) if the craft is dead or the anchor is not finite |
| `DrawAnchor.ToEgo(posEcl)` | `Ego + (posEcl - Ecl)`: carries geometry measured at the step's epoch onto this frame's render position |
| `KsaWorld.DrawLineEcl` / `DrawSphereEcl` | Convert through the anchor, then call `Program.GizmosRenderer` |
| `KsaWorld.DrawLineEgo` / `DrawSphereEgo` | Pass an Ego position straight to the renderer (rounds use `AnchorEgo + round.OffsetFromPlatform`) |
| `KsaWorld.DrawTorusEcl` | A "solid" ring: 16 to 160 spheres spaced closer than their radius, each snapped to the terrain under it |

Why the anchor and not `camera.EclToEgo(vehicle.GetPositionEcl())`:

- `Camera.GetPositionEgo(IPosition)` returns `-PositionCce` for the followed object, the Bepu
  physics position (`KinematicStates.PositionPhys` through the bubble origin) for a vehicle in the
  same physics bubble as the followed one, and `GetPositionEcl() - PositionEcl` otherwise.[^ksa-camera]
  That is where the engine draws the craft, which can differ from the analytic orbit position
  (a landed craft).
- Ecliptic positions near Earth move about 500 m per frame at 60 fps, so the anchor's `Ecl` half
  must be the platform sample taken in the same simulation step as the geometry, never re-read at
  draw time. The mod pins this invariant in `DrawAnchor` and tests it headlessly.[^ksarmory-drawanchor]
- Rounds are stored as offsets from their platform, so they draw straight off `AnchorEgo` with
  no absolute arithmetic at all.

What the engine does with a submission (`GizmosRenderer`, build 5554):[^ksa-gizmosrenderer]

| Call | Effect |
|---|---|
| `DrawSphere(double3 positionEgo, float radiusMeters, float4 color)` | Narrows the position to `float3` and appends a `GenericGizmo` to a CPU array of 655,360; silently dropped when full |
| `DrawLine(double3 startEgo, double3 endEgo, float4 color)` | Appends two `LineGizmoVertex` (position plus packed colour) to a second 655,360-entry array |
| `ResetInstances()` | Zeroes both counts; called once per frame by `Program.OnFrame` |
| `Render(cmd, viewport, frameIndex)` | `RenderSpheres` then `RenderLines`: copies the CPU arrays into this frame-in-flight's slice of host-visible buffers and draws instanced spheres (triangle list, back-face culled) and a line list, both reverse-Z depth-tested with depth write and alpha blending |
| `DrawCircle` / `DrawArc` / `DrawWireBox` / `DrawCylinderSides` / `DrawWirePrimitive` | Helpers built from `DrawLine`; KSArmory does not use them and rolls its own rim loops |

## Lifecycle and timing

The draw calls run in `[StarMapAfterGui]`, which StarMap implements as a Harmony postfix on
`Program.OnDrawUiViewports`.[^starmap-programpatcher] In `Program.OnFrame` (build 5554):[^ksa-program]

| Line | Call | Relevance |
|---|---|---|
| L2260 | `GizmosRenderer.ResetInstances()` | Everything submitted earlier in the frame, or in the previous frame after the render, is discarded |
| L2263 | `OnFrameViewports` | This frame's cameras and matrices are computed |
| L2265-L2269 | `if (DrawUI) { OnDrawUiFrame; OnDrawUiViewports }` | `OnAfterGui` runs here: `StepOnce` first, then the panel, then `Visuals.*`[^ksarmory-mod] |
| L2324 | `Render` then `RenderGame` | Command recording |
| L4842 | `GizmosRenderer.Render(commandBuffer2, RenderedViewport, resourceFrameIndex)` | Inside `_offscreenTarget.BeginRendering` (L4792) ... `EndRendering` (L4848), right after `SuperMeshRenderSystem.RenderMainPass` (L4839) |

So the overlays live in phase `render:scene-opaque`, not in the later `GizmoPass.Run` (L4931,
`render:gizmos`), which is a different renderer. Consequences:

- They are depth-tested against terrain and craft and write depth themselves.
- Everything recorded after L4848 is applied on top of them: atmosphere and weather clouds
  (L4875), translucency (L4901), the mod's own compute pass before bloom (L4915), bloom and the
  tonemap composite. A blast shake moves them with the picture and a whiteout veils them.
- `GetRenderCamera()` is `RenderedViewport.GetCamera()` (L641-L644); outside `RenderGame`,
  `_renderedViewport` is the main viewport (set at L4684 and left there), so during the GUI hook
  the anchor is computed with the main camera that `RenderGame` will use.

Why this hook. The mod's own notes put it plainly: a postfix on `OnFrame` lands after the render,
so its submissions are wiped by the next frame's reset before they are ever drawn; the GUI
postfix sits between the reset and the render.[^ksarmory-modding-notes] The decomp agrees (L2260
before L2268 before L2324). Stepping the simulation at the top of the same hook means the anchor,
the round offsets and the drawn craft all share one epoch.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Visuals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Visuals.cs) | All weapon-system overlays and the shell stream |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3410-L3479) | `BeginDraw`, `AnchorEgo`, `TryEclToEgo` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3605-L3620) | `DrawSphereEgo`, `DrawLineEgo`, `DrawSphereEcl` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5896-L5945) | `DrawTorusEcl`, `DrawLineEcl` |
| [src/KSArmory/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DrawAnchor.cs) | The two-epoch anchor, free of KSA types for headless tests |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L500-L568) | `OnAfterGui`: step, panel, then the overlay calls |

## Snippets

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
        // ...
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

```csharp
// src/KSArmory/Ksa/Visuals.cs L104-L134
public static void DrawShellStream(WeaponSystem system)
{
    if (ShellTracers.Painting) return;
    if (system.Rounds.Count == 0 || system.Platform is not { } platform) return;
    if (!KsaWorld.BeginDraw(platform, system.PlatformEcl)) return;

    foreach (IProjectile round in system.Rounds)
    {
        // ...
        if (!RoundLabel.IsGunRound(round.Tube) || round.State != RoundState.Flying
            || system.ShellDrawnAsBody(round))
        {
            continue;
        }

        // Local, never VelocityEcl: the latter carries 29.8 km/s of ecliptic motion and would
        // lay every streak along the same direction whatever the gun did.
        double3 along = Vec.Unit(round.VelocityLocal);
        if (!Vec.IsFinite(along)) continue;
        // ...
        double streak = Math.Min(ShellStreakMetres, Vec.Len(round.TravelSinceLaunch));
        if (streak < 0.1) continue;

        double3 nose = KsaWorld.AnchorEgo + round.OffsetFromPlatform;
        KsaWorld.DrawLineEgo(nose - (along * streak), nose, ShellColour);
    }
}
```

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.GizmosRenderer` (public static field) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L217) | The renderer instance every helper calls |
| `GizmosRenderer.DrawSphere(double3, float, float4)` | [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L181-L195) | Track, round, decoy, tube and torus-bead spheres |
| `GizmosRenderer.DrawLine(double3, double3, float4)` | [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L217-L235) | Cone ribs, threat and seeker lines, trails, shell streaks, arcs |
| `GizmosRenderer.ResetInstances` / `Render` | [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L338-L477) | Engine-side frame bracket the hook must fall inside |
| `Program.GetRenderCamera` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L641-L644) | The camera the anchor is computed with |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250) | Where the engine draws the anchor craft |
| `Program.DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L526) | Gates the StarMap GUI hooks, and so these overlays |

## Pitfalls and version notes

- **Submit between L2260 and L4842.** From `[StarMapAfterOnFrame]` nothing ever appears. A Harmony
  prefix on anything in that window works too: `GizmosRenderer.Render` itself is not gated on
  `DrawUI`, so a submission from the `OnFrameCelestials` prefix would draw with the HUD hidden.
  KSArmory deliberately draws only from the GUI hook, so its overlays vanish with F2 and from
  `ScreenshotCapture` frames, like the rest of the HUD.
- **Main view only.** `RenderViewport` (secondary camera windows) never calls
  `GizmosRenderer.Render`; it runs `GizmoPass.Run` (L4604), a different renderer.[^ksa-program]
  None of these overlays appear in a camera window.
- **Ego positions are narrowed to `float`.** Fine near the camera; a line ending megametres away
  loses metre-level precision. Draw short shapes near the camera (the search cone is drawn at a
  capped display range, not the radar's range).
- **Silent capacity.** 655,360 spheres and 327,680 lines per frame, extras dropped without a log;
  `Render` copies the whole CPU array every frame regardless of count.[^ksa-gizmosrenderer]
- **Spheres and lines only.** `GizmoType` is `Sphere`, `Line`, `Num`; the mod's notes record the
  missing filled primitive and the sphere-ring torus workaround.[^ksarmory-blocked]
- **Never difference Ecl samples from different instants.** The overlay drifts by the frame's
  ecliptic motion (about 500 m near Earth); see the anchor rules in the epochs feature.
- **Shells have three renderers.** Part-model instances when the loose-body hook is installed
  (`ShellDrawnAsBody`), the compute tracer pass when it is painting, and this gizmo streak only
  when neither applies.

## See also

- [Frames, epochs and the draw anchor](./ecliptic-frames-and-epochs.md)
- [One simulation step per frame from whichever hook runs first](./dual-hook-simulation-step.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Round bodies drawn past the engine's cull](./round-body-draw-hooks.md)
- [Compute shell tracers](./compute-shell-tracers.md)
- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [World-anchored screen markers](./world-anchored-screen-markers.md)
- [Radar search and lock](./radar-search-and-lock.md)
- [Anchor to the engine's own render position and add travel since launch](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-2.md) - how-to article demonstrating this feature
- [Submit spheres and lines from the StarMap GUI postfix between reset and render](../../../howto/rendering/overlays/draw-world-space-lines-and-gizmos/draw-world-space-lines-and-gizmos-1.md) - how-to article demonstrating this feature

[^ksarmory-visuals]: src/KSArmory/Ksa/Visuals.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-drawanchor]: src/KSArmory/Sim/DrawAnchor.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-gizmosrenderer]: KSA.GizmosRenderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
