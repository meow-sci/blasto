---
type: How-To
title: "Analyse a part mesh: MeshView positions, weld, dihedral feature edges and Kasa circle fit"
description: Read the index-unrolled triangle positions of a shared MeshReference and derive welded vertices, sharp edges and fitted rim circles, cached per asset
category: vehicles-and-parts
group: part-geometry
topic: get-part-points-in-vehicle-and-world-frames
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-measuretools/features/mesh-feature-edges-and-circle-fitting
  - mods/ksa-measuretools/features/circle-and-face-angle-tools
techniques: [mesh-analysis]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.MeshReference, KSA.MeshViewModule, KSA.Part]
tags: [mesh, geometry, circle-fit, edges, cache]
---
Outcome: get a part mesh's welded vertices, its sharp or open ("feature") edges and the circles those rims form, from the same triangle list the game uses for part ray casts, with
no extra assets. MeasureTools derives a tank's rim centre and diameter this way; any mod that needs structure from a part mesh can reuse the recipe.

Prerequisite: none beyond a hook on the main thread (this is pure maths on cached arrays). Demonstrated by
[ksa-measuretools/mesh-feature-edges-and-circle-fitting](../../../../mods/ksa-measuretools/features/mesh-feature-edges-and-circle-fitting.md) and [circle-and-face-angle-tools](../../../../mods/ksa-measuretools/features/circle-and-face-angle-tools.md).

## Background: how the game does it

`MeshReference.Load` imports each glTF primitive as a `MeshAsset` with positions and normals, then fills `PositionsCompare[j]` with `vertexSpan[index]` for every index of the primitive's index buffer:

```csharp
// current/decomp/KSA/MeshReference.cs L96-L104
for (int j = 0; j < num; j++)
{
    Span<int> span = HostPrimitives[j].IndexBuffer.ToSpan<int>();
    Span<float3> vertexSpan = HostPrimitives[j].GetVertexSpan<float3>(MeshAttribute.Position);
    PositionsCompare[j] = new double3[span.Length];
    for (int k = 0; k < span.Length; k++)
    {
        PositionsCompare[j][k] = double3.Unpack(in vertexSpan[span[k]]);
    }
```

So `MeshReference.PositionCompare` (`PositionsCompare[0]`) is **primitive 0 only**, **index-unrolled** (a triangle list, three entries per triangle) and in the mesh's local frame [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L50-L56)[^ksa-meshreference].
Parts reach it through `Part.Modules.Get<MeshViewModule>()[0].MeshView`. `MeshViewModule.CreateComponents` resolves the view with `MeshReference.Get()`, which returns the one instance registered in
`ModLibrary`, so **every part of one type shares a `MeshReference`** [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs#L34-L47)[^ksa-meshviewmodule]. Cache per `MeshReference` object, not per part.

## Lifecycle and timing

MeasureTools fills the cache lazily from whichever path first needs a mesh: the placement click (`frame:input`) or the hover preview (`[StarMapAfterGui]`, `frame:draw-ui`). The first build of
a large mesh costs real main-thread time. Because results stay in the subpart's local frame, they never need invalidating; they are transformed per frame with
`subPart.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego)` ([article 1](get-part-points-in-vehicle-and-world-frames-1.md)). Reset the cache from `[StarMapUnload]`. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Fetch the mesh and key the cache on the `MeshReference`.** From the hit subpart:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L456-L460
private static MeshFeatureCache.MeshFeatures GetHitFeatures(ref readonly PartHit hit)
{
    // The hit subpart was just raycast successfully, so its mesh view exists.
    return MeshFeatureCache.Get(hit.SubPart!.Modules.Get<MeshViewModule>()[0].MeshView);
}
```

2. **Weld.** Because `PositionCompare` copies out of one welded vertex buffer, shared corners and UV-seam duplicates are bitwise-identical `double3` values; a dictionary keyed on the exact triple welds them with
   no epsilon. Then build an edge table per triangle (face normal, face count, first two normals) and mark an edge as a feature when it is boundary or non-manifold (face count not 2) or the two face normals differ by more than 30 degrees:

```csharp
// MeasureTools/Features/Measure/MeshFeatureCache.cs L103-L110,L152-L163
private static MeshFeatures Build(double3[] triangleVertices)
{
    // Weld by exact position: PositionCompare entries are copies out of the
    // mesh's welded vertex buffer (MeshReference.Load), so shared edges and
    // UV-seam duplicates carry bitwise-identical positions.
    var vertexIds = new Dictionary<(double, double, double), int>();
    var positions = new List<double3>();
    int[] ids = new int[triangleVertices.Length];
// ...
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

3. **Chain loops and fit circles.** Walk adjacency through vertices with exactly two feature edges; a walk that returns to its start is a closed loop. For loops of at least 8 edges take the centroid, the Newell
   normal, an in-plane basis, and a Kasa algebraic fit solved with Cramer's rule; reject the loop if any vertex is more than 2% of the radius off the fitted radius or plane:

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
double a = (r0 * (m11 * m22 - m12 * m21) - m01 * (r1 * m22 - m12 * r2) + m02 * (r1 * m21 - m11 * r2)) / det;
double b = (m00 * (r1 * m22 - m12 * r2) - r0 * (m10 * m22 - m12 * m20) + m02 * (m10 * r2 - r1 * m20)) / det;
double c = (m00 * (m11 * r2 - r1 * m21) - m01 * (m10 * r2 - r1 * m20) + r0 * (m10 * m21 - m11 * m20)) / det;
double cx = -a * 0.5;
double cy = -b * 0.5;
double radiusSq = cx * cx + cy * cy - c;
if (!(radiusSq > 0.0))
    return false;
double radius = Math.Sqrt(radiusSq);

double maxRadiusResidual = 0.0;
for (int i = 0; i < n; i++)
{
    double3 d = positions[loop[i]] - centroid;
    double x = double3.Dot(d, u) - cx;
    double y = double3.Dot(d, w) - cy;
    maxRadiusResidual = Math.Max(maxRadiusResidual, Math.Abs(Math.Sqrt(x * x + y * y) - radius));
}
if (maxRadiusResidual > radius * CircleRadiusTolerance || maxPlaneDeviation > radius * CirclePlaneTolerance)
    return false;

double3 center = centroid + u * cx + w * cy;
circle = new CircleFeature(center, normal, radius);
return true;
```

4. **Use the result.** Project the cached local circle centres, vertices and edge midpoints per frame and offer them as snap targets ([article 3](get-part-points-in-vehicle-and-world-frames-3.md)), or read `radius * 2` as a rim diameter.

## Complete example

- [MeasureTools/Features/Measure/MeshFeatureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeshFeatureCache.cs#L72-L170)[^ksa-measuretools-meshfeaturecache] - cache and feature-edge build.
- [MeasureTools/Features/Measure/MeshFeatureCache.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeshFeatureCache.cs#L172-L310): loop chaining and the circle fit.
- [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L462-L487)[^ksa-measuretools-mappicker] - using fitted rim centres as snap candidates.

## Variations and gotchas

- **Primitive 0 only.** Multi-primitive (multi-material) meshes are analysed on the first primitive only; rims in other primitives are not found. The same limit applies to the stock `Part.RayCastEgo`.
- **Exact welding assumes the importer copied one buffer.** A mesh with split vertices at slightly different coordinates would produce boundary edges everywhere.
- **`CircleMinEdges = 8`** keeps squares and hexagons out; an octagonal part would be treated as a circle.
- **Never invalidated.** If a mod rebuilds meshes at runtime, key on something else.
- **Linear cost.** There is no BVH; `RaycastWatertight` loops over every triangle, so gate by projected size first.

## See also

- [Overview](overview.md); [Article 5](get-part-points-in-vehicle-and-world-frames-5.md)
- [ksa-measuretools/mesh-feature-edges-and-circle-fitting](../../../../mods/ksa-measuretools/features/mesh-feature-edges-and-circle-fitting.md), [circle-and-face-angle-tools](../../../../mods/ksa-measuretools/features/circle-and-face-angle-tools.md)
- [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)

[^ksa-meshreference]: current/decomp/KSA/MeshReference.cs (decompiled, build 2026.10.10.5554)
[^ksa-meshviewmodule]: current/decomp/KSA/MeshViewModule.cs (decompiled, build 2026.10.10.5554)
[^ksa-measuretools-meshfeaturecache]: KSA-MeasureTools: MeasureTools/Features/Measure/MeshFeatureCache.cs
[^ksa-measuretools-mappicker]: KSA-MeasureTools: MeasureTools/Features/Measure/MapPicker.cs
