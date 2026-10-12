---
type: Game Feature
title: Part meshes, attach nodes and part-local points in vehicle, world and screen space
description: How KSA stores part geometry and the transform chain from a part-local point to vehicle assembly, ego, ECL and screen coordinates
category: vehicles-and-parts
group: part-geometry
topic: get-part-points-in-vehicle-and-world-frames
demonstrated_by:
  - mods/ksa-measuretools/features/part-mesh-raycast-picking
  - mods/ksa-measuretools/features/part-anchor-rehoming
  - mods/ksa-measuretools/features/cad-style-snap-tiers
  - mods/ksa-measuretools/features/mesh-feature-edges-and-circle-fitting
  - mods/ksa-measuretools/features/circle-and-face-angle-tools
  - mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone
lifecycle: [frame:input, frame:draw-ui, frame:prepare, render:scene-opaque]
game_types:
  - KSA.Part
  - KSA.PartConnectorInstance
  - KSA.Vehicle
  - KSA.EngineController
  - KSA.MeshReference
  - KSA.PartModel
  - KSA.MeshViewModule
  - KSA.PartInstance
tags: [parts, geometry, connectors, mesh, transforms, asmb, ego, ecl]
---
A part's shape and anchor points are not stored as one world-space number. Every `Part` keeps its position, rotation and scale
relative to its parent, the vehicle caches the part-to-vehicle matrix, and the world position only exists once the vehicle's own pose
(and, for drawing, the camera) is applied. This topic is about walking that chain yourself, without the render camera, and about
reading the part's triangles for geometry analysis.

## Engine background

- **Which type holds geometry.** The runtime `Part` is what you read. `PartInstance` is the serialized description of a part tree
  (`Transform`, `Children`, `SubPartInstances`, `Connectors`) and `PartConnectorInstance` is only a save record
  (`Index` plus `ConnectedLocalInstanceId`) [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L36-L37)[^ksa-partinstance] [current/decomp/KSA/PartConnectorInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartConnectorInstance.cs#L5-L11)[^ksa-partconnectorinstance]. `PartModel` holds per-viewport GPU draw
  commands and per-instance data for rendering, and draws from `Template.Mesh.DeviceMeshInterleaved` [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L500-L503)[^ksa-partmodel]. None of those
  is a place to read positions.
- **Frames.** A part's `Asmb2ParentAsmb`, `PositionParentAsmb` and `Scale` give `MatrixAsmb2ParentAsmb`; `Part.MatrixAsmb2VehicleAsmb` chains it
  through `PartParent` for subparts. Both are cached in flight and recomputed on every call while `Program.Editor != null`
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L740-L754)[^ksa-part]. The vehicle frame is "assembly" (`asmb`) space, origin at the assembly origin, not the centre of mass.
- **Vehicle to world.** `Vehicle.Asmb2Ego` is `Body2Cce` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502-L508)[^ksa-vehicle]; `Vehicle.CenterOfMassAsmb` is the offset to the centre of mass; `Vehicle.GetMatrixAsmb2Ego(bodyPositionEgo)`
  rotates by `Asmb2Ego` and translates to `bodyPositionEgo - CenterOfMassAsmb.Transform(Asmb2Ego)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1276-L1282).
  Ego space is camera-relative; `Camera.EgoToEcl` is a pure translation by `Camera.PositionEcl` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L226)[^ksa-camera], so ego and ECL share axes.
- **Connectors.** `Part.Connector` is the runtime attach node: `PositionParentAsmb`, `Asmb2ParentAsmb`, `Flags`
  (`Internal`, `ToSurface`, `FromSurface`) and a vehicle-frame position that applies the parent's rotation and translation but not its scale
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L191-L266).
- **Mesh data.** `Part.Modules.Get<MeshViewModule>()[0].MeshView` is a `MeshReference`; the module is created with `template.MeshView.Get()`, the shared
  `ModLibrary` instance [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs#L34-L47)[^ksa-meshviewmodule]. `MeshReference.PositionsCompare[i]` holds primitive `i`'s positions unrolled through its index buffer
  (three entries per triangle) [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L82-L104)[^ksa-meshreference].

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [1: `Connector.PositionEgo` and `PositionVehicleAsmb`](get-part-points-in-vehicle-and-world-frames-1.md) | KSA-MeasureTools | `screen-space-picking` | attach nodes and part centres projected to the screen; no part scale |
| [2: part-local to world without a camera](get-part-points-in-vehicle-and-world-frames-2.md) | KSA-MeasureTools | `frame-relative-anchors` | `(posVehicleAsmb - CenterOfMassAsmb)` rotated by `Asmb2Ego` about `GetPositionEcl()` |
| [3: stack axis and axial/radial split](get-part-points-in-vehicle-and-world-frames-3.md) | KSA-MeasureTools | `screen-space-picking` | connector facing, asmb X as the stack axis, surface connectors skipped |
| [4: mesh positions, weld, dihedral edges, circle fit](get-part-points-in-vehicle-and-world-frames-4.md) | KSA-MeasureTools | `mesh-analysis` | rims and edges of the shared `MeshReference`, cached per asset |
| [5: clone an interleaved mesh into a Simple mesh](get-part-points-in-vehicle-and-world-frames-5.md) | unscience | `vulkan-buffer` | `SimpleVkMesh` from `HostPrimitives` for renderers that cannot draw interleaved meshes |

Pick 1 when you need "where is this node on screen or in vehicle space", 2 when a point must be turned into an ECL position every frame without a camera,
3 for stack-axis maths, 4 when you need the triangles, and 5 when a different renderer must draw a part's mesh.

## See also

- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for when part transforms are settled
- [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), the stock `Part.RayCastEgo` triangle test
- [Keep an anchor on a part through staging, docking, editor changes and saves](../anchor-to-parts-across-vehicle-changes/overview.md)
- [Draw an engine diagram from nozzle positions and live engine state](../draw-an-engine-diagram-from-nozzle-positions/overview.md)
- Mod features: [part-mesh-raycast-picking](../../../../mods/ksa-measuretools/features/part-mesh-raycast-picking.md), [part-anchor-rehoming](../../../../mods/ksa-measuretools/features/part-anchor-rehoming.md), [cad-style-snap-tiers](../../../../mods/ksa-measuretools/features/cad-style-snap-tiers.md), [mesh-feature-edges-and-circle-fitting](../../../../mods/ksa-measuretools/features/mesh-feature-edges-and-circle-fitting.md), [unscience rocky-mcrock-face-interleaved-mesh-clone](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md)

[^ksa-partinstance]: current/decomp/KSA/PartInstance.cs (decompiled, build 2026.10.10.5554)
[^ksa-partconnectorinstance]: current/decomp/KSA/PartConnectorInstance.cs (decompiled, build 2026.10.10.5554)
[^ksa-partmodel]: current/decomp/KSA/PartModel.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-camera]: current/decomp/KSA/Camera.cs (decompiled, build 2026.10.10.5554)
[^ksa-meshviewmodule]: current/decomp/KSA/MeshViewModule.cs (decompiled, build 2026.10.10.5554)
[^ksa-meshreference]: current/decomp/KSA/MeshReference.cs (decompiled, build 2026.10.10.5554)
