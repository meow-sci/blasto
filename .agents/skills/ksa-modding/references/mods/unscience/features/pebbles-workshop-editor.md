---
type: Mod Feature
title: Floating collider workshop with a textured preview, CPU-projected gizmos and detached undo history
description: Pebbles edits a detached clutter object in a floating ImGui window that shows an offscreen preview image, overlays collider wireframes and gizmos projected with that image's own matrix, fits primitives to mesh bounds and keeps JSON-cloned undo history
mod: unscience
submod: pebbles
category: ui
techniques: [imgui-window, imgui-drawlist, json-config, starmap-lifecycle]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderFloatingWindows; window, canvas, gizmos, inspector, input)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; preview refresh and offscreen render)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replays ISubmod.Update while the HUD is hidden)
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImInputString
  - Brutal.ImGuiApi.ImTextureRef
  - KSA.Program
tags: [imgui, editor, gizmos, camera, picking, undo, colliders, floating-window, ground-clutter]
---

The Collider Workshop is the editor Pebbles opens from **Preview and set up colliders**: a resizable
floating window titled "Pebbles — Collider Workshop" with a lit, textured 3D view of the chosen mesh
on the left and an inspector on the right. The player orbits (right drag), pans (middle drag) and
zooms (wheel), adds box, sphere, capsule or cylinder colliders fitted to the mesh bounds, drags axis
handles to move, rotate or resize them, types exact values, duplicates, mirrors or deletes shapes,
grounds the mesh, snaps, and undoes or redoes every step. **Done** hands the edited recipe back to the
main form; neither editing nor Done touches a planet.[^pebbles-readme]

Mechanism in one line: the editor owns a detached `ObjectRecipe` and never touches the scene; the GPU
preview is rendered offscreen before the GUI phase and shown with `ImDrawListPtr.AddImage`, and every
collider outline, grid line and gizmo is projected on the CPU with the same view-projection matrix
that produced that image and drawn into the window's draw list on top of it.[^workshop-editor]

## How it works

**Hosting and the floating window.** `PebblesSubmod.RenderContent` opens the editor with a clone of
the form's replacement recipe and a completion callback, and disables the whole mesh/scale form
while it is open
([pebbles.lib/PebblesSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.Ui.cs#L23-L47)).
`PebblesSubmod.RenderFloatingWindows` rebinds the completion and calls `WorkshopEditor.Draw` every
frame; `PebblesSubmod.Update` calls `WorkshopEditor.Update`
([pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs#L20-L42)).
`RenderFloatingWindows` is the optional `ISubmod` member for stand-alone windows: the supermod host
calls it for every sub-mod after its own panel, even when the unscience window is closed or the
Pebbles section is collapsed.[^isubmod][^unscience-mod] `Draw` itself calls `ImGui.Begin` with the
stable id `###pebbles-workshop`, restores size and position from state, enforces a 560 x 450 minimum,
and lays out a two-column table (preview 2 : inspector 1) when at least 780 px are available, else
stacks them.

**Detached data model.** `WorkshopState` holds only authoring data (the `ObjectRecipe`, selection,
tool, snap steps, camera, window rectangle, collapsed sections). Every copy goes through
`RecipeCopy.Clone`, a `System.Text.Json` round trip with `MaxDepth = 64`, so nothing aliases the main
form or a live planet recipe. Vectors are stored as the `Vec3` record struct with properties because
`System.Numerics` vectors expose public fields, which the serializer skips by default
([pebbles.lib/Models/PebblesRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/PebblesRecipe.cs#L8-L16)).[^pebbles-recipe]

**Undo history** ([pebbles.lib/Workshop/WorkshopState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopState.cs#L51-L71)).
`WorkshopHistory` keeps up to 80 cloned snapshots; `Record` clears redo. Three granularities feed it:

| Edit | How it is recorded |
|---|---|
| Buttons (add, fit, delete, duplicate, mirror, ground, toggle colliders) | `Edit(action)` records the recipe before running the action |
| Numeric drags and text fields | `NumericChanged` remembers the first "before" snapshot; `FinishNumeric` records it once `ImGui.IsAnyItemActive()` is false, so one drag is one undo step |
| Gizmo drags | `BeginDrag` clones the recipe; releasing the mouse records it; Escape restores it (`CancelGesture`) |

**Camera** ([pebbles.lib/Workshop/WorkshopMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopMath.cs#L8-L31)).
`WorkshopView` is an orbit camera (target, yaw, pitch clamped to +-1.5 rad, distance, FOV). The
projection uses near = distance x 0.001 and far = distance x 1000, and flips `M22` because the preview
uses a positive-height Vulkan viewport, so the same matrix drives both the GPU image and the CPU
overlay. Wheel zoom is exponential (`exp(-wheel * 0.13)`), pan converts pixels to metres at the target
distance, and **Frame mesh** sets distance = bounding radius / sin(FOV / 2) x 1.3.[^workshop-canvas]

**Canvas** ([pebbles.lib/Workshop/WorkshopCanvas.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopCanvas.cs#L27-L54)).
An `InvisibleButton` covering the region captures left, right and middle clicks so the window does
not drag; then, under `PushClipRect`, the window draw list receives a background rectangle, the
preview image (`AddImage`), a power-of-ten grid sized from the camera distance, each visible
collider's wireframe and the selected collider's handles. When the preview is ready the overlay uses
`WorkshopPreview.ViewProjection`, the matrix of the image currently on screen, rather than the live
camera, so outlines never drift from the mesh while a re-render is pending.[^brutal-imgui][^brutal-imdrawlist-extensions]

**Wireframes** ([pebbles.lib/Workshop/WorkshopDrawing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopDrawing.cs#L29-L84)).
Boxes draw 12 edges; spheres three 48-segment circles; cylinders and capsules two rings plus four
side lines, capsules adding half-circle caps at `half height - radius`. Each 3D segment is clipped by
`WorkshopMath.Project`, which rejects points behind the camera or outside depth 0..1. Hull colliders
draw a cached wireframe of their source mesh (about 6,000 sampled edges at most) scaled by
`HullScale`; physics uses its convex envelope.[^workshop-drawing][^workshop-hulls]

**Gizmos and dragging** ([pebbles.lib/Workshop/WorkshopCanvas.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopCanvas.cs#L104-L177)).
Handles are 0.16 x camera distance long, along local axes (always for Resize) or world axes, and
the hovered axis is the one whose projected segment (or rotation ring) is closest to the mouse within
9 px. Then:

- **Move**: `AxisDrag` projects the mouse delta onto the handle's screen-space vector and converts
  pixels to metres with the handle's world length; the delta is snapped.
- **Rotate**: two mouse rays are intersected with the plane through the collider centre normal to the
  axis; the signed angle `atan2(axis . (from x to), from . to)` is snapped in degrees, composed onto
  the original rotation (`CreateFromAxisAngle(axis, angle) * original`) and converted back with
  `WorkshopMath.Euler`, which solves
  KSA's XYZ order (row-vector `Rx * Ry * Rz`). A screen-space angle is the fallback when the plane is
  edge-on.
- **Resize**: twice the drag delta changes the matching dimension (sphere uniformly, capsule and
  cylinder diameter or height, capsule height kept at least the diameter); on hulls it changes
  `HullScale` relative to the source mesh's extent.

**Picking.** A left click off a handle casts a ray through the inverse view-projection (near point and
a point at depth 0.99 to avoid far-plane rounding) and runs a slab test against each visible
collider's oriented half-extents in its local frame; the nearest hit is selected, a miss clears the
selection.

**Inspector** ([pebbles.lib/Workshop/WorkshopInspector.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopInspector.cs#L13-L144)).
Mesh scale goes through `ClutterAuthoring.SetScale`, which also scales every collider's offset,
dimensions and hull scale; **Ground mesh and colliders** shifts the object and all colliders up by
`-BoundsMin.Y` of the preview; **Use colliders** switches the policy between `Custom` and `None`.
Per collider: name, collision enabled, visible, centre, rotation, full box dimensions or diameter and
total height, a filterable hull source picker (`WorkshopAssets.AssetCombo`, frozen through
`ClutterAssets.ResolveSelection`), **Fit to mesh bounds**, **Duplicate**, **Delete** and **Mirror
copy**.[^workshop-inspector][^clutter-authoring][^workshop-assets]

**Fitting and mirroring** ([pebbles.lib/Workshop/WorkshopState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopState.cs#L73-L104)).
`Fit` uses the preview's transformed bounds: a box takes the AABB; a sphere's diameter is the AABB
diagonal; a capsule's diameter is the XZ diagonal and its height the AABB height plus that diameter,
which the tests prove contains every AABB corner; a cylinder takes the XZ diagonal and the height.
`Mirror` negates one position component and conjugates the rotation as `S * R * S`, which stays a
proper rotation for these symmetric primitives.[^workshop-state][^workshop-checks]

**Completion.** **Done** runs `RecipeValidation.Object` and passes a clone to the callback, which
replaces the form's replacement object; **Cancel** or closing the window discards the draft. The main
**Apply to planet** later copies the object into the checked clutter types (see
[authored clutter colliders](./pebbles-clutter-colliders.md)).[^recipe-validation]

## Lifecycle and timing

| Work | Call chain | Frame point (build 5554) |
|---|---|---|
| Preview refresh and offscreen render | `Mod.OnBeforeUi` (`[StarMapBeforeGui]`, prefix of `Program.OnDrawUiFrame`, `Program.cs` L2267) -> `UpdateSubmods` -> `PebblesSubmod.Update` -> `WorkshopEditor.Update` | `frame:draw-ui`, after `PrepareImGui` (L2261) and `OnFrameViewports` (L2263), before any GUI emission |
| Same, HUD hidden | `HiddenUiFrameHook` prefix on `Program.OnDrawUiConsole` (L2275) replays `UpdateSubmods` | Same phase; the window itself is not drawn |
| Window, canvas, gizmos, input, inspector | `Mod.OnAfterUi` (`[StarMapAfterGui]`, postfix of `Program.OnDrawUiViewports`, L2268) -> `RenderFloatingWindows` -> `WorkshopEditor.Draw` | `frame:draw-ui`, inside `NewFrame` ... `ImGui.Render` (L2286) |
| Preview image sampled | `ImGuiBackend.Vulkan.RenderDrawData` in `RenderGame` (L4950) or `RenderEditor` (L5074) | `render:imgui`, last pass of the frame's command buffer |

The split is deliberate. GPU work (texture uploads, scene buffers, re-rendering the target, freeing
the old image) happens only in `Update`, before this frame's ImGui list can reference the preview
texture; `Draw` only adds references to an image that is already complete. Releasing the preview is
likewise deferred: `Release()` sets a flag and the next `Update` disposes and recreates the
`WorkshopPreview`.[^workshop-editor][^workshop-preview][^ksa-program]

Consequences: the preview renders with the canvas size and camera from the previous frame's `Draw`,
so it trails input by one frame; the overlay uses the displayed image's matrix, so it trails
identically and stays aligned. Because nothing in the editor
touches scene, physics or solver state, no solver synchronization is needed for editing; only Apply
goes through the solver-handoff commit.

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Workshop/WorkshopEditor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopEditor.cs) | Window, toolbar, footer, `Update`/`Draw` split, edit and gesture bookkeeping |
| [pebbles.lib/Workshop/WorkshopState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopState.cs) | `WorkshopState`, `WorkshopHistory`, `WorkshopColliders.Fit`/`Mirror`/`HalfExtents`, validation |
| [pebbles.lib/Workshop/WorkshopMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopMath.cs) | Orbit camera, projection, rays, plane and box intersection, Euler conversion |
| [pebbles.lib/Workshop/WorkshopCanvas.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopCanvas.cs) | Canvas, camera input, picking, gizmo drags |
| [pebbles.lib/Workshop/WorkshopDrawing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopDrawing.cs) | Grid, collider wireframes, handles |
| [pebbles.lib/Workshop/WorkshopInspector.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopInspector.cs) | Numeric editing and collider commands |
| [pebbles.lib/Workshop/WorkshopHulls.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopHulls.cs) | Hull source wireframe cache and picking bounds |
| [pebbles.lib/Workshop/WorkshopAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopAssets.cs) | Filterable asset combo |
| [pebbles.lib/PebblesUi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesUi.cs) | Main-form ImGui helpers; only the filtered `Choice` combo (mesh and planet pickers) is used at this ref |
| [pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs#L20-L47) | `Update` and `RenderFloatingWindows` hosting |
| [ksa-abstractions.lib/ISubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ISubmod.cs#L27-L33) | Default `RenderFloatingWindows` contract |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L150-L201) | Host calls `Update` before and `RenderFloatingWindows` after the GUI |
| [pebbles.tests/WorkshopChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/WorkshopChecks.cs) | Projection, unprojection, drags, picking, fits, mirror, history, state round trip |

## Snippets

GPU work only in the pre-GUI update; validation on Done:

```csharp
// pebbles.lib/Workshop/WorkshopEditor.cs L55-L72
    public void Update()
    {
        if (_releaseRequested) { _preview.Dispose(); _preview = new(); _releaseRequested = false; _stale = true; }
        if (!IsOpen || _assets == null) return;
        try
        {
            if (_refreshRequested)
            {
                _refreshRequested = false;
                _preview.Refresh(_state.Object, _assets, _state.PreviewLod);
                RefreshHullSources();
                _stale = false;
                if (_frameAfterRefresh) { FrameMesh(); _frameAfterRefresh = false; }
            }
            if (!_stale) _preview.Render(_state.View, _width, _height);
        }
        catch (Exception ex) { _stale = true; _message = "Preview: " + ex.Message; Console.WriteLine("pebbles: " + _message); }
    }
```

One matrix for the GPU preview and the CPU overlay, plus unprojection for picking:

```csharp
// pebbles.lib/Workshop/WorkshopMath.cs L24-L57
    public static Matrix4x4 ViewProjection(WorkshopView view, int width, int height)
    {
        float near = Math.Max(.00001f, view.Distance * .001f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(view.FovRadians, .1f, 2.8f),
            Math.Max(1, width) / (float)Math.Max(1, height), near, Math.Max(near * 10, view.Distance * 1000));
        projection.M22 *= -1; // positive-height Vulkan viewport
        return Matrix4x4.CreateLookAt(view.Eye, view.Target, Vector3.UnitY) * projection;
    }

    public static bool Project(Vector3 point, Matrix4x4 matrix, Vector2 origin, Vector2 size, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), matrix);
        screen = default;
        if (clip.W <= .000001f || !float.IsFinite(clip.W)) return false;
        var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        screen = origin + new Vector2(ndc.X + 1, ndc.Y + 1) * size * .5f;
        return ndc.Z >= 0 && ndc.Z <= 1;
    }

    public static bool Ray(Vector2 screen, Matrix4x4 matrix, Vector2 origin, Vector2 size, out Vector3 start, out Vector3 direction)
    {
        start = direction = default;
        if (size.X <= 0 || size.Y <= 0 || !Matrix4x4.Invert(matrix, out var inverse)) return false;
        var ndc = (screen - origin) / size * 2 - Vector2.One;
        var a = Vector4.Transform(new Vector4(ndc, 0, 1), inverse);
        // Avoid far-plane rounding at large far/near ratios.
        var b = Vector4.Transform(new Vector4(ndc, .99f, 1), inverse);
        if (Math.Abs(a.W) < 1e-8f || Math.Abs(b.W) < 1e-8f) return false;
        start = new Vector3(a.X, a.Y, a.Z) / a.W;
        var delta = new Vector3(b.X, b.Y, b.Z) / b.W - start;
        if (delta.LengthSquared() < 1e-12f) return false;
        direction = Vector3.Normalize(delta);
        return true;
    }
```

Move and rotate drags:

```csharp
// pebbles.lib/Workshop/WorkshopCanvas.cs L129-L153
    private void Drag(Vector2 mouse)
    {
        if (Selected is not { } current || _dragCollider == null) return;
        var original = _dragCollider;
        float delta = WorkshopMath.AxisDrag(mouse - _dragMouse, _dragAxisScreen, _handleLength);
        if (_state.Tool == WorkshopTool.Move)
        {
            delta = Snap(delta, _state.MoveSnap);
            current.Position = Vec3.From(original.Position.Vector + _dragAxisWorld * delta);
        }
        else if (_state.Tool == WorkshopTool.Rotate)
        {
            float angle = WorkshopMath.RotationDrag(mouse, _dragMouse, _dragCenterScreen);
            if (WorkshopMath.Ray(_dragMouse, _matrix, _origin, _size, out var a, out var ad) &&
                WorkshopMath.Ray(mouse, _matrix, _origin, _size, out var b, out var bd) &&
                WorkshopMath.Plane(a, ad, original.Position.Vector, _dragAxisWorld, out var ap) &&
                WorkshopMath.Plane(b, bd, original.Position.Vector, _dragAxisWorld, out var bp))
            {
                var from = ap - original.Position.Vector; var to = bp - original.Position.Vector;
                angle = MathF.Atan2(Vector3.Dot(_dragAxisWorld, Vector3.Cross(from, to)), Vector3.Dot(from, to));
            }
            angle = Snap(angle * 180 / MathF.PI, _state.AngleSnap) * MathF.PI / 180;
            var rotation = Quaternion.CreateFromAxisAngle(_dragAxisWorld, angle) * WorkshopMath.Rotation(original.RotationDegrees.Vector);
            current.RotationDegrees = Vec3.From(WorkshopMath.Euler(rotation));
        }
```

Bounded detached history and bounds fitting:

```csharp
// pebbles.lib/Workshop/WorkshopState.cs L51-L83
/// <summary>Bounded detached history. A whole drag is committed as one edit.</summary>
public sealed class WorkshopHistory
{
    private readonly List<ObjectRecipe> _undo = new(), _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Record(ObjectRecipe previous)
    {
        _undo.Add(RecipeCopy.Clone(previous)); _redo.Clear();
        if (_undo.Count > 80) _undo.RemoveAt(0);
    }
    public ObjectRecipe Undo(ObjectRecipe current) => Transfer(_undo, _redo, current);
    public ObjectRecipe Redo(ObjectRecipe current) => Transfer(_redo, _undo, current);
    private static ObjectRecipe Transfer(List<ObjectRecipe> source, List<ObjectRecipe> destination, ObjectRecipe current)
    {
        if (source.Count == 0) return current;
        destination.Add(RecipeCopy.Clone(current));
        var next = source[^1]; source.RemoveAt(source.Count - 1); return RecipeCopy.Clone(next);
    }
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}

public static class WorkshopColliders
{
    public static ColliderRecipe Fit(ColliderKind kind, Vector3 minimum, Vector3 maximum)
    {
        var size = Vector3.Max(maximum - minimum, new Vector3(.001f));
        float diameter = Math.Max(size.X, size.Z);
        if (kind == ColliderKind.Sphere) size = new Vector3(size.Length());
        else if (kind == ColliderKind.Capsule) { diameter = MathF.Sqrt(size.X * size.X + size.Z * size.Z); size = new Vector3(diameter, size.Y + diameter, diameter); }
        else if (kind == ColliderKind.Cylinder) { diameter = MathF.Sqrt(size.X * size.X + size.Z * size.Z); size = new Vector3(diameter, size.Y, diameter); }
        return new() { Kind = kind, Name = kind.ToString(), Position = Vec3.From((minimum + maximum) * .5f), Dimensions = Vec3.From(size) };
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.OnFrame`, `PrepareImGui`, `OnDrawUiFrame`, `OnDrawUiViewports`, `OnDrawUiConsole`, `ImGui.Render` call | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2286) | Frame phases behind `Update` and `RenderFloatingWindows` |
| `ImGuiBackend.Vulkan.RenderDrawData` call in `RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4946-L4951) | Where the preview image is actually sampled |
| `ImGui.Begin(ImString, ref bool, ...)`, `InvisibleButton`, `GetWindowDrawList`, `IsAnyItemActive`, `GetIO().MouseWheel` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5584) | Window, input capture, draw list, undo coalescing |
| `ImDrawListExtensions.AddImage(ImDrawListPtr, ImTextureRef, ...)` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L611) | Shows the offscreen preview |

## Pitfalls and version notes

- **Orbiting re-renders.** Every camera or size change makes the next `Update` wait for the device and
  re-render the whole preview (see [offscreen preview](./pebbles-workshop-offscreen-preview.md)); the
  README warns large meshes may hitch.
- **One-frame latency is by design**; do not "fix" it by rendering from `Draw`, which would replace an
  image the current ImGui list may already reference.
- **Session-only state.** `WorkshopState` serializes cleanly (`WorkshopChecks` round-trips it and
  `WorkshopValidation` bounds camera and snap values), but nothing persists it at this ref: unapplied
  collider edits vanish with the session, as the README states.[^workshop-checks][^pebbles-readme]
- **Main form locked while open.** The mesh form is disabled and `ImportGlb` throws ("Finish or cancel
  collider setup before selecting another mesh") so the editor's recipe cannot be replaced underneath
  it.
- **No hull creation UI.** **Add fitted shape** lists only Box, Sphere, Capsule and Cylinder
  (`NewColliderKind` 0..3); the hull inspector, wireframe and hull-scale drag only serve hull entries
  already in a recipe.
- **Uniform scale only.** `SetScale` throws for older recipes with non-uniform scale; pick a new mesh.
- **Euler round trip.** `WorkshopMath.Euler` returns the principal solution; orientation (not the
  angle triple) is preserved, which the tests check with quaternion dot products.
- Managed checks cover the math, fitting, mirroring and history; ImGui interaction and the GPU image
  need in-game testing.

## See also

- [Workshop offscreen 3D preview](./pebbles-workshop-offscreen-preview.md)
- [Authored clutter colliders](./pebbles-clutter-colliders.md)
- [Per-celestial ground clutter replacement](./pebbles-ground-clutter-replacement.md)
- [ISubmod contract](./lib-isubmod-contract.md) and [hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Supermod host](./unscience-supermod-host.md)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Build a 3D orbit canvas with CPU-projected gizmos, ray picking and undo](../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/build-a-pan-zoom-canvas-in-imgui-2.md) - how-to article demonstrating this feature
- [Keep per-frame work running when the HUD is hidden: replay StarMap GUI work from a prefix on OnDrawUiConsole](../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/draw-ui-while-the-hud-is-hidden-3.md) - how-to article demonstrating this feature

[^pebbles-readme]: pebbles.lib/README.md
[^workshop-editor]: pebbles.lib/Workshop/WorkshopEditor.cs
[^isubmod]: ksa-abstractions.lib/ISubmod.cs
[^unscience-mod]: unscience/Mod.cs
[^pebbles-recipe]: pebbles.lib/Models/PebblesRecipe.cs
[^workshop-canvas]: pebbles.lib/Workshop/WorkshopCanvas.cs
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imdrawlist-extensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^workshop-drawing]: pebbles.lib/Workshop/WorkshopDrawing.cs
[^workshop-hulls]: pebbles.lib/Workshop/WorkshopHulls.cs
[^workshop-inspector]: pebbles.lib/Workshop/WorkshopInspector.cs
[^clutter-authoring]: pebbles.lib/Models/ClutterAuthoring.cs
[^workshop-assets]: pebbles.lib/Workshop/WorkshopAssets.cs
[^workshop-state]: pebbles.lib/Workshop/WorkshopState.cs
[^workshop-checks]: pebbles.tests/WorkshopChecks.cs
[^recipe-validation]: pebbles.lib/Models/RecipeValidation.cs
[^workshop-preview]: pebbles.lib/Preview/WorkshopPreview.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
