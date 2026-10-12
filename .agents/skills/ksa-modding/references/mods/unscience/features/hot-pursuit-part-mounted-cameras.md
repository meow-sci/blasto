---
type: Mod Feature
title: Live part-mounted cameras in leased stock secondary viewports
description: Hot Pursuit claims stock secondary viewports through ViewportRegistry and replaces FixedController.OnFrame only for its own viewports with a part-relative pose and nearby-celestial sync, so the stock renderer draws a camera riding a sub-part
mod: unscience
submod: hot-pursuit
category: camera
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.FixedController.OnFrame(IViewport, double)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, lease sync and follow)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent)
  - unscience ISaveParticipantSource (scene save record hot-pursuit)
lifecycle: [frame:viewports, frame:draw-ui, frame:render, event:save, event:load]
game_types:
  - KSA.FixedController
  - KSA.ViewportRegistry
  - KSA.IViewportOwner
  - KSA.IGameViewport
  - KSA.GameViewport
  - KSA.Camera
  - KSA.Program
  - KSA.Vehicle
  - KSA.Part
  - KSA.Celestial
  - KSA.StaticCelestialDistanceRendering
  - KSA.StaticObjectRenderer
tags: [camera, viewports, secondary-viewport, harmony, rendering, floating-origin]
---

Hot Pursuit lets the player click any visible vessel part and get a live camera bolted to it: a
nose-cone view down the stack, a camera looking back at a lander leg, a chase view from a booster.
Each camera appears in KSA's own resizable camera window and can be nudged in the part's local frame,
rotated, re-aimed and given its own field of view.[^hp-readme]

Mechanism in one line: every camera owns an `IViewportOwner` token and leases a stock secondary
viewport with `ViewportRegistry.TryClaimSecondaryViewport`; the viewport is put in `CameraMode.Fixed`,
and a prefix on `FixedController.OnFrame(IViewport, double)` skips the stock fixed-camera math only for
viewports Hot Pursuit owns, writing a pose recomposed from the part's current transform
instead.[^hp-patches][^hp-submod]

## How it works

The viewport pool. In 5554, `Program` builds every viewport once at boot: the main viewport, one part
thumbnail viewport, four `ViewportType.Secondary` viewports (500x500, with UI and input, no shadows,
ray tracing or audio flags) and two character-portrait viewports. It then calls
`ViewportRegistry.SealAllocation()`. `MAX_VIEWPORTS` is 8 and the boot set uses all eight, so no mod can
allocate another; the four secondaries are the whole budget, shared with the game's own Add Camera
action and any other mod.[^ksa-program][^ksa-viewportregistry]

Leasing and configuration (`HotPursuitSubmod`):

| Step | Code | Game behavior relied on |
|---|---|---|
| Claim | `TryClaimSecondaryViewport(entry.Owner, out viewport)` | Returns the same viewport for an owner that already holds one; otherwise leases the first free secondary and calls `ResetToDefaults()` (Orbit mode, cleared Fixed offsets, default name and flags) |
| Configure | `SetName`, `SetCameraMode(CameraMode.Fixed)`, `SetResizeAllowed(true)` + `RequestResize(w, h)` + `SetResizeAllowed(false)`, `BaseCamera.SetFieldOfView`, `BaseCamera.SetFollow(vehicle, tidalLocking: true, changeControl: false, alert: false)`, `SetVisible` | Because a claim resets the viewport, everything is re-applied after every claim |
| Track | `ViewportRegistry.TryGetOwned(entry.Owner, ...)` every `Update`, and again inside the prefix | Closing the stock window (`GameViewport.DrawImGui`) calls `ReleaseSecondaryViewport(this)`; the entry then shows "Reopen viewport" instead of keeping a stale reference |
| Release | `ReleaseSecondaryViewport(entry.Owner)` on remove, reset and dispose | Hides and resets the slot for other owners |

`HotPursuitCamera` holds a separate `CameraViewportOwner` (an empty class implementing
`IViewportOwner`) per entry, because the registry keys ownership by reference and allows one viewport
per owner.[^hp-camera]

The pose seam. `GameViewport.OnFrame` calls `GetActiveController().OnFrame(this, dt)` and then
`GetCamera().OnFrame(dt)`; in Fixed mode the controller is `FixedController`, whose stock body places
the camera at `following.GetPositionEcl() + CameraOffset` (or at a docking
connector).[^ksa-gameviewport][^ksa-fixedcontroller] The prefix calls `ApplyFixedPose(inViewport)`:

1. Find the entry whose `Viewport` is this exact viewport; otherwise return `true` (stock runs). Portrait
   viewports, docking cameras and other owners' Fixed viewports are untouched.
2. Re-check ownership with `TryGetOwned`; if the slot was released since the last `Update`, drop the
   reference and let the stock controller run.
3. Re-resolve the target by vehicle id and `Part.InstanceId` (see
   [mesh-ray placement](./hot-pursuit-mesh-ray-placement.md)) so a part destroyed this frame is never
   dereferenced, then apply the pose and return `false`. If the pose code throws after matching, the
   prefix still returns `false` to keep the last valid pose rather than run the stock math with a zero
   direction.

Pose math (`HotPursuitPose.TryApply`):[^hp-pose]

- Reference frame: the main viewport's camera. `vehicle.GetMatrixAsmb2Ego(mainCamera)` gives the
  vessel matrix in that camera's ego (camera-relative) space, and `part.MatrixAsmb2Ego(in vehicleMatrix)`
  composes the sub-part chain including per-part `Scale`, so robotics/articulated sub-parts and scaled
  parts are followed exactly.[^ksa-part][^ksa-vehicle]
- Position: `(MountPoint + Translation)` through the part matrix gives an ego point;
  `mainCamera.EgoToEcl` turns it into an absolute ECL position. Working in ego space and converting
  once avoids subtracting huge ECL coordinates (floating origin).
- Orientation: the stored surface normal goes through the inverse-transpose of the part matrix, the
  tangent through the part matrix, and the basis is re-orthogonalized. This keeps the camera aimed
  along the true surface normal on non-uniformly scaled parts. Euler pitch/yaw/roll from the UI are
  applied in that basis, then `Camera.LookAtRotation(forward, up)`.
- Write: `camera.PositionEcl`, then `camera.ClampCamera()` (the same 0.5 m terrain clamp
  `Camera.OnFrame` will apply, so the following step sees the final position), then
  `HotPursuitCelestialState.Synchronize(camera)`, `WorldRotation`, and `SetFieldOfView(entry.FieldOfView)`.

Nearby-celestial sync (`HotPursuitCelestialState`). `Program.OnFrameCelestials` fills
`NearbyCelestial`, the distance fields, terrain height and `CurrentAltitudeKm` only for
`GetCamera()`, which is the main viewport's camera at that point in the frame. The sync calls
`Program.FindNearbyCelestial(camera)` for the mounted camera, mirrors the 80,000 km surface-distance
cutoff, and fills the same public fields.[^hp-celestial][^ksa-program] The authors added it because
without it the nearby body was also drawn by the distant-sphere pass as a large dark-grey disk across
the feed.[^hp-readme]

## Lifecycle and timing

| Code | When | Notes |
|---|---|---|
| `FixedController.OnFrame` prefix (pose, clamp, celestial sync) | `frame:viewports`: `Program.OnFrameViewports` (L2263, def L2608) -> `viewport.OnFrame` (L2622) -> `GameViewport.OnFrame` (L118) | Runs immediately before `Camera.OnFrame` (L119) builds the view matrix and frustum, so the feed has no one-frame lag behind the vessel. Physics results for this frame are already applied; the part matrices are stable on the main thread |
| `HotPursuitSubmod.Update` (lease sync, follow, FOV, visibility) | `frame:draw-ui` (`StarMapBeforeGui` or the hidden-HUD replay) | Runs after the viewports in the same frame; anything it changes (visibility, a newly opened lease) is first used for rendering this frame, and for the pose the next frame |
| Stock camera window, `GameViewport.DrawImGui` | `frame:draw-ui`, `Program.OnDrawUiViewports` (L3172) | Closing it releases the lease |
| Secondary scene render, `Program.RenderViewport` | `frame:render`: `RenderGame` renders every visible non-main viewport before the main view (L4672-L4683) | Uses the camera matrices baked in `frame:viewports` |
| Save capture and restore | `event:save` / `event:load`, order 180 | Restore reclaims slots for cameras that were open |

Why `FixedController` and not `Update`: an ImGui-phase write would happen after `Camera.OnFrame` already
built that frame's matrices, so the feed would lag one frame and jitter against a moving vessel.

## What a secondary view renders (5554)

`Program.RenderViewport` (L4468-L4626) is a reduced pipeline compared with the main path in
`RenderGame`. It draws stars and the Milky Way, `StaticCelestialDistanceRendering`, static-celestial
spheres, generic meshes and splines, part models, static objects, planet terrain (when the viewport has
`RenderTerrain`), SuperMesh opaque and translucency passes, atmosphere-only, sun bloom, orbit lines,
gizmos, grid and the composite. These main-view passes are absent from it:[^ksa-program]

| Missing in secondary feeds | Main-path call (5554) |
|---|---|
| Depth prepass, ambient occlusion, light shadows, sun and cascaded shadows | `Program.cs` L4713, L4723, L4740, L4753 |
| IVA ray tracing pass | L4777 |
| Sun disc, planetary ring meshes, ground clutter | L4816, L4831, L4736 |
| Particles (opaque and translucent) | L4851, L4903 |
| Ocean and volumetric clouds (`_planetTransparenciesRenderer.Render`) | L4866, L4875 |
| Volumetric engine exhaust | L4891 |
| Part glass (`PartModelGlass.Shared.WriteCommandsColor`) | L4894 |
| Overall bloom | L4916 |

So engine plumes, smoke and particles, clouds, ocean, shadows and window glass do not appear in Hot
Pursuit feeds; the mod deliberately does not inject the main-camera passes.[^hp-readme]

## Key code

| Path | Role |
|---|---|
| [hot-pursuit.lib/HotPursuitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPatches.cs#L11-L47) | Exact-signature target, selective prefix |
| [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L119-L236) | Ownership check, claim, configure, sync, release |
| [hot-pursuit.lib/HotPursuitPose.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPose.cs#L13-L115) | Part-relative pose, normal transform, Euler basis |
| [hot-pursuit.lib/HotPursuitCelestialState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCelestialState.cs#L12-L38) | Per-camera nearby-celestial fields |
| [hot-pursuit.lib/HotPursuitCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCamera.cs) | Entry state and owner token |
| [hot-pursuit.lib/HotPursuitSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Ui.cs#L10-L82) | Per-camera controls, reopen, resize |
| [hot-pursuit.lib/HotPursuitSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Saves.cs#L21-L68) | Scene save: target, mount basis, settings, open state |

## Snippets

The selective prefix:

```csharp
// hot-pursuit.lib/HotPursuitPatches.cs L11-L47
    private static MethodBase? Target() =>
        AccessTools.Method(typeof(FixedController), nameof(FixedController.OnFrame),
            new[] { typeof(IViewport), typeof(double) });
// ...
    private static bool BeforeFixedFrame(IViewport inViewport)
    {
        try
        {
            // False skips only the stock controller update for a Hot Pursuit-owned viewport.
            // GameViewport still calls Camera.OnFrame immediately afterward to bake the view.
            return !(HotPursuitSubmod.Instance?.ApplyFixedPose(inViewport) ?? false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"hot-pursuit: fixed-camera pose error: {ex.Message}");
            // ApplyFixedPose can throw only after matching one of our entries. Keep the camera's
            // last valid pose instead of running stock FixedController with its zero direction.
            return false;
        }
    }
```

Pose composition in the main camera's ego frame, then one conversion to ECL:

```csharp
// hot-pursuit.lib/HotPursuitPose.cs L28-L75
        var referenceCamera = Program.MainViewport.GetCamera();
        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(referenceCamera);
        var partMatrix = part.MatrixAsmb2Ego(in vehicleMatrix);
        var mount = entry.MountPoint + entry.Translation;
        var positionEgo = double3.Transform(mount, partMatrix);
        var positionEcl = referenceCamera.EgoToEcl(positionEgo);
// ...
        if (!TryBuildBasis(entry, partMatrix, out var forwardEcl, out var upEcl,
                out var rightEcl))
            return false;
// ...
        ApplyEuler(ref forwardEcl, ref upEcl, ref rightEcl, entry.RotationDeg);
// ...
        var camera = ownedViewport.BaseCamera;
        camera.PositionEcl = positionEcl;
        // GameViewport calls Camera.OnFrame after this prefix, which terrain-clamps the camera.
        // Clamp once now as well so the celestial metrics below describe the final render pose;
        // Camera.OnFrame's second clamp is then idempotent.
        camera.ClampCamera();
        HotPursuitCelestialState.Synchronize(camera);
        camera.WorldRotation = Camera.LookAtRotation(forwardEcl, upEcl);
        camera.SetFieldOfView(entry.FieldOfView);
```

Mirroring the main camera's nearby-celestial state for a secondary camera:

```csharp
// hot-pursuit.lib/HotPursuitCelestialState.cs L12-L38
    internal static void Synchronize(Camera camera)
    {
        Celestial? nearby = Program.FindNearbyCelestial(camera);
        if (nearby == null)
        {
            camera.NearbyCelestial = null;
            return;
        }

        double distanceKm = camera.DistanceTo(nearby.GetPositionEcl()) * MetersToKilometers;
        double surfaceDistanceKm = distanceKm - nearby.MeanRadius * MetersToKilometers;
        if (surfaceDistanceKm > MaxSurfaceDistanceKm)
        {
            camera.NearbyCelestial = null;
            return;
        }

        camera.NearbyCelestial = nearby;
        camera.DistanceToNearbyCelestialKm = distanceKm;
        camera.DistanceToNearbyCelestialSurfaceMeanKm = surfaceDistanceKm;
        camera.NearbyCelestialTerrainHeight = nearby.GetTerrainHeight(camera) * MetersToKilometers;

        double3 positionCce = nearby.GetPositionCce(camera);
        double terrainRadius = nearby.MeanRadius +
            nearby.GetTerrainHeightFromDirCce(positionCce.Normalized());
        camera.CurrentAltitudeKm = (positionCce.Length() - terrainRadius) * MetersToKilometers;
    }
```

The lease primitive in the game:

```csharp
// current/decomp/KSA/ViewportRegistry.cs L185-L215
	public static bool TryClaimSecondaryViewport(IViewportOwner inOwner, [NotNullWhen(true)] out IGameViewport? outViewport)
	{
		ArgumentNullException.ThrowIfNull(inOwner, "inOwner");
		if (OwnedSecondaryViewports.TryGetValue(inOwner, out outViewport))
		{
			return true;
		}
		if (!TryLeaseSecondaryViewport(out outViewport))
		{
			return false;
		}
		OwnedSecondaryViewports.Add(inOwner, outViewport);
		return true;
	}

	private static bool TryLeaseSecondaryViewport([NotNullWhen(true)] out IGameViewport? outViewport)
	{
		IGameViewport[] gameViews = _gameViews;
		foreach (IGameViewport gameViewport in gameViews)
		{
			if (gameViewport.Is(ViewportType.Secondary) && !LeasedSecondaryViewports.Contains(gameViewport.Id))
			{
				((IGameViewportLifecycle)gameViewport).ResetToDefaults();
				LeasedSecondaryViewports.Add(gameViewport.Id);
				outViewport = gameViewport;
				return true;
			}
		}
		outViewport = null;
		return false;
	}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `FixedController.OnFrame(IViewport, double)` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L48) | Prefixed; skipped for owned viewports |
| `ViewportRegistry.MAX_VIEWPORTS`, `AvailableSecondaryCount`, `TryClaimSecondaryViewport`, `ReleaseSecondaryViewport`, `TryGetOwned` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L18-L258) | Lease API and pool size |
| `IViewportOwner` | [current/decomp/KSA/IViewportOwner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewportOwner.cs) | Empty marker interface for ownership |
| `GameViewport.OnFrame`, `ResetToDefaults`, `DrawImGui` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L119) | Controller-then-camera order, reset on claim, window close releases |
| Viewport build and seal | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L948-L962) | One main, one thumbnail, four secondary, two portrait, then `SealAllocation` |
| `Program.RenderViewport`, secondary loop in `RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4626) | Reduced secondary pipeline |
| `Program.OnFrameCelestials`, `Program.FindNearbyCelestial` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) | Main-camera-only celestial state; lookup reused per camera (`FindNearbyCelestial` L5228) |
| `Camera.SetFollow`, `ClampCamera`, `EgoToEcl`, `NearbyCelestial` and distance fields, `SetFieldOfView`, `LookAtRotation` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L21-L71) | Pose write and per-camera state (`SetFollow` L602, `ClampCamera` L633) |
| `Part.MatrixAsmb2Ego` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1213-L1224) | Sub-part chain with scale |
| `Vehicle.GetMatrixAsmb2Ego(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281) | Vessel matrix in camera ego space |
| `StaticCelestialDistanceRendering` (`GetEffectiveNearbyCelestial` filter) | [current/decomp/KSA/StaticCelestialDistanceRendering.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticCelestialDistanceRendering.cs#L418-L424) | Skips the camera's nearby body when drawing distant spheres |
| `StaticObjectRenderer.WriteCommandsColor` | [current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L305-L311) | Draws nothing when the viewport camera's `NearbyCelestial` is null |

## Pitfalls and version notes

- Four cameras at most, minus any slot held by the stock Add Camera action, docking cameras or another
  mod. `AvailableSecondaryCount` is shown in the panel; when no slot is free the entry stays with a
  "Reopen viewport" button.
- `changeControl: false` in `SetFollow` is required. The default `true` sets `Program.ControlledVehicle`
  to the followed vessel, so a camera would steal control.[^ksa-camera]
- The main camera is the reference frame for ego math. Using the secondary camera's own (previous)
  pose would add a one-frame floating-origin error.
- 5541 added `Camera.GetEffectiveNearbyCelestial()` (`NearbyCelestial ?? Program.FindNearbyCelestial(this)`),
  and 5554 uses it in `StaticCelestialDistanceRendering`, `PlanetViewportData` and the atmosphere renderer.
  That covers the distant-sphere artifact even without the sync. The sync still matters for code that
  reads `NearbyCelestial` directly: `StaticObjectRenderer.WriteCommandsColor` returns early when it is
  null, so launch pads and other statics are missing from a secondary feed without it, and the
  atmosphere renderer's `DistantPlanet` flag compares against
  it.[^ksa-staticcelestialdistance][^ksa-staticobjectrenderer]
- The camera is terrain-clamped to 0.5 m above the surface (by `Camera.OnFrame` as well), so mounts on
  a landed vessel's underside are lifted.
- Feeds are full extra scene renders; each visible camera costs roughly a reduced main view.
- If another feature also patches `UpdateProjection` for every camera, per-camera FOV breaks; Glass
  limits its own patch to the main camera for this reason. See [Glass](./glass-fov-override.md).
- All hooked and called members are unchanged between 5541 (the build the repo compiles against) and
  5554. `FixedController.OnFrame` is resolved with an explicit parameter list and `Apply` throws
  `MissingMethodException` if it disappears.[^scope-camera]

## See also

- [Mesh-ray placement and stable part anchoring](./hot-pursuit-mesh-ray-placement.md)
- [Glass FOV override](./glass-fov-override.md)
- [Camera animations](./camera-controller-override-camera-animations.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Sub-mod: Hot Pursuit](../submods/hot-pursuit.md)
- [Mod overview](../overview.md)
- [Skip the stock FixedController.OnFrame for the viewports you own with a Harmony prefix](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-3.md) - how-to article demonstrating this feature
- [Re-apply the follow after every viewport claim on a secondary camera](../../../howto/camera/main-view/follow-a-point-that-is-not-a-vehicle/follow-a-point-that-is-not-a-vehicle-5.md) - how-to article demonstrating this feature
- [Write the pose in a FixedController.OnFrame prefix before Camera.OnFrame and synchronize the nearby body inside it](../../../howto/camera/secondary-viewports/aim-a-secondary-viewport-camera/aim-a-secondary-viewport-camera-2.md) - how-to article demonstrating this feature
- [Claim a viewport per camera with an IViewportOwner, track the lease every update and keep KSA's window](../../../howto/camera/secondary-viewports/borrow-a-secondary-viewport/borrow-a-secondary-viewport-2.md) - how-to article demonstrating this feature
- [Pose a camera from the part's Asmb2Ego matrices in a FixedController.OnFrame prefix](../../../howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-1.md) - how-to article demonstrating this feature

[^hp-readme]: hot-pursuit/README.md
[^hp-patches]: hot-pursuit.lib/HotPursuitPatches.cs
[^hp-submod]: hot-pursuit.lib/HotPursuitSubmod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^hp-camera]: hot-pursuit.lib/HotPursuitCamera.cs
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^hp-pose]: hot-pursuit.lib/HotPursuitPose.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^hp-celestial]: hot-pursuit.lib/HotPursuitCelestialState.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-staticcelestialdistance]: KSA.StaticCelestialDistanceRendering (decompiled, build 2026.10.10.5554)
[^ksa-staticobjectrenderer]: KSA.StaticObjectRenderer (decompiled, build 2026.10.10.5554)
[^scope-camera]: scope/camera.md (authors' camera integration analysis)
