---
type: Mod Feature
title: Per-frame decal anchoring to parts, canopy cloth and terrain
description: Decals store part-local, cloth-barycentric or geodetic anchors, re-resolve them by stable ids every frame and recompose a double-precision decal-to-ego box, so they follow staging, inflation, planet rotation and floating-origin shifts and go dormant instead of being deleted when the anchor despawns
mod: unscience
submod: graffiti
category: rendering
techniques: [starmap-lifecycle, double-precision-transforms]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replays ISubmod.Update while the HUD is hidden)
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.Vehicle
  - KSA.Part
  - KSA.PartTree
  - KSA.Parachute
  - KSA.ModuleBase
  - KSA.Celestial
  - KSA.Camera
  - KSA.Program
tags: [decals, coordinate-frames, ego-space, geodetic, parachutes, precision]
---

A projected decal is only useful if it stays stuck: to a hull through staging and robotics, to a
canopy while it reefs, inflates and flutters, and to the ground while the planet turns under a
floating origin. Graffiti never stores an ego-space or ecliptic position. Each decal keeps an anchor
in the frame of the thing it was sprayed on and rebuilds its projection box from that every
frame.[^decal-entry]

Mechanism in one line: once per frame, re-find the anchor object by stable id, compose
`S * R * T * parent` (scale = width/height/depth, rotation = decal basis, translation = anchor point,
parent = the anchor's current local-to-ego matrix) in `double`, invert it in `double`, and only then
pack both matrices to `float` for the shader.

## How it works

**Anchor kinds** (`DecalAnchorKind`, stored in `DecalEntry`):

| Kind | Stored | Re-resolved by | Parent matrix each frame |
|---|---|---|---|
| `Vehicle` (also `KittenEva`) | Hit point and normal in the hit sub-part's frame, roll | Vehicle id via `Universe.CurrentSystem.Get(id)`, then a recursive search of `Parts.Parts` and `SubParts` for `Part.InstanceId` | `part.MatrixAsmb2Ego(vehicle.GetMatrixAsmb2Ego(camera))` (includes part scale and the sub-part chain) |
| `Parachute` | Three cloth node indices, barycentric weights, clicked side, canopy index | `Parachute.InstanceId` first, else parent part `InstanceId` + `CanopyIndex` (survives a reload, where module ids change) | Attach point `AttachLocationPartAsmb` through `Parent.MatrixAsmb2VehicleAsmb`, translated into the vehicle-to-ego matrix (the same math as `Parachute.BuildLocal2Ego`) |
| `Terrain` | Latitude, longitude (degrees), compass heading, cached surface radius | Body id via `Universe.CurrentSystem.Get(id)` | None: ENU basis from `Vehicle.ComputeEnu2Cce`, placed at `camera.GetPositionEgo(body) + surfaceCce` |

**Composition** (`DecalAnchors.TryCompose`):[^decal-anchors]

- Vehicle and canopy: the "PNG up" axis is the anchor frame's +Y projected onto the tangent plane
  (+X if +Y is parallel to the normal), rotated by the stored roll. For a canopy, the point and the
  normal are recomputed from the current three cloth nodes, so the basis flexes with the triangle.
- Terrain: the radius is `MeanRadius + GetTerrainHeightFromDirCcf(dir, accurate: true)`, computed once
  and cached (`DecalEntry.TerrainRadius`). Heading 0 points the PNG's up north. Exactly on the spin
  axis `ComputeEnu2Cce` returns null, so a pole decal gets an arbitrary but stable tangent basis
  instead of staying dormant.[^ksa-vehicle][^ksa-celestial]
- Box depth: the placement default is `max(0.3 m hull or 2 m terrain, half the larger side)`; terrain
  boxes are additionally deepened to 1 % of the camera distance at compose time because the rendered
  terrain is a screen-space-error LOD mesh that drifts metres off the true height at range.[^graffiti-submod]
- `Finish` rejects non-finite or singular matrices, stores `DecalToEgo`, `EgoToDecal`, the outward
  axis and the camera distance used by the draw-distance cull.

**Dormancy.** `GraffitiSubmod.Update` re-resolves every entry, sets `Live` only when a texture slot,
the camera and a successful compose all exist, and keeps the pipeline alive while any entry exists.
A decal whose vehicle despawned, whose canopy is stowed or whose body left the system is dormant (the
panel shows `[anchor gone]`) and reappears if the anchor comes back; it is never pruned.[^graffiti-submod]
The same anchors are what the save adapter persists, with shared saved-part addresses replacing
runtime instance ids.[^graffiti-saves]

## Lifecycle and timing

All of this runs in `GraffitiSubmod.Update`, called from unscience's `[StarMapBeforeGui]`, a prefix
of `Program.OnDrawUiFrame` (or from the hidden-HUD replay in `OnDrawUiConsole`), i.e.
`frame:draw-ui`.[^graffiti-submod] At that point:

- part and vehicle transforms hold the physics step applied in `PrepareFrame`'s `ApplyVehicleSolvers`;
  the next step already runs on workers, but graffiti only reads;
- canopy nodes are the `ClothPositionsFront` buffer swapped in by `ApplyClothSolvers`, the same array
  the canopy renderer reads later this frame;[^ksa-parachute]
- the main camera is final (`OnFrameViewports` ran; `OnFrameController` only changes the cursor
  mode), so matrices composed here match the camera `RenderGame` records with and decals do not
  swim.[^ksa-program]

Composition must be redone every frame: ego space is camera-relative, and both the camera and the
planet's `GetCcf2Cce` rotation change continuously. The results are consumed by the
[projected decal pass](./graffiti-projected-decal-pass.md) in `render:grid` of the same frame.

## Key code

| Path | Role |
|---|---|
| [graffiti.lib/DecalEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalEntry.cs#L37-L143) | Anchor fields, per-frame resolution and render outputs |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs#L255-L299) | `ResolveAnchor`, `FindParachute`, recursive `FindPart` |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs#L380-L381) | `AutoDepth` |
| [graffiti.lib/DecalAnchors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalAnchors.cs) | Terrain, vehicle and canopy composition; `Finish`; `Basis` |

## Snippets

Re-resolution by stable ids, with the reload fallback for canopies:

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

Following a live canopy triangle:

```csharp
// graffiti.lib/DecalAnchors.cs L150-L176
    private static bool TryComposeParachute(DecalEntry entry, Camera camera)
    {
        if (entry.Vehicle is not { } vehicle || entry.Parachute is not { } parachute
            || parachute.ClothPositionsFront is not { } positions)
            return false;
        var maxNode = Math.Max(entry.ClothNodeA, Math.Max(entry.ClothNodeB, entry.ClothNodeC));
        if (entry.ClothNodeA < 0 || entry.ClothNodeB < 0 || entry.ClothNodeC < 0
            || maxNode >= positions.Length)
            return false;

        var a = double3.Unpack(in positions[entry.ClothNodeA]);
        var b = double3.Unpack(in positions[entry.ClothNodeB]);
        var c = double3.Unpack(in positions[entry.ClothNodeC]);
        var position = a * entry.ClothBarycentric.X + b * entry.ClothBarycentric.Y
                       + c * entry.ClothBarycentric.Z;
        var normal = double3.Cross(b - a, c - a) * entry.ClothNormalSign;
        var normalLength = normal.Length();
        if (!double.IsFinite(normalLength) || normalLength <= 0)
            return false;
        normal /= normalLength;

        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
        var attachAsmb = double3.Unpack(in parachute.AttachLocationPartAsmb)
            .Transform(parachute.Parent.MatrixAsmb2VehicleAsmb);
        var local2Ego = double4x4.CreateTranslation(attachAsmb) * vehicleMatrix;
        return FinishVehicleLike(entry, position, normal, local2Ego);
    }
```

Geodetic anchor: accurate radius cached once, ENU basis, heading, distance-scaled depth:

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
        // ...
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

Compose and invert in double, pack last:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.CurrentSystem`, `CelestialSystem.Get(string)` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L295) | Vehicle and body lookup by persistent id |
| `Part.InstanceId`, `SubParts`, `MatrixAsmb2Ego(ref readonly double4x4)`, `MatrixAsmb2VehicleAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1200-L1215) | Sub-part lookup (`InstanceId` L574, `SubParts` L1127) and local-to-ego |
| `Vehicle.GetMatrixAsmb2Ego(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1280) | Vehicle assembly frame relative to the camera |
| `Vehicle.ComputeEnu2Cce(double3, doubleQuat)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3175-L3193) | ENU basis; returns null on the spin axis |
| `Parachute.ClothPositionsFront`, `AttachLocationPartAsmb`, `CanopyIndex`, `BuildLocal2Ego` | [current/decomp/KSA/Parachute.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Parachute.cs#L1226-L1230) | Canopy frame mirrored from the game's draw path |
| `Celestial.GetDirCcfFromLatLon`, `GetTerrainHeightFromDirCcf`, `GetCcf2Cce`, `GetCci2Cce`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L667-L800) | Geodetic to body-fixed to ecliptic |
| `Program.GetMainCamera()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631) | Camera used for this frame's ego space |

## Pitfalls and version notes

- **Invert in double.** Inverting the packed float matrix would lose the surface point to cancellation
  at kilometre distances; the matrices are only packed after both are computed.[^decal-anchors]
- **KSA matrices are row-vector** (`v * M`): composition reads left to right as `S * R * T * parent`,
  and `Basis` puts the decal axes in rows, the same shape `ComputeEnu2Cce` builds.[^ksa-vehicle]
- **Parachute module ids change on reload**; the parent part id plus `CanopyIndex` fallback restores
  the anchor, and cloth weights survive because the topology is shared (8 x 16) for every canopy.[^ksa-parachute]
- **Terrain radius** must use the accurate height path; 5402 changed the radius inputs of the accurate
  modifiers to `MeanRadius`, and the authors flag terrain decals as needing a live check since then.[^scope-decals]
- Kitten decals ride the root part, not the animated avatar pose.

## See also

- [Click placement and picking](./graffiti-decal-placement-picking.md)
- [Projected decal pass](./graffiti-projected-decal-pass.md)
- [Saved part identity](./lib-save-part-identity.md) (how vehicle anchors are saved)
- [Graffiti sub-mod](../submods/graffiti.md) and the [mod overview](../overview.md)
- [Part-local, cloth-barycentric or geodetic anchors with double-precision composition](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-6.md) - how-to article demonstrating this feature

[^decal-anchors]: graffiti.lib/DecalAnchors.cs
[^decal-entry]: graffiti.lib/DecalEntry.cs
[^graffiti-submod]: graffiti.lib/GraffitiSubmod.cs
[^graffiti-saves]: graffiti.lib/GraffitiSubmod.Saves.cs
[^scope-decals]: scope/decals.md (authors' graffiti integration analysis)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-parachute]: KSA.Parachute (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
