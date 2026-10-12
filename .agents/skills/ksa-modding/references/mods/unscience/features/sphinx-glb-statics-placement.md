---
type: Mod Feature
title: Body-fixed GLB statics placed by terrain click or beside a vessel
description: Sphinx anchors imported GLB models to a celestial's body-fixed frame from a ray-marched terrain pick or a vessel-relative point, grounds and centers their rotated bounds, applies live XYZ transforms through a queued edit loop and restores them from unscience scene saves
mod: unscience
submod: sphinx
category: celestials
techniques: [glb-model, imgui-window, imgui-drawlist, save-sidecar, starmap-lifecycle]
hooks:
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: queued placements and edits after solver waits, library rescan, body pruning)"
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent and RenderFloatingWindows: panel, armed terrain click)"
  - harmony-prefix KSA.Program.OnDrawUiConsole (shared HiddenUiFrameHook replays Update while the HUD is hidden)
  - harmony-postfix KSA.StaticObjectRenderer.UpdateRenderData (per-viewport placement matrix, see render hooks)
  - "harmony-postfix KSA.GameSave.Populate (shared NativeSaveHooks: capture)"
  - "harmony-prefix KSA.Universe.DeserializeSave (shared NativeSaveHooks: reset)"
  - "harmony-postfix KSA.Universe.DeserializeSave (shared NativeSaveHooks: replay)"
lifecycle: [frame:draw-ui, frame:render, event:save, event:load]
game_types:
  - KSA.Celestial
  - KSA.Camera
  - KSA.Cursor
  - KSA.Program
  - KSA.Vehicle
  - KSA.IViewport
  - KSA.JobSystems
  - KSA.Universe
tags: [statics, glb, terrain, placement, ccf, picking, transforms, persistence]
---

Sphinx lets a player drop any imported GLB (a building, a statue, a ramp) onto a planet or moon
surface and keep editing it: XYZ scale, rotation in degrees, local metre offsets and terrain-slope
alignment apply live, and each placement can be hidden, duplicated, snapped back to the ground or
removed. Placements stay fixed to the body as it rotates, are drawn through the game's own static
object shaders, can collide, and are restored by unscience scene saves.[^sphinx-readme]

Mechanism in one line: a placement is a `GroundAnchor` (exact `Celestial`, body-fixed CCF position and
surface normal); a pure `PlacementMath.GroundedLocal` matrix scales, rotates, centers and grounds the
model's import bounds in glTF Y-up space; and `GroundPlacement.Frame` turns the anchor into a
camera-relative matrix every frame, so nothing about the placement is ever stored in a moving frame.

## How it works

**Model and texture sources.** "Import GLB…" and "Import PNG…" copy files into the shared `glbs` and
`pngs` catalogs through the [shared file library](./lib-shared-file-library.md); the catalogs are
rescanned every two seconds from `Update`, so files imported by Pebbles show up too. Placement imports a
content-hashed snapshot with Pebbles' `ClutterAssets.ImportGlb` and uses the returned mesh id from then
on, so it reuses [Pebbles' bounded GLB importer](./pebbles-glb-import.md) and
[material conversion](./pebbles-glb-material-conversion.md) instead of a second parser.[^sphinx-submod][^clutter-assets]

**Anchors** ([sphinx.lib/GroundPlacement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/GroundPlacement.cs)):

| Step | Code | Detail |
|---|---|---|
| Surface point | `Surface` (L85-L90) | `MeanRadius + GetTerrainHeightFromDirCcf(dir, accurate: true)` along a CCF direction |
| Slope normal | `At` (L12-L26) | Four extra surface samples 1 m east, west, north and south; cross product of the two chords, flipped to point outward, radial fallback |
| Terrain click | `TryCursor` (L37-L62) | `Cursor.GetEgoRay(Program.MainViewport)` rotated into the camera's `NearbyCelestial` CCF frame; 128 equal steps up to the pick range find the first point below ground, then 24 bisections refine it |
| Beside vessel | `BesideControlled` (L28-L35) | Controlled vessel's position relative to its parent `Celestial`, rotated into CCF, moved east by the clearance, then re-grounded with `At` |
| Frame | `FrameCcf` (L70-L78) | Up = slope normal (aligned) or radial; X = local east projected onto the tangent plane; Z = X × up; origin = anchor |
| To render space | `Frame` (L64-L68) | `FrameCcf × Ccf2Cce × translation(camera.GetPositionEgo(body))` |

"East" is `UnitZ × radial` with a `UnitY` fallback at the poles, so the heading (Y rotation) is measured
from local east.[^ground-placement]

**Local transform** ([sphinx.lib/PlacementMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/PlacementMath.cs#L9-L29)).
Scale (0.01 to 1000 per axis), then rotations X, Y, Z in degrees; the eight corners of the original
import bounds are transformed, and a translation centers the result horizontally and puts its lowest
point at Y = 0 before the user offset is added. Every input and the result are checked for finite,
ordered values. Because it always starts from the import bounds, edits never accumulate; the managed
tests check 200 random scale and rotation cases for grounding, centering, axis lengths and
repeatability.[^placement-math][^sphinx-tests-program]

**Entries** ([sphinx.lib/SphinxEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxEntry.cs#L8-L35)).
`SphinxEntry` holds id, mesh id, optional PNG, anchor, visibility, slope alignment, scale, rotation,
offset, UV mapping, collider mode, the private render resource and the collider. `Matrix(camera)`
packs `GroundedLocal × Frame` into a float matrix for the render hooks.[^sphinx-entry]

**Queued actions.** Every world-changing action from the UI captures its values and enqueues a closure
on `_pending`; `Update` drains the queue after `WaitForPhysics()` and wraps each closure so an exception
becomes the status line:[^sphinx-submod][^sphinx-editing]

| Action | Behavior |
|---|---|
| Place | Checks render hooks ready, 32-entry limit, body still present; builds the render resource (8 million vertex total budget) and collider; validates the transform first |
| Transform edit | Queued on every widget change; validates `GroundedLocal`, builds the new collider, then swaps collider and values together; a failure keeps the previous state and shows Retry / "Use current settings" |
| Visibility | Toggles drawing and detaches the collider from every physics bubble when hidden |
| Duplicate | New resource from the same mesh, PNG and UV mapping, offset along X by the scaled width; inherits collider mode |
| Snap to ground | Converts the local offset into a CCF point with the entry's basis, re-anchors there with `At`, rebuilds the collider with zero offset |
| Remove / Remove all | Disposes resources (collider detach, GPU retire) before dropping ownership; "Remove all" also releases Sphinx's GLB import cache, never the shared files |

Entries whose `Celestial` is no longer returned by `CelestialProvider` are removed in `Update`, which
covers system changes.[^sphinx-submod]

**Session vs saved state** ([sphinx.lib/SphinxSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Saves.cs)).
In the shipped supermod, placements are a scene-save participant (`sphinx`, restore order 150). The
`StaticSave` DTO stores body id, mesh id (Pebbles' path plus SHA-256 identity), PNG name, CCF position
and normal, scale, rotation, offset, UV scale and offset, visibility, slope alignment and collider mode.
Reset clears pending actions, waits for the solvers and disposes everything; validation rejects more
than 32 entries, unknown collider modes, degenerate anchors or scales and invalid UV mappings; replay
rebuilds each entry through the same `StaticModelResources`, `GroundedLocal` and collider paths, and warns
(retaining the record) for an ambiguous or missing body or file. Queued, unapplied edits are not part of
the snapshot. The in-panel help text still says placements last for the session; that is accurate
only for the standalone development host, which installs no save hooks.[^sphinx-saves][^sphinx-lib-readme][^sphinx-ui]

## Lifecycle and timing

| What | Where it runs | Frame phase |
|---|---|---|
| Panel, widgets, armed click (`RenderContent`, `RenderFloatingWindows`) | StarMap `AfterGui` | `frame:draw-ui` |
| Queue drain, library rescan, body pruning (`Update`) | StarMap `BeforeGui` (and `HiddenUiFrameHook` with F2) | `frame:draw-ui` |
| Matrix per viewport (`Prepare`) | Postfix on `StaticObjectRenderer.UpdateRenderData` inside `Program.UpdateRenderingResources` | `frame:render` |
| Save capture / reset / replay | `NativeSaveHooks` | `event:save`, `event:load` (`frame:prepare`) |

The UI never touches world state directly because `Update` runs after `Program.PrepareFrame` has already
dispatched the next vehicle and cloth steps (`ExecuteNextClothSolvers` and `ExecuteNextVehicleSolvers`
at L2217-L2220).[^ksa-program] Those workers read collider shapes and Sphinx's per-bubble handle maps,
and KSA's global shape registry refuses mutation while a vehicle update is stepping
(`ConstraintSim.UnlockShapes` throws).[^ksa-constraint-sim] Sphinx therefore calls
`JobSystems.VehicleSolver.Wait()` and `ClothSolvers.Wait()` before draining the queue, which stalls the
main thread for the rest of the in-flight step, but only in frames that have pending actions.
Library rescans and placement math are main-thread only and need no wait.

The anchor is body-fixed, and the body rotation and the camera-relative body position are read again
for every viewport in every frame, so placements follow the rotating planet and work in secondary
viewports whose camera has the same `NearbyCelestial`. Picking happens in `RenderFloatingWindows`
(after the panel) only when ImGui does not want the mouse; Escape, entering the editor or losing the
system cancels it.[^sphinx-ui]

## Key code

| Path | Role |
|---|---|
| [sphinx.lib/GroundPlacement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/GroundPlacement.cs) | Anchors, slope, cursor ray march, CCF and ego frames |
| [sphinx.lib/PlacementMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/PlacementMath.cs) | Pure grounded local transform |
| [sphinx.lib/SphinxEntry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxEntry.cs) | Placement record and matrix |
| [sphinx.lib/SphinxSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.cs#L27-L71) | `Update` queue drain, `QueuePlacement`, `WaitForPhysics` |
| [sphinx.lib/SphinxSubmod.Editing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Editing.cs) | Selection, live transform edits, snap, duplicate, remove |
| [sphinx.lib/SphinxSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Ui.cs) | Panel, transform widgets, armed terrain click |
| [sphinx.lib/SphinxSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxSubmod.Saves.cs) | Scene-save participant |
| [sphinx.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.tests/Program.cs) | Managed placement-math checks |
| [scope/statics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/statics.md) | Authors' integration and acceptance notes |

## Snippets

Ray march, then bisection, against the height map (all in the body's CCF frame):

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

Slope from four one-metre samples:

```csharp
// sphinx.lib/GroundPlacement.cs L12-L26
    public static GroundAnchor At(Celestial body, double3 nearCcf)
    {
        var radial = nearCcf.NormalizeOrZero();
        if (!Finite(radial) || radial.LengthSquared() < .5) throw new InvalidOperationException("No valid surface direction.");
        double3 point = Surface(body, radial);
        var east = East(radial);
        var north = double3.Cross(radial, east).NormalizeOrZero();
        // One metre samples capture local terrain slope using the current accurate terrain chain.
        var a = Surface(body, (point + east).NormalizeOrZero()) - Surface(body, (point - east).NormalizeOrZero());
        var b = Surface(body, (point + north).NormalizeOrZero()) - Surface(body, (point - north).NormalizeOrZero());
        var normal = double3.Cross(a, b).NormalizeOrZero();
        if (double3.Dot(normal, radial) < 0) normal = -normal;
        if (!Finite(normal) || normal.LengthSquared() < .5) normal = radial;
        return new(body, point, normal);
    }
```

Body-fixed basis, then into the camera-relative frame of the viewport being drawn:

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

Grounding and centering from the original bounds:

```csharp
// sphinx.lib/PlacementMath.cs L9-L29
    public static Matrix4x4 GroundedLocal(Vector3 min, Vector3 max, Vector3 scale, Vector3 degrees, Vector3 offset)
    {
        if (!Finite(scale) || scale.X < .01f || scale.Y < .01f || scale.Z < .01f || scale.X > 1000 || scale.Y > 1000 || scale.Z > 1000)
            throw new ArgumentException("Scale axes must be between 0.01 and 1000.");
        if (!Finite(degrees) || !Finite(offset)) throw new ArgumentException("Offsets and rotation must be finite.");
        if (!Finite(min) || !Finite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new ArgumentException("Model bounds must be finite and ordered.");
        var r = degrees * (MathF.PI / 180);
        var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(r.X)
            * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z);
        var low = new Vector3(float.PositiveInfinity); var high = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var p = Vector3.Transform(new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), local);
            low = Vector3.Min(low, p); high = Vector3.Max(high, p);
        }
        var centering = new Vector3(-(low.X * .5f + high.X * .5f), -low.Y, -(low.Z * .5f + high.Z * .5f));
        if (!Finite(low) || !Finite(high) || !Finite(centering + offset))
            throw new ArgumentException("The model transform exceeds supported coordinates.");
        return local * Matrix4x4.CreateTranslation(centering + offset);
    }
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCcf(double3, bool)` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789) | Height-map surface radius (bicubic when `accurate`) |
| `Celestial.GetCcf2Cce()` / `GetCce2Ccf()`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532-L541) | Body-fixed to CCE rotation and back |
| `Cursor.GetEgoRay(IViewport)` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27-L30) | Mouse ray in ego space |
| `Camera.NearbyCelestial`, `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L71) | Picked body; camera-relative body and vessel positions |
| `Program.GetMainCamera`, `Program.MainViewport`, `Program.EditorFlag` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L225-L631) | Picking context; flight-scene only |
| `Program.PrepareFrame` solver dispatch | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2221) | Why `Update` must wait before mutating |
| `ConstraintSim.UnlockShapes` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L124-L133) | Refuses shape edits during a vehicle step |

## Pitfalls and version notes

- Height comes from `GetTerrainHeightFromDirCcf`, which samples the body's height map, not the streamed
  render mesh or the physics terrain patches; the README advises positive Y offsets for large models on
  rough ground.[^ksa-celestial][^sphinx-readme]
- The anchor stores a `Celestial` reference. A system change or reload yields new objects, so the
  pruning in `Update` removes stale entries; scene saves rebind by body id and refuse ambiguous ids.
- `WaitForPhysics` blocks the main thread on the in-flight vehicle step. Batch heavy edits (many
  duplicates, large mesh colliders) rather than issuing them every frame.
- Transform widgets queue an edit on every change, so a drag queues one rebuild per frame; each rebuild
  makes a new collider (a large mesh collider can hitch).
- Placement and transforms are rejected outside 0.01 to 1000 scale per axis or for non-finite values;
  colliders add a 20 km diagonal and 100 km offset limit (see [static colliders](./sphinx-static-colliders.md)).
- Placement is disabled in the vehicle editor, and nothing renders there.
- The authors list native acceptance (flat, sloped and polar terrain, misses and range, secondary
  viewports, pause and warp) as still required; nothing here was run in game.[^scope-statics]

## See also

- [Static colliders](./sphinx-static-colliders.md), [UV mapping and PNG texture overrides](./sphinx-uv-texture-overrides.md) and [render hooks](./sphinx-static-object-render-hooks.md)
- [Bounded GLB 2.0 import](./pebbles-glb-import.md) and [GLB material conversion](./pebbles-glb-material-conversion.md)
- [Shared file library](./lib-shared-file-library.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md) and [scene save sidecar](./unscience-scene-save-sidecar.md)
- [Keep per-frame work running while the HUD is hidden](./lib-hidden-ui-frame-hook.md)
- [Sphinx sub-mod](../submods/sphinx.md) and [mod overview](../overview.md)
- [Surface normal from four one-metre samples and a cross product](../../../howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/measure-slope-and-detect-ground-contact-on-terrain-2.md) - how-to article demonstrating this feature
- [GroundAnchor composed each frame into a camera-relative matrix](../../../howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-2.md) - how-to article demonstrating this feature
- [Per-bubble static handles from a BeginStaticObjectPass postfix](../../../howto/physics/colliders/add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-1.md) - how-to article demonstrating this feature

[^sphinx-readme]: sphinx/README.md
[^sphinx-submod]: sphinx.lib/SphinxSubmod.cs
[^clutter-assets]: pebbles.lib/Assets/ClutterAssets.cs
[^ground-placement]: sphinx.lib/GroundPlacement.cs
[^placement-math]: sphinx.lib/PlacementMath.cs
[^sphinx-tests-program]: sphinx.tests/Program.cs
[^sphinx-entry]: sphinx.lib/SphinxEntry.cs
[^sphinx-editing]: sphinx.lib/SphinxSubmod.Editing.cs
[^sphinx-saves]: sphinx.lib/SphinxSubmod.Saves.cs
[^sphinx-lib-readme]: sphinx.lib/README.md
[^sphinx-ui]: sphinx.lib/SphinxSubmod.Ui.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-constraint-sim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^scope-statics]: scope/statics.md
