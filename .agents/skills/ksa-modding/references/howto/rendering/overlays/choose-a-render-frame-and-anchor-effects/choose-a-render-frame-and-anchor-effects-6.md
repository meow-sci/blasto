---
type: How-To
title: Part-local, cloth-barycentric or geodetic anchors with double-precision composition
description: Anchor decals to parts, canopy cloth triangles or a latitude and longitude by re-resolving stable ids each frame and composing the decal-to-ego matrix in double precision
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
approach: 6
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/graffiti-decal-anchoring]
techniques: [double-precision-transforms]
hooks: [starmap StarMapBeforeGui (via unscience ISubmod.Update), harmony-prefix KSA.Program.OnDrawUiConsole]
lifecycle: [frame:draw-ui]
game_types: [KSA.Part, KSA.PartTree, KSA.Vehicle, KSA.Celestial, KSA.Camera]
tags: [anchor, decal, part, cloth, geodetic, double-precision, reference-frames]
---

Outcome: a decal, sticker or marker that stays stuck to a hull through staging, docking and robotics, to a parachute canopy while it reefs and flutters, and to the ground while the planet turns under a floating origin. The anchor is never an ego or ecliptic position: each entry keeps
an anchor in the frame of the thing it was placed on, re-finds that object by a stable id every frame, and composes the projection box in `double` precision before packing to `float` for the shader. This is the part/cloth/terrain anchoring sub-step. Demonstrated by unscience Graffiti
([Per-frame decal anchoring to parts, canopy cloth and terrain](../../../../mods/unscience/features/graffiti-decal-anchoring.md)); the pass that consumes the matrices is in [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md). Prerequisites: StarMap.

## Background: how the game does it

`Camera.GetPositionEgo(IPosition)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250) and `Vehicle.GetMatrixAsmb2Ego(camera)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270) give the vehicle-to-ego transform; parts and sub-parts expose `MatrixAsmb2Ego` chains; a planet's rotation is `Celestial.GetCcf2Cce()` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532);
`Vehicle.ComputeEnu2Cce` builds an east-north-up basis for a surface point. KSA matrices are row-vector (`v * M`), so composition reads left to right. Part `InstanceId`s are stable within a session; parachute module ids change on reload.

## Lifecycle and timing

All of this runs in `GraffitiSubmod.Update`, called from unscience's `[StarMapBeforeGui]` (a prefix of `Program.OnDrawUiFrame`, or the hidden-HUD replay in `OnDrawUiConsole`), i.e. `frame:draw-ui`. At that point part and vehicle transforms hold the step applied in `PrepareFrame`; canopy nodes
are the cloth buffer swapped in by `ApplyClothSolvers`; and the main camera is final, so matrices composed here match the camera `RenderGame` records with and decals do not swim. Composition must be redone every frame: ego is camera-relative, and both the camera and the planet's rotation change
continuously. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Store the anchor in the frame of its object:** `Vehicle` entries keep the hit point and normal in the hit sub-part's frame plus a roll; `Parachute` entries keep three cloth node indices, barycentric weights and the clicked side; `Terrain` entries keep latitude, longitude, compass heading and a cached surface radius.

2. **Re-resolve by stable id each frame,** with the reload fallback for canopies (parent part id plus canopy index when the module id changed). A decal whose anchor is gone becomes dormant, not pruned, and reappears if the anchor returns.

   ```csharp
   // graffiti.lib/GraffitiSubmod.cs L255-L283
   private static void ResolveAnchor(DecalEntry entry)
   {
       var system = Universe.CurrentSystem;
       if (entry.Kind == DecalAnchorKind.Terrain)
       {
           entry.Body = system?.Get(entry.TargetId) as Celestial;
           return;
       }

       var vehicle = system?.Get(entry.TargetId) as Vehicle;
       entry.Vehicle = vehicle;
       entry.Part = vehicle == null ? null : FindPart(vehicle, entry.PartInstanceId);
       entry.Parachute = entry.Kind == DecalAnchorKind.Parachute && vehicle != null
           ? FindParachute(vehicle, entry.ParachuteInstanceId, entry.PartInstanceId,
               entry.ParachuteCanopyIndex)
           : null;
   }

   private static Parachute? FindParachute(Vehicle vehicle, ulong instanceId, uint partInstanceId,
       int canopyIndex)
   {
       foreach (var parachute in vehicle.Parts.Modules.Get<Parachute>())
           if (parachute.InstanceId == instanceId)
               return parachute;
       foreach (var parachute in vehicle.Parts.Modules.Get<Parachute>())
           if (parachute.Parent.InstanceId == partInstanceId && parachute.CanopyIndex == canopyIndex)
               return parachute;
       return null;
   }
   ```

3. **Terrain anchors:** compute the radius once from `MeanRadius + GetTerrainHeightFromDirCcf(dir, accurate: true)` and cache it, rotate to Cce with `GetCcf2Cce()`, build the ENU basis, apply heading, and place at `camera.GetPositionEgo(body) + surfaceCce`; deepen the box with camera distance because the rendered terrain LOD drifts metres off the true height:

   ```csharp
   // graffiti.lib/DecalAnchors.cs L81-L123
   var dirCcf = body.GetDirCcfFromLatLon(entry.Position.X, entry.Position.Y);
   var radius = entry.TerrainRadius
                ?? body.MeanRadius + body.GetTerrainHeightFromDirCcf(dirCcf, accurate: true);
   if (!double.IsFinite(radius))
       return false;
   entry.TerrainRadius = radius;

   var ccf2Cce = body.GetCcf2Cce();
   var surfaceCce = (dirCcf * radius).Transform(ccf2Cce);

   double3 east, north, up;
   if (Vehicle.ComputeEnu2Cce(surfaceCce, body.GetCci2Cce()) is { } enu2Cce)
   {
       east = double3.UnitX.Transform(enu2Cce);
       north = double3.UnitY.Transform(enu2Cce);
       up = double3.UnitZ.Transform(enu2Cce);
   }
   else
   {
       // Exactly on the spin axis ENU has no north and ComputeEnu2Cce returns null — a pole
       // decal gets an arbitrary but frame-stable tangent basis about the radial instead of
       // being dormant forever.
       var length = surfaceCce.Length();
       if (!double.IsFinite(length) || length <= 0)
           return false;
       up = surfaceCce / length;
       if (!TryTangent(double3.UnitX, up, out north) && !TryTangent(double3.UnitY, up, out north))
           return false;
       east = double3.Cross(north, up);
   }

   // heading 0 = the PNG's up points north; +90 = east (a compass bearing).
   var heading = entry.RotationDeg * Deg2Rad;
   var cos = Math.Cos(heading);
   var sin = Math.Sin(heading);
   var axisY = north * cos + east * sin;
   var axisX = east * cos - north * sin;

   var positionEgo = camera.GetPositionEgo(body) + surfaceCce;
   // Deepen the box with camera distance so the LOD-morphed rendered surface stays inside
   // it at planetary zoom (see TerrainDepthPerMetre). Never below the user's chosen depth.
   var depth = Math.Max(entry.Depth, positionEgo.Length() * TerrainDepthPerMetre);
   return Finish(entry, Basis(axisX, axisY, up), positionEgo, double4x4.Identity, useParent: false, depth);
   ```

4. **Compose and invert in double, pack last.** Inverting the packed float matrix would lose the surface point to cancellation at kilometre distances:

   ```csharp
   // graffiti.lib/DecalAnchors.cs L197-L221
   private static bool Finish(
       DecalEntry entry, double4x4 rotation, double3 translation, double4x4 parent, bool useParent,
       double depth)
   {
       var scale = double4x4.CreateScale(entry.Width, entry.Height, depth);
       var local = scale * rotation * double4x4.CreateTranslation(translation);
       var decalToEgo = useParent ? local * parent : local;
       if (!double.IsFinite(decalToEgo.W.X) || !double.IsFinite(decalToEgo.W.Y)
           || !double.IsFinite(decalToEgo.W.Z))
           return false;
       if (!double4x4.Invert(decalToEgo, out var egoToDecal))
           return false;

       var originEgo = decalToEgo.W.XYZ;
       var outward = decalToEgo.Z.XYZ;
       var outwardLength = outward.Length();
       if (!double.IsFinite(outwardLength) || outwardLength <= 0)
           return false;

       entry.DecalToEgo = float4x4.Pack(in decalToEgo);
       entry.EgoToDecal = float4x4.Pack(in egoToDecal);
       entry.AxisZEgo = float3.Pack(outward / outwardLength);
       entry.DistanceEgo = originEgo.Length();
       return double.IsFinite(entry.DistanceEgo);
   }
   ```

5. Vehicle and canopy anchors multiply the local matrix by the anchor's current local-to-ego matrix (`part.MatrixAsmb2Ego(vehicle.GetMatrixAsmb2Ego(camera))`); for a canopy the point and normal are recomputed from the current three cloth nodes, so the basis flexes with the triangle.

## Complete example

| File | Role |
|---|---|
| [graffiti.lib/DecalAnchors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalAnchors.cs) | Composition for all three anchor kinds |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs#L255-L283) | Stable-id resolution |

## Variations and gotchas

- **Row-vector matrices:** `S * R * T * parent`, with the decal axes in the rows of the basis.
- **Terrain radius** must use the accurate height path; a past build changed the radius inputs of the accurate modifiers, and terrain decals need a live check after updates.
- **Pole decals** get an arbitrary but frame-stable tangent basis instead of staying dormant (`ComputeEnu2Cce` returns null exactly on the spin axis).
- **Kitten decals ride the root part,** not the animated avatar pose.
- **Dormant is not deleted:** save adapters persist the anchors, with shared saved-part addresses replacing runtime instance ids.

## See also

- [Overview](overview.md)
- [Approach 1: two-instant draw anchor](choose-a-render-frame-and-anchor-effects-1.md)
- [Per-frame decal anchoring to parts, canopy cloth and terrain](../../../../mods/unscience/features/graffiti-decal-anchoring.md)
- [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md), [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md)
