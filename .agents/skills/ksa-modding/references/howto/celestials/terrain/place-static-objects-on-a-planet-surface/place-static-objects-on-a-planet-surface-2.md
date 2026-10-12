---
type: How-To
title: GroundAnchor composed each frame into a camera-relative matrix
description: Anchor a mod-drawn model to a planet with a body-fixed position and normal, rebuild its camera-relative matrix every frame in the render-data hook, and restore it from the scene save
category: celestials
group: terrain
topic: place-static-objects-on-a-planet-surface
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/sphinx-glb-statics-placement
  - mods/unscience/features/sphinx-static-object-render-hooks
techniques: [glb-model, harmony-postfix]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update)
  - harmony-postfix KSA.StaticObjectRenderer.UpdateRenderData
  - harmony-postfix KSA.GameSave.Populate
  - harmony-postfix KSA.Universe.DeserializeSave
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types:
  - KSA.Celestial
  - KSA.Camera
  - KSA.IViewport
  - KSA.StaticObjectRenderer
  - KSA.Program
  - KSA.Cursor
  - KSA.Vehicle
  - KSA.JobSystems
  - KSA.Universe
tags: [static-object, anchor, ccf, glb, placement, save]
---
Outcome: an imported GLB model stands on a planet or moon, follows the body's rotation, survives floating-origin shifts, can be placed by terrain click or beside a vessel, and is restored from the scene save. Demonstrated by unscience's Sphinx submod ([Sphinx GLB statics placement](../../../../mods/unscience/features/sphinx-glb-statics-placement.md), [Sphinx static-object render hooks](../../../../mods/unscience/features/sphinx-static-object-render-hooks.md)). This approach is for models that are *not* landmarks; compare [approach 1](place-static-objects-on-a-planet-surface-1.md).

## Background: how the game does it

Nothing about a mod-drawn model is stored in a moving frame. The ground is **body-fixed** (`Ccf`): a point on the surface is `direction * (MeanRadius + GetTerrainHeightFromDirCcf(direction, accurate: true))` and does not change as the planet turns ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L795)). The renderer wants a camera-relative (ego) matrix, so each frame the anchor is rotated to the ecliptic-aligned frame with `GetCcf2Cce()` and translated by `camera.GetPositionEgo(body)` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236)). The model matrix reaches the GPU through the static-object renderer's per-viewport resource update, which is where the mod postfixes (`StaticObjectRenderer.UpdateRenderData(IViewport, int)`).

## Lifecycle and timing

| What | Where | Phase |
|---|---|---|
| Placement actions (terrain click, beside vessel, edits) | queued from the panel, drained in the submod `Update` (`StarMapBeforeGui`) after the vehicle and cloth workers are waited on | `frame:draw-ui` |
| Matrix per viewport | postfix on `StaticObjectRenderer.UpdateRenderData` | `frame:render` |
| Capture, reset, replay | postfixes on `GameSave.Populate` and `Universe.DeserializeSave` | `event:save`, `event:load` |

Waiting for the solvers matters because the colliders the placement owns are read by the vehicle and cloth workers (`JobSystems.VehicleSolver`, `ClothSolvers`); the global shape registry refuses mutation while a vehicle update is stepping. The body's rotation and camera-relative position are re-read every frame for every viewport, so placements follow the planet and work in secondary viewports whose camera has the same `NearbyCelestial`. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the anchor** as the exact body, a body-fixed position and a surface normal:

```csharp
// sphinx.lib/GroundPlacement.cs L8-L8
internal readonly record struct GroundAnchor(Celestial Body, double3 PositionCcf, double3 NormalCcf);
```

2. **Create it from a point** with `At` (surface point on the ground, normal from four neighbouring samples, see [measure slope](../measure-slope-and-detect-ground-contact-on-terrain/overview.md)), from the terrain under the cursor, or beside the controlled vessel. For the cursor, rotate the ego ray into the camera's `NearbyCelestial` `Ccf` frame, march 128 equal steps to the first point below ground and bisect 24 times:

```csharp
// sphinx.lib/GroundPlacement.cs L37-L62
public static bool TryCursor(double range, out GroundAnchor anchor)
{
    anchor = default;
    var camera = Program.GetMainCamera();
    if (camera?.NearbyCelestial is not { } body) return false;
    var ray = Cursor.GetEgoRay(Program.MainViewport);
    var rotation = body.GetCce2Ccf();
    var origin = (ray.Origin - camera.GetPositionEgo(body)).Transform(rotation);
    var direction = ray.Direction.Transform(rotation).NormalizeOrZero();
    if (!Finite(origin) || !Finite(direction) || direction.LengthSquared() < .5 || Depth(body, origin) <= 0) return false;
    double above = 0, below = double.NaN;
    for (int i = 1; i <= 128; i++)
    {
        double t = range * i / 128;
        if (Depth(body, origin + direction * t) <= 0) { below = t; break; }
        above = t;
    }
    if (!double.IsFinite(below)) return false;
    for (int i = 0; i < 24; i++)
    {
        double mid = (above + below) * .5;
        if (Depth(body, origin + direction * mid) <= 0) below = mid; else above = mid;
    }
    anchor = At(body, origin + direction * below);
    return true;
}
```

3. **Compose the frame each frame.** The anchor frame (up is the slope normal when alignment is on, else radial; X is local east projected onto the tangent plane; Z is `X x up`) is multiplied by `Ccf2Cce` and the camera-relative translation:

```csharp
// sphinx.lib/GroundPlacement.cs L64-L78
public static double4x4 Frame(GroundAnchor anchor, bool align, Camera camera)
{
    return FrameCcf(anchor, align) * double4x4.CreateFromQuaternion(anchor.Body.GetCcf2Cce())
        * double4x4.CreateTranslation(camera.GetPositionEgo(anchor.Body));
}

public static double4x4 FrameCcf(GroundAnchor anchor, bool align)
{
    var up = align ? anchor.NormalCcf : anchor.PositionCcf.NormalizeOrZero();
    var x = East(anchor.PositionCcf.NormalizeOrZero());
    x = (x - up * double3.Dot(x, up)).NormalizeOrZero();
    var z = double3.Cross(x, up).NormalizeOrZero();
    var p = anchor.PositionCcf;
    return new double4x4(x.X,x.Y,x.Z,0, up.X,up.Y,up.Z,0, z.X,z.Y,z.Z,0, p.X,p.Y,p.Z,1);
}
```

   East is `UnitZ x radial` with a `UnitY` fallback at the poles, so a heading (the Y rotation) is measured from local east.

4. **Ground and centre the model in its own space** with a pure function of the import bounds, so edits never accumulate (scale 0.01 to 1000 per axis, then rotations X, Y, Z in degrees; the lowest point goes to Y = 0). The entry's matrix is `GroundedLocal * Frame`, packed to float:

```csharp
// sphinx.lib/SphinxEntry.cs L21-L27
public float4x4 Matrix(Camera camera)
{
    var local = PlacementMath.GroundedLocal(Model.Min, Model.Max, Vector(Scale), Vector(Rotation), Vector(Offset));
    var matrix = new double4x4(local.M11,local.M12,local.M13,local.M14,local.M21,local.M22,local.M23,local.M24,
        local.M31,local.M32,local.M33,local.M34,local.M41,local.M42,local.M43,local.M44);
    return float4x4.Pack(matrix * GroundPlacement.Frame(Anchor, Align, camera));
}
```

5. **Prepare the matrix per viewport.** The postfix on `UpdateRenderData` writes each visible entry's matrix for viewports whose camera has the entry's body as `NearbyCelestial` and not in the editor:

```csharp
// sphinx.lib/SphinxSubmod.cs L85-L95
internal void Prepare(IViewport viewport, int frame)
{
    if (_disposed || Program.EditorFlag) return;
    var camera = viewport.GetCamera();
    foreach (var entry in _entries)
    {
        if (!entry.Visible || !ReferenceEquals(entry.Anchor.Body, camera.NearbyCelestial)) continue;
        try { entry.Model.Prepare(viewport, frame, entry.Matrix(camera)); }
        catch (Exception ex) { entry.Visible = false; _pending.Enqueue(() => SphinxPhysics.Detach(entry)); _status = $"#{entry.Id} hidden: {ex.Message}"; Console.WriteLine($"sphinx: {ex}"); }
    }
}
```

   Recording the draw calls is the rendering topics' business; see [draw mod meshes with the engine renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md).

6. **Save the anchor, not a matrix.** The scene-save participant stores body id, mesh id, body-fixed position and normal, scale, rotation, offset and flags; replay rebuilds each entry through the same resource and grounding paths:

```csharp
// sphinx.lib/SphinxSubmod.Saves.cs L14-L32
public sealed class StaticSave
{
    public string BodyId { get; set; } = "";
    public string MeshId { get; set; } = "";
    public string? Png { get; set; }
    public double3 PositionCcf, NormalCcf;
    public float3 Scale, Rotation, Offset;
    public Vector2 UvScale, UvOffset;
    public bool Visible, Align;
    public int Collision;
}
public IEnumerable<ISaveParticipant> SaveParticipants
{
    get { yield return new SaveParticipant<StaticSave[]>("sphinx", CaptureStatics, ResetStatics, RestoreStatics, 150, ValidateStatics); }
}
private StaticSave[] CaptureStatics() => _entries.Select(e => new StaticSave { BodyId = e.Anchor.Body.Id,
    MeshId = e.MeshId, Png = e.Png, PositionCcf = e.Anchor.PositionCcf, NormalCcf = e.Anchor.NormalCcf,
    Scale = e.Scale, Rotation = e.Rotation, Offset = e.Offset, UvScale = e.Mapping.Scale, UvOffset = e.Mapping.Offset,
    Visible = e.Visible, Align = e.Align, Collision = (int)e.Collision }).ToArray();
```

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/GroundPlacement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/GroundPlacement.cs) | Anchor, cursor ray march, `Ccf` and ego frames |
| [sphinx.lib/PlacementMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/PlacementMath.cs) | `GroundedLocal` from import bounds (tested with 200 random cases) |
| [sphinx.lib/SphinxEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxEntry.cs) | Placement record and matrix |
| [sphinx.lib/SphinxSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Saves.cs) | Scene-save participant |
| [sphinx.lib/SphinxPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPatches.cs) | The postfixes and their apply and remove |

## Variations and gotchas

- **Never store the matrix.** It depends on the camera and the planet phase; store the anchor.
- **Ambiguous or missing bodies on replay** are warned about and the record retained.
- **Statics keep rendering with the HUD hidden**, because the render hooks do not depend on `DrawUI`.
- **Shadows.** Meshes drawn this way receive shadows but never cast them; the cascaded shadow loop covers planet, SuperMesh, part-model and spline depth only.
- **Exceptions.** An exception in the per-entry matrix or draw hides that entry and detaches its collider rather than propagating into the render.
- **Colliders** for mod-drawn statics are the physics topics' business: [add static colliders to physics bubbles](../../../physics/colliders/add-static-colliders-to-physics-bubbles/overview.md).

## See also

- [Overview](overview.md); [approach 1](place-static-objects-on-a-planet-surface-1.md); [approach 3](place-static-objects-on-a-planet-surface-3.md)
- [Sphinx GLB statics placement](../../../../mods/unscience/features/sphinx-glb-statics-placement.md), [Sphinx static-object render hooks](../../../../mods/unscience/features/sphinx-static-object-render-hooks.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Choose a render frame and anchor effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
