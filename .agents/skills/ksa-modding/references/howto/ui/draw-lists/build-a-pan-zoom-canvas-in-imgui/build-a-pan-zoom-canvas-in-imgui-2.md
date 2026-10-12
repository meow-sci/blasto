---
type: How-To
title: Build a 3D orbit canvas with CPU-projected gizmos, ray picking and undo
description: Show an offscreen render in an ImGui window, project colliders and gizmo handles with the same view-projection matrix, pick with a ray, drag handles, and keep bounded JSON-clone undo history
category: ui
group: draw-lists
topic: build-a-pan-zoom-canvas-in-imgui
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/pebbles-workshop-editor]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImTextureRef
  - Brutal.ImGuiApi.ImInputString
  - KSA.Program
tags: [canvas, 3d, gizmo, picking, ray, undo, redo, offscreen, draw-list]
---
The Collider Workshop in unscience's Pebbles sub-mod is a floating editor window with a 3D view of a mesh on the left and an inspector on the right. The player orbits (right drag), pans (middle drag) and zooms (wheel), adds box, sphere, capsule or cylinder colliders fitted to the mesh bounds, drags axis handles to move, rotate or resize them, and undoes every step. The mesh is rendered offscreen before the GUI phase and shown with `AddImage`; every collider outline, grid line and gizmo is projected on the CPU with the same view-projection matrix and drawn in the window's draw list on top. This article is the canvas side: the shared matrix, hit-testing, picking, gizmo drags and undo. The offscreen render itself (`WorkshopPreview`) is a renderer subject; here it is treated as "an `ImTextureRef` that is ready or not". Demonstrated by [Floating collider workshop with a textured preview, CPU-projected gizmos and detached undo history](../../../../mods/unscience/features/pebbles-workshop-editor.md).

## Background: how the game does it

Three Brutal ImGui facilities do the work: `ImDrawListExtensions.AddImage` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L611)[^brutal-imguiapi-imdrawlistextensions]) draws an `ImTextureRef` into the draw list, `ImGui.InvisibleButton` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6255-L6262)[^brutal-imguiapi-imgui]) takes `ImGuiButtonFlags.MouseButtonLeft|Right|Middle` so every button is captured, and `ImGui.PushClipRect` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7907)[^brutal-imguiapi-imgui]) clips to the canvas. The game's own 3D-to-screen projection is `Camera.EclToScreen` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340)[^ksa-camera]); the workshop does not use it because it has a private orbit camera, and so has to build its own matrix. Because the preview is a Vulkan render with a positive-height viewport, the projection's `M22` is negated so the same matrix drives the GPU image and the CPU overlay.

## Lifecycle and timing

The work is split across two StarMap hooks in `frame:draw-ui`:

| Work | Call chain | Frame point |
|---|---|---|
| Preview refresh and offscreen render | `[StarMapBeforeGui]` (prefix of `Program.OnDrawUiFrame`) then `WorkshopEditor.Update` | After `PrepareImGui`, before any GUI emission |
| Window, canvas, gizmos, input, inspector | `[StarMapAfterGui]` (postfix of `Program.OnDrawUiViewports`) then `RenderFloatingWindows` then `WorkshopEditor.Draw` | Inside `NewFrame` to `ImGui.Render` |
| Preview image sampled | The ImGui render pass | `render:imgui` |

GPU work (texture uploads, scene buffers, re-rendering the target, freeing the old image) happens only in `Update`, before this frame's ImGui list can reference the texture; `Draw` only adds references to an image that is already complete. The preview therefore renders with the canvas size and camera of the previous frame's `Draw` and trails input by one frame; the overlay uses the displayed image's matrix, so it trails identically and stays aligned. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Host the editor as a floating window independent of any panel.** `RenderFloatingWindows` is an optional member of the host's sub-mod contract, called every frame even when the parent panel is closed; it rebinds the completion callback and calls `Draw`, while `Update` runs in the pre-GUI hook:

```csharp
// pebbles.lib/PebblesSubmod.cs L20-L23
public void Update(double dt)
{
    _controller.Update();
    _workshop.Update();
```

```csharp
// pebbles.lib/PebblesSubmod.cs L37-L42
public void RenderFloatingWindows()
{
    _workshop.SetCompletion(CompleteWorkshop);
    _workshop.Draw(_assets);
    _glbBrowser.Render(name => ImportAttempt(() => ImportGlb(GlbLibrary.Files.FullPath(name))));
}
```

2. **Begin a stable-id window with restored size.** The title uses a `###` id, minimum size is enforced, and position and size are written back to the (serializable) state every frame:

```csharp
// pebbles.lib/Workshop/WorkshopEditor.cs L74-L92
public void Draw(ClutterAssets assets)
{
    _assets = assets;
    if (!IsOpen) return;
    if (_restoreWindow)
    {
        ImGui.SetNextWindowSize(new float2(_state.Width, _state.Height), ImGuiCond.Always);
        if (_state.WindowX >= 0) ImGui.SetNextWindowPos(new float2(_state.WindowX, _state.WindowY), ImGuiCond.Always);
        _restoreWindow = false;
    }
    else ImGui.SetNextWindowSize(new float2(1000, 720), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSizeConstraints(new float2(560, 450), new float2(float.MaxValue, float.MaxValue));
    bool open = true;
    bool shown = ImGui.Begin("Pebbles — Collider Workshop###pebbles-workshop"u8, ref open);
    try
    {
        var windowSize = ImGui.GetWindowSize(); var windowPosition = ImGui.GetWindowPos();
        _state.Width = windowSize.X; _state.Height = windowSize.Y;
        _state.WindowX = windowPosition.X; _state.WindowY = windowPosition.Y;
```

3. **Keep GPU work in `Update`.** Rebuild or re-render only here, guarded by a try/catch that marks the preview stale rather than throwing into the game:

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

4. **One matrix for image and overlay.** Build a perspective projection from an orbit camera, flip `M22` for the Vulkan viewport, and write `Project` (with a depth and behind-camera rejection) and `Ray` (an unprojection that uses depth 0.99 to avoid far-plane rounding):

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

5. **Reserve, clip, draw.** An `InvisibleButton` covering the region captures all three mouse buttons so the window does not drag; under `PushClipRect` the draw list receives the background, the preview image, a power-of-ten grid sized from the camera distance, each visible collider's wireframe and the selected collider's handles. While the preview is ready the overlay uses the matrix of the image on screen, not the live camera:

```csharp
// pebbles.lib/Workshop/WorkshopCanvas.cs L27-L54
private void Canvas(float height)
{
    var pos = ImGui.GetCursorScreenPos();
    var extent = new float2(Math.Max(100, ImGui.GetContentRegionAvail().X), height);
    _width = (int)extent.X; _height = (int)extent.Y;
    _origin = new(pos.X, pos.Y); _size = new(extent.X, extent.Y);
    _matrix = !_stale && _preview.IsReady && _state.ShowMesh ? _preview.ViewProjection : WorkshopMath.ViewProjection(_state.View, _width, _height);
    ImGui.InvisibleButton("##workshop-canvas"u8, extent,
        ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
    bool hovered = ImGui.IsItemHovered();
    var draw = ImGui.GetWindowDrawList();
    ImGui.PushClipRect(pos, pos + extent, true);
    try
    {
    draw.AddRectFilled(pos, pos + extent, (ImColor8)0xff262321u);
    if (!_stale && _preview.IsReady && _state.ShowMesh) draw.AddImage(_preview.Texture, pos, pos + extent);
    DrawGrid(draw);
    if (_state.ShowColliders)
        foreach (var collider in _state.Object.Colliders)
            if (collider.Visible) DrawCollider(draw, collider);
    int hoveredAxis = -1;
    var mouse = ImGui.GetMousePos(); var mouseVector = new Vector2(mouse.X, mouse.Y);
    if (Selected is { Visible: true } selected && _state.ShowColliders)
        hoveredAxis = DrawHandles(draw, selected, mouseVector);
    HandleInput(hovered, mouseVector, hoveredAxis);
    }
    finally { ImGui.PopClipRect(); }
}
```

6. **Handle camera input yourself.** Right drag orbits (pitch clamped to about +-1.5 rad), middle drag pans (pixels converted to metres at the target distance), the wheel zooms exponentially, Escape cancels a gesture, and a left click either begins a handle drag or picks:

```csharp
// pebbles.lib/Workshop/WorkshopCanvas.cs L56-L102
private void HandleInput(bool hovered, Vector2 mouse, int hoveredAxis)
{
    if (ImGui.IsKeyPressed(ImGuiKey.Escape) && (_activeAxis >= 0 || _cameraDrag != 0)) { CancelGesture(); return; }
    if (hovered && _activeAxis < 0)
    {
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { _cameraDrag = 1; _lastMouse = mouse; }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) { _cameraDrag = 2; _lastMouse = mouse; }
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0) _state.View.Distance = Math.Clamp(_state.View.Distance * MathF.Exp(-wheel * .13f), .002f, 100000);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredAxis >= 0 && Selected is { } selected) BeginDrag(selected, hoveredAxis, mouse);
            else PickCollider(mouse);
        }
    }
    if (_cameraDrag != 0)
    {
        var button = _cameraDrag == 1 ? ImGuiMouseButton.Right : ImGuiMouseButton.Middle;
        if (!ImGui.IsMouseDown(button)) _cameraDrag = 0;
        else
        {
            var delta = mouse - _lastMouse; _lastMouse = mouse;
            if (_cameraDrag == 1)
            {
                _state.View.YawRadians -= delta.X * .009f;
                _state.View.PitchRadians = Math.Clamp(_state.View.PitchRadians + delta.Y * .009f, -1.5f, 1.5f);
            }
            else
            {
                var forward = Vector3.Normalize(_state.View.Target - _state.View.Eye);
                var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
                var up = Vector3.Cross(right, forward);
                float scale = _state.View.Distance * 2 * MathF.Tan(_state.View.FovRadians * .5f) / _size.Y;
                _state.View.Target += (-right * delta.X + up * delta.Y) * scale;
            }
        }
    }
    if (_activeAxis >= 0)
    {
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) Drag(mouse);
        else
        {
            if (_dragBefore != null) _history.Record(_dragBefore);
            _dragBefore = null; _activeAxis = -1;
        }
    }
}
```

7. **Pick with a ray and an oriented box test.** Unproject the cursor, slab-test each visible collider in its local frame, take the nearest hit; a miss clears the selection:

```csharp
// pebbles.lib/Workshop/WorkshopCanvas.cs L104-L117
private void PickCollider(Vector2 mouse)
{
    if (!WorkshopMath.Ray(mouse, _matrix, _origin, _size, out var start, out var direction)) return;
    string closest = ""; float distance = float.MaxValue;
    foreach (var collider in _state.Object.Colliders)
    {
        if (!collider.Visible) continue;
        var bounds = PickBounds(collider);
        if (WorkshopMath.RayBox(start, direction, bounds.Center,
            WorkshopMath.Rotation(collider.RotationDegrees.Vector), bounds.Half, out float d) && d < distance)
        { distance = d; closest = collider.Id; }
    }
    _state.SelectedColliderId = closest;
}
```

8. **Draw wireframes by projecting segments.** `Line` projects both endpoints and skips segments with an endpoint behind the camera; circles are 48-segment polylines:

```csharp
// pebbles.lib/Workshop/WorkshopDrawing.cs L10-L27
private void Line(ImDrawListPtr draw, Vector3 a, Vector3 b, ImColor8 color, float thickness = 1)
{
    if (Project(a, out var pa) && Project(b, out var pb)) draw.AddLine(Pixel(pa), Pixel(pb), color, thickness);
}

private void DrawGrid(ImDrawListPtr draw)
{
    float spacing = MathF.Pow(10, MathF.Floor(MathF.Log10(Math.Max(.001f, _state.View.Distance * .1f))));
    var center = _state.View.Target;
    center.X = MathF.Round(center.X / spacing) * spacing;
    center.Z = MathF.Round(center.Z / spacing) * spacing; center.Y = 0;
    for (int i = -10; i <= 10; i++)
    {
        var color = (ImColor8)0x404c4c4cu;
        Line(draw, center + new Vector3(i * spacing, 0, -10 * spacing), center + new Vector3(i * spacing, 0, 10 * spacing), color);
        Line(draw, center + new Vector3(-10 * spacing, 0, i * spacing), center + new Vector3(10 * spacing, 0, i * spacing), color);
    }
}
```

9. **Drag handles.** Move projects the mouse delta onto the handle's screen-space vector and converts pixels to metres with the handle's world length. Rotate intersects two mouse rays with the plane through the collider centre and takes the signed angle between them (with a screen-space fallback when the plane is edge-on):

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

## Sub-step: undo and redo with JSON-cloned snapshots and coalesced drags

The editor owns a detached data model, so undo is a stack of whole-object clones. Cloning goes through a `System.Text.Json` round trip (`RecipeCopy.Clone`), which makes snapshots simple and alias-free at the cost of snapshot size; the history is bounded at 80 entries and any new edit clears redo:

```csharp
// pebbles.lib/Workshop/WorkshopState.cs L51-L71
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
```

Three granularities feed it, so that one gesture is one undo step:

| Edit | How it is recorded |
|---|---|
| Buttons (add, fit, delete, duplicate, mirror, ground) | `Edit(action)` records the object before running the action |
| Numeric drags and text fields | `NumericChanged` remembers the first "before" snapshot; `FinishNumeric` records it once `ImGui.IsAnyItemActive()` is false |
| Gizmo drags | `BeginDrag` clones the object; releasing the mouse records it; Escape restores it (`CancelGesture`) |

```csharp
// pebbles.lib/Workshop/WorkshopEditor.cs L167-L185
private void Edit(Action action, bool refresh = false)
{
    CancelGesture(); _history.Record(_state.Object); action();
    if (refresh) { _refreshRequested = true; _stale = true; }
}
private void NumericChanged(ObjectRecipe before, bool refresh)
{
    _numericBefore ??= before;
    if (refresh) { _refreshRequested = true; _stale = true; }
}
private void FinishNumeric()
{
    if (_numericBefore != null && !ImGui.IsAnyItemActive()) { _history.Record(_numericBefore); _numericBefore = null; }
}
public void CancelGesture()
{
    if (_dragBefore != null) _state.Object = _dragBefore;
    _dragBefore = null; _activeAxis = -1; _cameraDrag = 0;
}
```

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Workshop/WorkshopEditor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopEditor.cs) | Window, toolbar, `Update`/`Draw` split, edit and gesture bookkeeping |
| [pebbles.lib/Workshop/WorkshopCanvas.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopCanvas.cs) | Canvas, camera input, picking, gizmo drags |
| [pebbles.lib/Workshop/WorkshopMath.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopMath.cs) | Orbit camera, projection, rays, intersection |
| [pebbles.lib/Workshop/WorkshopDrawing.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopDrawing.cs) | Grid, wireframes, handles |
| [pebbles.lib/Workshop/WorkshopState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Workshop/WorkshopState.cs) | State, history, fit and mirror |
| [pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs#L20-L47) | `Update` and `RenderFloatingWindows` hosting |

## Variations and gotchas

- **Snapshot size vs. diffs.** Whole-object JSON clones are simple and safe against aliasing, but heavy for large models; keep the history bounded (80 here).
- **Vectors and `System.Text.Json`.** `System.Numerics` vectors expose public fields, which the serializer skips by default, so the mod stores vectors as a record struct with properties.
- **Image and overlay must share a matrix.** Draw the overlay with the matrix of the image currently shown, or outlines drift from the mesh while a re-render is pending.
- **Vulkan viewport flip.** Forgetting the `M22` flip draws the overlay upside down relative to the image.
- **No solver sync needed.** Nothing here touches scene, physics or solver state; only applying the result back to the game does, and that goes through its own commit.
- **Version.** Brutal ImGui calls plus a private Vulkan preview; the preview is the part tied to engine internals.

## See also

- [Overview of canvases](overview.md)
- [Approach 1: 2D pan and zoom canvas](build-a-pan-zoom-canvas-in-imgui-1.md)
- [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../../howto/ui/input/cursor-to-world-ray/overview.md)
- [Place pieces on a grid with a snapping ghost, top-down camera and world gizmos](../../../../howto/ui/input/place-pieces-on-a-grid-with-a-ghost/overview.md)
- Mod feature: [Floating collider workshop with a textured preview, CPU-projected gizmos and detached undo history](../../../../mods/unscience/features/pebbles-workshop-editor.md)

[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
