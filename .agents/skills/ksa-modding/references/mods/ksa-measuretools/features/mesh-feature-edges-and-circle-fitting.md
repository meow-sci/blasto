---
type: Mod Feature
title: Extract feature edges and fit rim circles from a part's triangle list
description: Welds MeshReference.PositionCompare by exact position, keeps boundary, non-manifold and sharp (over 30 degrees) edges, chains them into loops and fits rim circles with a Newell normal and a Kasa fit, cached per shared mesh
mod: ksa-measuretools
category: vehicles-and-parts
techniques: [mesh-analysis, per-asset-cache]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.MeshReference, KSA.MeshViewModule, KSA.Part]
tags: [mesh, geometry, feature-edges, circle-fit, cad, cache, parts]
---

MeasureTools gives part meshes CAD-like structure that the game does not provide: the sharp and open
edges where a tank wall meets its end cap, and the circles those rims form. The picker snaps to
these edges, their midpoints and the fitted rim centers, and the Circle tool reads a diameter from a
fitted rim. All of it is derived at runtime from the same triangle list the game's own part raycast
uses, so no extra assets are needed.[^mt-meshcache]

## How it works

`MeshFeatureCache.Get(MeshReference mesh)` returns a `MeshFeatures` (welded vertices, feature edge
segments, circles) and builds it on first use from `mesh.PositionCompare`. The cache key is the
`MeshReference` object. Parts get it through `MeshViewModule.MeshView`, which
`MeshViewModule.CreateComponents` resolves with `MeshReference.Get()` to the instance registered in
`ModLibrary`, so every part of one type shares it and the build runs once per mesh type per
session.[^ksa-meshviewmodule][^ksa-meshreference]

1. **Weld.** `PositionCompare` is index-unrolled: `MeshReference.Load` copies
   `vertexSpan[index]` for every index of the first primitive. Shared corners and UV-seam duplicates
   are therefore bitwise-identical `double3` values, and a `Dictionary<(double, double, double), int>`
   welds them exactly, with no epsilon.[^ksa-meshreference]
2. **Edge table.** For each triangle compute the unit face normal (skip degenerate ones) and record
   each undirected edge `(min(v0, v1), max(v0, v1))` with its face count and the first two face
   normals.
3. **Feature edges.** An edge is a feature when its face count is not 2 (boundary or non-manifold) or
   when its two face normals differ by more than 30 degrees (`dot < cos 30`). Smooth cylinder walls
   (small dihedral between facets) stay out; rims and panel corners stay in.
4. **Loops.** Build vertex-to-edge adjacency, then walk from each unvisited edge through vertices that
   have exactly two feature edges. A walk that returns to its start is a closed loop.
5. **Circle fit.** For loops with at least 8 edges: centroid; Newell normal (sum of cross products of
   consecutive centered vertices); an in-plane basis; a Kasa algebraic fit
   (`x^2 + y^2 + a x + b y + c = 0`, normal equations solved by Cramer's rule); then reject the loop
   if any vertex deviates from the radius by more than 2% of the radius or from the plane by more
   than 2% of the radius. Accepted circles store center, unit normal and radius in the subpart's
   local frame.

Everything stays in the subpart's local frame. The picker transforms candidates per frame with
`subPart.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego)`, so part scale and live part motion are handled at
projection time and the cache never needs invalidating.[^mt-mappicker][^ksa-part]

## Lifecycle and timing

The cache is filled lazily from whichever path first needs a mesh's features: a placement click
(`frame:input`) or the hover preview (`[StarMapAfterGui]`, `frame:draw-ui`). The first build for a
large mesh costs real main-thread time; DEBUG builds log the edge and circle counts, triangle count
and milliseconds per mesh.[^mt-meshcache] `MeshFeatureCache.Reset()` is called from
`[StarMapUnload]` and touches no game state.[^mt-mod]

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MeshFeatureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeshFeatureCache.cs#L72-L170) | Cache and feature-edge build |
| [MeasureTools/Features/Measure/MeshFeatureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeshFeatureCache.cs#L172-L310) | Loop chaining and the Newell + Kasa circle fit |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L456-L460) | `GetHitFeatures`: from the hit subpart to its cached features |

## Snippets

```csharp
// MeasureTools/Features/Measure/MeshFeatureCache.cs L103-L163
private static MeshFeatures Build(double3[] triangleVertices)
{
    // Weld by exact position: PositionCompare entries are copies out of the
    // mesh's welded vertex buffer (MeshReference.Load), so shared edges and
    // UV-seam duplicates carry bitwise-identical positions.
    var vertexIds = new Dictionary<(double, double, double), int>();
    var positions = new List<double3>();
    int[] ids = new int[triangleVertices.Length];
    // ...
    var edges = new Dictionary<(int, int), EdgeInfo>();
    for (int t = 0; t + 2 < triangleVertices.Length; t += 3)
    {
        double3 a = triangleVertices[t];
        double3 faceNormal = double3.Cross(triangleVertices[t + 1] - a, triangleVertices[t + 2] - a).NormalizeOrZero();
        if (faceNormal.X == 0.0 && faceNormal.Y == 0.0 && faceNormal.Z == 0.0)
            continue;
        // ...
    }

    double sharpDotThreshold = Math.Cos(FeatureEdgeMinAngleDeg * (Math.PI / 180.0));
    var featureEdges = new List<(int V0, int V1)>();
    foreach (KeyValuePair<(int, int), EdgeInfo> pair in edges)
    {
        EdgeInfo info = pair.Value;
        // Boundary (1) and non-manifold (3+) edges are always features; a
        // manifold edge is one when its faces bend past the threshold.
        bool feature = info.FaceCount != 2
            || double3.Dot(info.Normal1, info.Normal2) < sharpDotThreshold;
        if (feature)
            featureEdges.Add(pair.Key);
    }
```

```csharp
// MeasureTools/Features/Measure/MeshFeatureCache.cs L278-L309
// Solve the 3x3 normal equations for [a b c] via Cramer's rule.
double m00 = sxx, m01 = sxy, m02 = sx;
double m10 = sxy, m11 = syy, m12 = sy;
double m20 = sx, m21 = sy, m22 = n;
double r0 = -sxz, r1 = -syz, r2 = -sz;
double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
if (Math.Abs(det) < 1e-12)
    return false;
// ...
double cx = -a * 0.5;
double cy = -b * 0.5;
double radiusSq = cx * cx + cy * cy - c;
if (!(radiusSq > 0.0))
    return false;
double radius = Math.Sqrt(radiusSq);
// ...
if (maxRadiusResidual > radius * CircleRadiusTolerance || maxPlaneDeviation > radius * CirclePlaneTolerance)
    return false;

double3 center = centroid + u * cx + w * cy;
circle = new CircleFeature(center, normal, radius);
return true;
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `MeshReference.PositionCompare`, `PositionsCompare`, `Load`, `Get`, `Id` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L28-L118) | Source triangles and the shared instance used as cache key |
| `MeshViewModule.MeshView`, `CreateComponents` | [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs) | Mesh per (sub)part |
| `Part.Modules`, `Part.MatrixAsmb2Ego` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1213-L1225) | Module lookup; projecting cached local features |

## Pitfalls and version notes

- Only primitive 0 of a glTF mesh is in `PositionCompare`; rims that live in another primitive are
  not found.
- Exact welding relies on the importer copying positions from one welded buffer. A mesh exported
  with split (non-shared) vertices at slightly different coordinates would produce boundary edges
  everywhere; the 30-degree test does not help there because the faces no longer share an edge.
- Tank rims are often a loop of many short edges; `CircleMinEdges = 8` keeps squares and hexagons
  out of the circle set, so an octagonal part would be treated as a circle.
- The cache is never invalidated during a session; mods that rebuild meshes at runtime would need a
  different key.

## See also

- [Overview](../overview.md)
- [CAD-style snap tiers](./cad-style-snap-tiers.md)
- [Circle and face-angle tools](./circle-and-face-angle-tools.md)
- [Part picking](./part-mesh-raycast-picking.md)
- [Analyse a part mesh: MeshView positions, weld, dihedral feature edges and Kasa circle fit](../../../howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-4.md) - how-to article demonstrating this feature

[^mt-meshcache]: MeasureTools/Features/Measure/MeshFeatureCache.cs
[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-mod]: MeasureTools/Mod.cs
[^ksa-meshreference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^ksa-meshviewmodule]: KSA.MeshViewModule (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
