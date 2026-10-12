---
type: How-To
title: "Place pieces on a grid: ray-plane cell pick, port-snapping ghost, top-down camera and per-viewport meshes"
description: Build a placement tool where the cursor ray picks a grid cell on a plane, a ghost snaps to open ports, a borrowed top-down camera is handed back, and meshes and gizmo lines are drawn into the world
category: ui
group: input
topic: place-pieces-on-a-grid-with-a-ghost
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/minigolf-course-builder]
techniques: [imgui-drawlist, reflection, harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapAfterGui
  - harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets
  - reflection KSA.GameViewport.<FixedController>k__BackingField
lifecycle: [frame:input, frame:viewports, frame:draw-ui, frame:render]
game_types:
  - KSA.Controller
  - KSA.FixedController
  - KSA.GameViewport
  - KSA.GizmosRenderer
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.SuperMeshRenderSystem
  - KSA.StaticMeshRenderable
  - KSA.Camera
tags: [placement, grid, ghost, snapping, gizmos, camera, builder, tool]
---
KSAGolf has a minigolf course builder: press B with a kitten on the ground and the main view turns straight down
over a grid laid at the kitten, the cell under the cursor is outlined, a ghost of the chosen piece follows it and snaps
onto the nearest open connection port (green when it connects, amber when it fits but touches no lane, red when blocked),
and a click lays it. Place, move, remove, undo, redo, save and load follow from there. This article covers the
reusable parts of any such tool: a body-fixed local frame, a cursor ray intersected with a plane to get a cell, a snapping
ghost, a camera view you borrow and hand back, input you take from the game, and drawing both gizmo lines and real meshes
into the world. Demonstrated by KSAGolf.

## Background: how the game does it

- **Cursor ray.** `Camera.ScreenToEgoRay` and a per-viewport conversion give a ray (see
  [cursor to world ray](../cursor-to-world-ray/overview.md)); the tool lifts it into its own local frame.
- **Fixed camera.** `CameraMode.Fixed` uses `FixedController`: while the camera follows something, each frame it puts the camera at
  `following.GetPositionEcl() + CameraOffset` looking along `CameraRotation`, with up derived from the followed object's reference
  frame. `CameraRotation` must be non-zero before the mode is set (the controller crosses it with the frame's up).
  [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L5-L45)[^ksa-fixedcontroller] `GameViewport.FixedController` has a protected setter and `IGameViewport.FixedController` is
  get-only.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42)[^ksa-gameviewport]
- **Mesh draw.** `StaticMeshRenderable.Draw(view)` records into the view's mesh buckets; `SuperMeshRenderSystem.ClearBuckets(viewport)`
  empties them right before the real fill for each rendered viewport (secondary views first, the main view last), so a draw
  submitted earlier in the frame is wiped.[current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L554)[^ksa-supermeshrendersystem][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4482)[^ksa-program]
- **Gizmos.** `Program.GizmosRenderer.DrawLine(startEgo, endEgo, color)` takes camera-relative positions. `ResetInstances()` runs
  at the top of `OnFrame`'s UI section, before the GUI hooks; `GizmosRenderer.Render` is called from `RenderGame` for the
  main view and `RenderEditor`, not from the secondary-viewport pass.[current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L217-L234)[^ksa-gizmosrenderer][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260)[^ksa-program][current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4842)[^ksa-program]
- **Input chain.** `Program` subscribes `OnKey` to the main window itself and the ImGui backend subscribes separately and feeds
  ImGui; the backend forwards to `Program.OnKey` only for secondary platform windows while `ImGuiBackend.InputFallthrough` is set.
  In `Program.OnKey` the active controller is asked before the controlled vehicle. See
  [share input with the game](../coordinate-imgui-focus-with-game-input/overview.md).

## Lifecycle and timing

| Work | Hook | Phase |
|---|---|---|
| B toggle, keys, wheel, drags, clicks, ghost | `[StarMapAfterGui]`, after the panel draws, only while a craft is flown | `frame:draw-ui` |
| Point the view | The same GUI hook calls `Step()` | `frame:draw-ui` |
| View re-asked in phase | `LevelHorizonController.OnFrame`, from `GameViewport.OnFrame` in `Program.OnFrameViewports` | `frame:viewports` |
| Swallow keys and clicks | Controller overrides, called from `Program.OnKey`/`OnMouseButton`/`OnScroll` during `Glfw.PollEvents` | `frame:input` |
| Draw meshes and gizmo lines | Harmony postfix on `SuperMeshRenderSystem.ClearBuckets`, once per rendered viewport | `frame:render` |

- Input is read after the panel (`_ui.Draw()` first in `OnAfterGui`) so a click on a window is not also a click on the world, and while
  ImGui's frame is open, which is why it lives in the GUI hook and not in a step that may run from the frame postfix.
- All three StarMap hooks run after the viewport pass has built the frame's matrices. A camera aimed from a GUI hook is consumed a frame
  late, so the controller re-asks an `IViewPose` source inside `OnFrame`, in phase with the matrices. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Enter and anchor a body-fixed frame.** Require a kitten on a celestial, remember the player's view *before* any write (setting
   Fixed clears the follow), and clear held input. The frame is body-fixed: up is the kitten's feet direction, the grid's +J is its facing
   projected onto the horizontal and +I is `J x up`:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L94-L112
public void Enter()
{
    if (Active) return;
    if (golf.Kitten is not { } kitten || kitten.Parent is not Celestial body)
    {
        Status = "fly a kitten on the ground to build a course";
        return;
    }

    golf.Suspended = true;
    if (!ReferenceEquals(body, _body) || !Near(kitten)) AnchorAt(kitten, body);

    kitten.ClearHeldPlayerInput();
    _saved = KsaWorld.RememberMainView();
    _borrowed = true;
    Active = true;
    Refresh();
    Log.Info("course builder: on");
}
```
```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L141-L157
private void AnchorAt(KittenEva kitten, Celestial body)
{
    double3 feet = ToCcf(body, KittenFrame.ToEcl(kitten, Vec.Zero));
    double3 up = Vec.Unit(feet);
    double3 facing = Vec.RejectFrom(double3.Transform(KittenFrame.DirectionToEcl(kitten, new double3(0, 0, 1)), body.GetCce2Ccf()), up);
    if (!(Vec.Len2(facing) > 1e-12)) facing = Vec.AnyPerpendicular(up);

    double3 j = Vec.Unit(facing);
    double3 i = Vec.Unit(Vec.Cross(j, up));

    // The first cell a metre ahead, centred on the kitten's line.
    Frame(body, feet + (j * 1.0) - (i * (CourseSize.CellM / 2.0)), i, j);
    _edits = new CourseEdits(Course.Empty);
    _view.Centre = new double2(0.5, 3.0);
    Name = "";
    Changed();
}
```

2. **Point the main view straight down.** Eye above the view centre, forward along down, screen-up along the course axis, passed with
   `this` as the pose source:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L173-L209
public void Step()
{
    if (!Active) return;

    if (golf.Kitten is null)
    {
        Leave();
        return;
    }

    if (TryLook(out double3 eyeEcl, out double3 forward, out double3 up) && golf.Kitten is { } kitten)
    {
        KsaWorld.TryLookFromMainViewport(eyeEcl - KsaWorld.PositionEcl(kitten), forward, up, FovDeg, this);
    }
}

public bool TryPose(double3 followedEcl, out double3 offsetFromFollowed, out double3 forwardEcl, out double3 upEcl, out double fovDeg)
{
    fovDeg = FovDeg;
    offsetFromFollowed = default;
    if (!TryLook(out double3 eye, out forwardEcl, out upEcl)) return false;

    offsetFromFollowed = eye - followedEcl;
    return Vec.IsFinite(offsetFromFollowed);
}

// Straight down over the view's centre, with the screen's up along the course axis the view is turned to.
private bool TryLook(out double3 eyeEcl, out double3 forwardEcl, out double3 upEcl)
{
    eyeEcl = forwardEcl = upEcl = default;
    if (_body is not { } body) return false;

    eyeEcl = LocalToEcl(body, new double3(_view.Centre.X, _view.Centre.Y, _view.HeightM));
    forwardEcl = -DirectionToEcl(body, new double3(0, 0, 1));
    upEcl = DirectionToEcl(body, new double3(_view.ScreenUp.X, _view.ScreenUp.Y, 0));
    return Vec.IsFinite(eyeEcl) && Vec.IsFinite(upEcl);
}
```

3. **Install a Fixed controller you control** (only if you need an up vector or an in-phase pose). `FixedController` is public and
   unsealed with a virtual `OnFrame`, so subclassing is ordinary; installing it means writing the compiler backing field because the setter is
   protected (`"<FixedController>k__BackingField"`, looked up once with `GetField(..., NonPublic | Instance)`). The field name is the one thing an API-surface check cannot cover, so warn when it moves and keep the stock controller:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1734-L1766
private static FixedController? LevelTheHorizon(IGameViewport viewport)
{
    if (viewport.FixedController is LevelHorizonController already) return already;

    try
    {
        if (viewport.BaseCamera is not { } camera) return viewport.FixedController;
        if (FixedControllerField(viewport) is not { } field) return viewport.FixedController;

        var level = new LevelHorizonController(camera);

        _stockFixedController ??= viewport.FixedController;
        field.SetValue(viewport, level);

        // The write is the whole point, and a silent no-op would read exactly like a working
        // swap: everything downstream still finds *a* controller.
        if (!ReferenceEquals(viewport.FixedController, level))
        {
            Log.Warn("camera: the FixedController write did not take - keeping KSA's own");
            _stockFixedController = null;
            return viewport.FixedController;
        }

        Log.Info("camera: levelling the horizon on the main view");

        return level;
    }
    catch (Exception e)
    {
        Log.Warn($"could not level the horizon, using KSA's own controller: {e.Message}");
        return viewport.FixedController;
    }
}
```

   The subclass asks the pose source again inside the engine's own frame loop and sets the camera exactly as the stock controller does, with
   the up substituted:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L265-L292
private void AskThePoseSource()
{
    if (Pose is not { } source) return;
    if (Camera.Following is not { } following) return;

    try
    {
        if (!source.TryPose(following.GetPositionEcl(), out double3 offset,
                            out double3 forward, out double3 up, out double fovDeg))
        {
            return;
        }

        if (!Vec.IsFinite(offset) || Vec.Len2(forward) < 0.5) return;

        CameraOffset = offset;
        CameraRotation = forward;
        UpEcl = up;

        // Clamped rather than refused. The engine's own setter does not clamp and the
        // projection throws outside (0, pi) -- from in here, which is the engine's loop.
        KsaWorld.TrySetMainViewFov(fovDeg);
    }
    catch
    {
        // Keep whatever was written a frame ago rather than dropping the view.
    }
}
```

4. **Pick the cell under the cursor.** Transform the cursor ray into course-local coordinates, intersect it with the plane at the current level's
   height, floor to a cell. The ray-plane maths is a plain function so it can be unit tested:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L277-L288
private void AimFromCursor()
{
    if (_aimed is { } aimed)
    {
        _cursor = aimed;
        return;
    }

    if (_body is not { } body || !KsaWorld.TryCursorRayEcl(out double3 origin, out double3 direction)) return;

    double3 o = EclToLocal(body, origin), d = DirectionToLocal(body, direction);
    if (BuildView.OnPlane(o, d, _level * CourseSize.LevelM) is { } hit) _cursor = BuildView.CellAt(hit);
```
```csharp
// src/KSAGolf/Sim/CourseBuild.cs L44-L52
public static double2? OnPlane(double3 origin, double3 direction, double heightM)
{
    if (Math.Abs(direction.Z) < 1e-9) return null;

    double t = (heightM - origin.Z) / direction.Z;
    return t > 0.0 ? new double2(origin.X + (direction.X * t), origin.Y + (direction.Y * t)) : null;
}

public static Cell CellAt(double2 metres) => new((int)Math.Floor(metres.X / CourseSize.CellM), (int)Math.Floor(metres.Y / CourseSize.CellM));
```

5. **Build the ghost.** Try to snap to the nearest open port within two cells in the requested orientation (or the next that fits); otherwise hold the
   piece by its first port's tile at the cursor. `Fit` colours it, and only `Connects` and `Open` are placeable:

```csharp
// src/KSAGolf/Sim/CourseBuild.cs L64-L70
public static Ghost At(Course course, PieceDef def, Orientation orientation, int level, Cell cursor)
{
    if (course.Snap(def, orientation, cursor) is { } snapped) return new Ghost(snapped, course.Judge(snapped), true);

    var free = new Placed(def, cursor - orientation.Apply(def.Ports[0].Cell, def.W, def.H), orientation, level);
    return new Ghost(free, course.Judge(free), false);
}
```

6. **Read input through ImGui, gated.** Keys only when ImGui does not want text input, mouse only when it does not want the mouse; wheel zooms, a
   right or middle drag pans, a left click lays the ghost (or, with nothing selected, picks the piece up to move it):

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L214-L226,L252-L270
public void Input(double dtPlayer)
{
    if (ImGui.IsKeyPressed(ImGuiKey.B, repeat: false) && !ImGui.GetIO().WantTextInput)
    {
        Toggle();
        return;
    }

    if (!Active || _body is null) return;

    ImGuiIOPtr io = ImGui.GetIO();
    bool keys = !io.WantTextInput;
    bool mouse = !io.WantCaptureMouse;
// ...
    if (mouse)
    {
        if (io.MouseWheel != 0f) _view.Zoom(io.MouseWheel);

        // Either button dragged pans, a screen's worth of drag moving the ground a screen's worth.
        if (ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            float2 d = io.MouseDelta;
            double perPixel = 2.0 * Math.Tan(double.DegreesToRadians(FovDeg) / 2.0) / Math.Max(1f, io.DisplaySize.Y);
            _view.Pan(-d.X * perPixel, d.Y * perPixel);
        }

        if (io.MouseDelta.X != 0f || io.MouseDelta.Y != 0f) _aimed = null;
        AimFromCursor();

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left, repeat: false)) Click();
    }

    Refresh();
```

7. **Take the keyboard and mouse from the game** while the tool is active, so the kitten does not walk off and a click does not open a part window. The
   controller subclass swallows key presses (not releases), clicks and scroll, and `IsMouseDrag()` returns true, which makes `Vehicle.OnMouseButton` ignore
   the right-button release that would open a part's window. Escape and the function keys still reach the game:

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L82-L103
// A view that takes the input is read through ImGui, which sees every key and button before the engine
// does, so swallowing here costs it nothing and keeps the kitten from walking off.
private bool TakesInput => Pose?.TakesInput == true;

public override bool OnKey(GlfwKeyEvent keyEvent)
{
    if (TakesInput && !keyEvent.IsMouse && keyEvent.Action != GlfwKeyAction.Release && Swallowed(keyEvent.Key)) return true;

    return base.OnKey(keyEvent);
}

// Letters, digits, the arrows and the editing keys: what walks a kitten and what the builder uses.
// Escape and the function keys still reach the game.
private static bool Swallowed(GlfwKey key)
    => key is >= GlfwKey.A and <= GlfwKey.Z or >= GlfwKey.Number0 and <= GlfwKey.Number9 or GlfwKey.Space
           or >= GlfwKey.Insert and <= GlfwKey.PageDown;

public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action,
                                   GlfwModifier mods)
{
    if (TakesInput) return true;
    if (Swinging is not null && button == GlfwMouseButton.Number1) return true;
```
```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L140-L142
// Load-bearing: the engine asks the active controller this before a right-release opens the
// window of whatever part is under the cursor, so a drag ending over a craft opens nothing.
public override bool IsMouseDrag() => TakesInput || Orbit?.Dragging == true || base.IsMouseDrag();
```

   The generic version of this pattern is [approach 5 of the input-sharing topic](../coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-5.md).

8. **Undo, redo, save, load.** Every edit goes through `CourseEdits`; `Changed()` rebuilds the outline lines, re-checks the hole, relevels, refreshes the ghost and
   publishes the result to the rest of the mod:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L382-L404
private void Changed()
{
    _courseLines = CourseLines.Of(Course);
    Report = Course.Check();
    Relevel();
    Refresh();
    Publish();
}

// Hands the laid course to the ball and to the physics a kitten stands on, and takes it back when there is none.
private void Publish()
{
    if (_body is not { } body || Course.Pieces.Count == 0)
    {
        golf.Course = null;
        CourseCollider.Want(null, []);
        return;
    }

    golf.Course = (body, new CourseSurface(Course, LocalToCcf(Vec.Zero), _iCcf, _jCcf, _upCcf, new Golf.BodyGround(body), GolfBall.RadiusM));
    CourseCollider.Want(body, CourseColliders.Of(Course).Select(b => CourseCollider.Place(LocalToCcf(b.Centre), LocalDirectionToCcf(b.X),
                                                                                            LocalDirectionToCcf(b.Y), LocalDirectionToCcf(b.Z), b.Size)));
}
```

9. **Leave and hand the view back** on every exit path; a viewport left in Fixed mode cannot be left by the camera-mode key (`NextCameraMode` returns false for Fixed):

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L114-L124
public void Leave()
{
    if (!Active) return;

    Active = false;
    if (_borrowed) KsaWorld.TryHandBackMainView(_saved, golf.Kitten, out _, out _);
    _borrowed = false;
    golf.Suspended = false;
    _ghost = null;
    Log.Info("course builder: off");
}
```

## Draw gizmos and meshes in the world

Two different channels, with different viewport rules:

- **Gizmo lines go to the main viewport only.** They are camera-relative, submitted per frame after `ResetInstances` and drawn by `RenderGame` for the main
  view. Skip every other viewport and skip a course farther than 2 km from the camera:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L535-L556
public void Draw(IViewport viewport)
{
    if (_body is not { } body) return;
    if (!Active && Course.Pieces.Count == 0) return;

    double3 camera = viewport.GetCamera().PositionEcl;
    double3 origin = LocalToEcl(body, Vec.Zero) - camera;
    if (!Vec.IsFinite(origin) || Vec.Len(origin) > 2000.0) return;

    var axes = (I: DirectionToEcl(body, new double3(1, 0, 0)), J: DirectionToEcl(body, new double3(0, 1, 0)), Up: DirectionToEcl(body, new double3(0, 0, 1)));
    DrawMeshes(viewport, origin, axes.I, axes.J, axes.Up);
    if (!ReferenceEquals(viewport, Program.MainViewport)) return;

    GizmosRenderer gizmos = Program.GizmosRenderer;
    void Line(double3 a, double3 b, float4 colour)
        => gizmos.DrawLine(origin + (axes.I * a.X) + (axes.J * a.Y) + (axes.Up * a.Z), origin + (axes.I * b.X) + (axes.J * b.Y) + (axes.Up * b.Z), colour);

    foreach (CourseLine line in _courseLines)
    {
        bool meshed = line.Piece >= 0 && line.Piece < Course.Pieces.Count && HasModel(Course.Pieces[line.Piece]);
        if (!meshed || line.Kind == LineKind.Faulty || (Active && line.Kind == LineKind.Opening)) Line(line.From, line.To, ColourOf(line.Kind));
    }
```

- **Meshes go into every viewport**, from a postfix on the public `SuperMeshRenderSystem.ClearBuckets(IViewport)`. The postfix runs right after the clear and
  before the kittens are drawn into the view; it calls one delegate per rendered viewport and, on the first exception, nulls the delegate and logs once so a broken
  draw costs the mesh for the session and never the frame:

```csharp
// src/KSAGolf/Ksa/ViewDrawHook.cs L37-L48
MethodInfo? target = typeof(SuperMeshRenderSystem).GetMethod(nameof(SuperMeshRenderSystem.ClearBuckets),
                                                             BindingFlags.Public | BindingFlags.Instance, [typeof(IViewport)]);
if (target is null)
{
    Log.Warn("the ball cannot be drawn: KSA has no SuperMeshRenderSystem.ClearBuckets(IViewport)");
    return;
}

MethodInfo postfix = typeof(ViewDrawHook).GetMethod(nameof(AfterClearBuckets), BindingFlags.NonPublic | BindingFlags.Static)!;
_harmony = new Harmony(HarmonyId);
_harmony.Patch(target, postfix: new HarmonyMethod(postfix));
Installed = true;
```
```csharp
// src/KSAGolf/Ksa/ViewDrawHook.cs L74-L88
private static void AfterClearBuckets(IViewport viewport)
{
    try
    {
        _draw?.Invoke(viewport);
    }
    catch (Exception e)
    {
        _draw = null;

        if (_complained) return;
        _complained = true;
        Log.Error("drawing into the world failed; the ball is no longer drawn this session", e);
    }
}
```

  Each piece is a `StaticMeshRenderable` whose `Transform` is in the camera-relative (ego) frame of the view being drawn (`positionEcl - viewport.GetCamera().PositionEcl`), submitted
  with `mesh.Draw(Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport))`. A see-through tinted copy is the ghost; a plinth is drawn under raised pieces:

```csharp
// src/KSAGolf/Ksa/CourseBuilder.cs L591-L601,L603-L622
private void DrawMeshes(IViewport viewport, double3 originEgo, double3 i, double3 j, double3 up)
{
    foreach (Placed piece in Course.Pieces) DrawPiece(viewport, piece, originEgo, i, j, up, id => MeshOf(id, piece.Def.Id));

    // The piece about to be laid, see-through and tinted by how it would sit.
    if (Active && _ghost is { } ghost)
    {
        string tint = ghost.Fit switch { Fit.Connects => "Fits", Fit.Open => "Open", _ => "Blocked" };
        DrawPiece(viewport, ghost.Piece, originEgo, i, j, up, id => GhostOf(id, tint));
    }
}
// ...
private static void DrawPiece(IViewport viewport, Placed piece, double3 originEgo, double3 i, double3 j, double3 up,
                              Func<string, StaticMeshRenderable?> meshOf)
{
    if (Model(piece) is not { } model || meshOf(model.Id) is not { } mesh) return;

    SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
    void Draw(StaticMeshRenderable m, double3 ex, double3 ey, double3 ez, double3 t)
    {
        m.Transform = new float4x4((float)ex.X, (float)ex.Y, (float)ex.Z, 0f,
                                   (float)ey.X, (float)ey.Y, (float)ey.Z, 0f,
                                   (float)ez.X, (float)ez.Y, (float)ez.Z, 0f,
                                   (float)t.X, (float)t.Y, (float)t.Z, 1f);
        m.Draw(system.ViewForViewport(viewport));
    }

    double c = CourseSize.CellM;
    (double2 x, double2 y, double2 at) = piece.Placement(model.Turn);
    double lift = piece.Level * CourseSize.LevelM;
    Draw(mesh, (i * x.X) + (j * x.Y), (i * y.X) + (j * y.Y), up, originEgo + (i * at.X) + (j * at.Y) + (up * lift));

```

  Only draws made into the main view cast shadows, because the cascaded shadow pass is recorded for the main view after its own clear.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs) | Enter, leave, view, input, editing, drawing |
| [src/KSAGolf/Sim/CourseBuild.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseBuild.cs) | `BuildView` (pan, zoom, turn, ray-plane, cell) and `Ghost` (snap, next orientation, mirror) |
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs) | Fixed controller subclass, pose source, input swallowing |
| [src/KSAGolf/Ksa/ViewDrawHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/ViewDrawHook.cs) | `ClearBuckets` postfix |
| [tests/KSAGolf.Tests/CourseBuildTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseBuildTests.cs) | Pan, zoom limits, ray finds its cell, ghost snapping and placement |

## Variations and gotchas

- **Swallowing is not total.** `GameSettings.OnKeyAll` and `Popup.OnKeyAll` see every key before the controller; a global binding on a swallowed key still fires.
- **Release keys pass through**, so a key held when building starts is released in the game; `ClearHeldPlayerInput()` on entry covers the rest.
- **Read input in the GUI hook only.** After `ImGui.Render` ImGui queries are meaningless.
- **Mirrored meshes.** A mesh is drawn turned but never mirrored (mirroring turns triangles inside out), so a chiral piece uses a separately modelled twin.
- **Reflection on a backing field** breaks silently on a rename; the mod warns and degrades (stock controller, view aimed a frame late).
- **Unverified in game.** The repo's list of in-game checks does not include the builder.
- **Version.** Built against 2026.9.22.5482; the `Program.cs` diff to 2026.10.10.5554 does not touch `OnKey`, `DispatchKeyEvent`, the mouse handler or `OnScroll`.

## See also

- [Overview of place-pieces-on-a-grid-with-a-ghost](overview.md)
- [Approach 2: gizmo rings anchored with GetPositionEgo](place-pieces-on-a-grid-with-a-ghost-2.md)
- [Cursor to world ray](../cursor-to-world-ray/overview.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Replace a viewport's camera controller and take input through it](../../../camera/controllers/replace-a-viewport-camera-controller/overview.md)
- Mod feature: [Top-down minigolf course builder](../../../../mods/ksagolf/features/minigolf-course-builder.md)

[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-supermeshrendersystem]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gizmosrenderer]: KSA.GizmosRenderer (decompiled, build 2026.10.10.5554)
