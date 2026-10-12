---
type: Mod Feature
title: Top-down minigolf course builder
description: A B-key build mode that turns the main view straight down over a body-fixed course grid, picks the cell under the cursor, snaps a ghost piece onto open ports, and places, moves, removes, undoes and saves pieces while taking the keys and clicks away from the kitten
mod: ksagolf
category: ui
techniques:
  - imgui-input
  - starmap-lifecycle
  - camera-controller-subclass
  - harmony-postfix
  - reflection
  - ray-plane-pick
  - headless-tests
hooks:
  - starmap StarMapAfterGui
  - subclass KSA.FixedController (OnKey, OnMouseButton, OnScroll, IsMouseDrag overrides)
  - reflection KSA.GameViewport.<FixedController>k__BackingField
  - harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets
lifecycle: [frame:input, frame:viewports, frame:draw-ui, frame:render]
game_types:
  - KSA.Controller
  - KSA.FixedController
  - KSA.GameViewport
  - KSA.Program
  - KSA.Vehicle
  - KSA.KittenEva
  - KSA.Celestial
  - KSA.GizmosRenderer
  - KSA.SuperMeshRenderSystem
  - KSA.StaticMeshRenderable
  - KSA.ImGuiBackendGlfwImpl
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiIOPtr
tags: [builder, editor, minigolf, camera, input, imgui, ghost, snapping, gizmos, top-down]
---

With a flown kitten standing on a world, B (or the panel's "Build a course" button) turns the main
view straight down over a grid laid at the kitten. The cell under the cursor is outlined, a ghost of
the chosen piece follows it and snaps onto the nearest open port of the hole (green when it would
connect, amber when it fits but touches no lane, red when blocked), and a click lays it. Number keys
choose a piece, 0 picks pieces up to move them, R steps through the orientations that actually
differ, M mirrors, PageUp/PageDown change level, Delete or X removes, Ctrl+Z/Ctrl+Y undo and redo,
WASD or the arrows pan, Q/E turn the view, the wheel zooms and a right or middle drag pans. A panel
window adds Undo, Redo, Clear, a sample hole, Save, Load and Done.[^golf-coursebuilder][^golf-ui]

While building, the kitten does not walk off and clicks do not open part windows: the builder's
view takes the keyboard and mouse away from the game and reads them through ImGui instead.

## How it works

**Entering.** `Enter()` needs `golf.Kitten`, the flown kitten golf is holding (the panel's "Play
golf" setting, on by default), with a `Celestial` parent. It sets `golf.Suspended`
(which hands golf's own borrowed view back first), anchors the course frame at the kitten unless one
already exists on the same body within 200 m, calls `kitten.ClearHeldPlayerInput()`, and remembers
the player's main view. The frame is body-fixed: up is the kitten's feet direction, the grid's +J is
the kitten's facing projected onto the horizontal, +I is `J x up`, and the first cell starts a metre
ahead, centred on the kitten's line. The floor is the ground at the anchor (`Golf.BodyGround`:
terrain, sea, launch-site top) plus 2 cm, and after every edit `Relevel` raises it to 2 cm above the
highest ground under any piece corner, so no piece is buried.[^golf-coursebuilder][^golf-golf]

**The view.** `BuildView` (Sim) holds the centre in course-local metres, a height between 3 and
60 m (12 m to start), and quarter turns; panning is scaled by height and a wheel step zooms by a
factor of 1.2.[^golf-coursebuild] Each frame `Step()` converts the eye, a straight-down forward and
the screen-up axis to `Ecl` and calls `KsaWorld.TryLookFromMainViewport(eye - kittenEcl, forward,
up, 50, this)`. That puts the main viewport in `CameraMode.Fixed` with the mod's
`LevelHorizonController` (a `FixedController` subclass installed by writing
`GameViewport.<FixedController>k__BackingField`), and registers the builder as the controller's
`IViewPose`, so the controller asks `TryPose` again inside the engine's viewport pass and checks
`TakesInput`.[^golf-ksaworld][^golf-levelhorizon]

**The cell under the cursor.** `KsaWorld.TryCursorRayEcl` gives the cursor ray (see
[Cursor picking and terrain ray](cursor-picking-and-terrain-ray.md)); `AimFromCursor` transforms it
into course-local coordinates and `BuildView.OnPlane` intersects it with the plane at the current
level's height (`level * 0.30 m`); `CellAt` floors the hit to a cell. The bridge can pin the cursor
to a cell with `AimAt` until the mouse moves.

**The ghost.** `Ghost.At(course, def, orientation, level, cell)` (Sim) first tries
`Course.Snap`, which joins the nearest open port within two cells in the requested orientation or
the next one that fits; otherwise the piece is held by its first port's tile at the cursor. `Fit`
colours it; only `Connects` and `Open` are placeable. See
[Course grid and save file](course-grid-and-save-file.md) for the rules.[^golf-coursebuild]

**Editing.** A click with a piece selected applies `Course.With(ghost)` through `CourseEdits` and
carries the level up after a ramp; in move mode a click removes the piece under the cursor and
selects its kind, orientation and level, so the next click lays it again. Every change runs
`Changed()`: rebuild the outline lines, re-check the hole, relevel, refresh the ghost and status, and
`Publish()` the course to the ball (`golf.Course = (body, CourseSurface)`) and to the kitten's
colliders (`CourseCollider.Want`).

**Taking input from the game.** GLFW key callbacks go to `ImGuiBackendGlfwImpl.OnKey`, which adds the
event to ImGui's IO and then forwards it to `Program.OnKey`.[^ksa-imguibackend] `Program.OnKey` runs
`GameSettings.OnKeyAll` and `Popup.OnKeyAll` first, then `DispatchKeyEvent`, which asks the
viewport's active controller before the controlled vehicle's `OnKey`, the call that walks a
kitten.[^ksa-program] While `TakesInput` is true `LevelHorizonController.OnKey` swallows letters,
digits, Space and Insert through PageDown (Escape and the function keys still reach the game),
`OnMouseButton` and `OnScroll` return true, and `IsMouseDrag()` returns true, which makes
`Vehicle.OnMouseButton` ignore the right-button release that would open a part's window.[^golf-levelhorizon][^ksa-vehicle]
The builder then reads the same keys through `ImGui.IsKeyPressed` and `ImGui.IsKeyDown`, gated on
`WantTextInput`, and the mouse gated on `WantCaptureMouse`.[^imgui-api]

**Drawing.** From the mod's postfix on `SuperMeshRenderSystem.ClearBuckets`, once per rendered
viewport: each piece's mesh is drawn as a `StaticMeshRenderable` with its transform set from the
course axes in camera-relative coordinates, a see-through tinted copy for the ghost, and a plinth
under raised pieces. Grid, outlines, faulty pieces (red), openings (green), lanes (yellow) and the
cursor (cyan) go to `Program.GizmosRenderer.DrawLine` in the main viewport only. Nothing is drawn
when the course origin is more than 2 km from the camera.[^golf-coursebuilder][^ksa-gizmos][^ksa-staticmesh]

**Leaving.** `Leave()` hands the main view back with `TryHandBackMainView` and lifts golf's
suspension. A save load calls `Forget()` (drop the course, the frame and the colliders); unload calls
`Release()`, which disposes the cached meshes.

## Lifecycle and timing

| Work | Hook | Phase | Thread |
|---|---|---|---|
| B toggle, keys, wheel, drags, clicks, ghost | `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`), after the panel draws and only while `KsaWorld.InFlight` | `frame:draw-ui` | main |
| Point the view | `KSAGolfMod.StepOnce` (same GUI hook, or its F2 fallbacks) calls `Step()` | `frame:draw-ui` | main |
| View re-asked in phase | `LevelHorizonController.OnFrame`, from `GameViewport.OnFrame` in `Program.OnFrameViewports` | `frame:viewports` | main |
| Swallow keys and clicks | Controller overrides, called from `Program.OnKey`, `OnMouseButton` and `OnScroll` during `Glfw.PollEvents` in `PrepareFrame` | `frame:input` | main |
| Draw meshes and gizmo lines | Harmony postfix on `SuperMeshRenderSystem.ClearBuckets` (called by `Program.RenderViewport` for each secondary view and inline in `RenderGame` for the main view, before the prepass; gizmo lines are copied to the GPU later in the same `RenderGame`) | `frame:render` | main |

The input is read after the panel (`_ui.Draw()` comes first in `OnAfterGui`) so a click on a window
is not also a click on the world.[^golf-mod] It must be read while ImGui's frame is open, which is
why it lives in the GUI hook and not in the step that may run from the frame postfix. Meshes must be
submitted from the `ClearBuckets` postfix because a static mesh draw submitted earlier in the frame
is cleared by that call before rendering (see [Mod mesh draw hook](mod-mesh-draw-hook.md)).

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L88-L209) | Enter, leave, anchor the frame, point the view |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L211-L438) | Input through ImGui, cursor cell, editing, publish, relevel, status |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L529-L694) | Drawing: meshes, ghost, plinths, gizmo grid and lines |
| [src/KSAGolf/Sim/CourseBuild.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CourseBuild.cs) | `BuildView` (pan, zoom, turn, ray-plane, cell) and `Ghost` (snap, next orientation, mirror) |
| [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L82-L142) | Input swallowing while a view takes input |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L164-L189) | `OnAfterGui`: step, panel, golf keys, builder input |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L184-L228) | The builder's panel window |
| [tests/KSAGolf.Tests/CourseBuildTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/CourseBuildTests.cs) | Pan follows the turned screen, zoom limits, the ray finds its cell, ghost snapping and placement, R and M |

## Snippets

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
// src/KSAGolf/Ksa/CourseBuilder.cs L214-L226, L252-L270
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

```csharp
// src/KSAGolf/Ksa/LevelHorizonController.cs L82-L102
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
```

```csharp
// src/KSAGolf/Sim/CourseBuild.cs L44-L52, L64-L70
    public static double2? OnPlane(double3 origin, double3 direction, double heightM)
    {
        if (Math.Abs(direction.Z) < 1e-9) return null;

        double t = (heightM - origin.Z) / direction.Z;
        return t > 0.0 ? new double2(origin.X + (direction.X * t), origin.Y + (direction.Y * t)) : null;
    }

    public static Cell CellAt(double2 metres) => new((int)Math.Floor(metres.X / CourseSize.CellM), (int)Math.Floor(metres.Y / CourseSize.CellM));
    // ...
    public static Ghost At(Course course, PieceDef def, Orientation orientation, int level, Cell cursor)
    {
        if (course.Snap(def, orientation, cursor) is { } snapped) return new Ghost(snapped, course.Judge(snapped), true);

        var free = new Placed(def, cursor - orientation.Apply(def.Ports[0].Cell, def.W, def.H), orientation, level);
        return new Ghost(free, course.Judge(free), false);
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Controller.OnKey` / `OnMouseButton` / `OnScroll` / `IsMouseDrag` (virtual) | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L22-L90) | Overridden by the mod's `FixedController` subclass to swallow input |
| `GameViewport.FixedController` (get-only, protected setter) | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42) | Replaced through its compiler backing field |
| `ImGuiBackendGlfwImpl.OnKey` | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L496-L522) | ImGui gets the key first, then `Program.OnKey` |
| `Program.OnKey` / `DispatchKeyEvent` / `OnMouseButton` / `OnScroll` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1800) | Order: settings, popups, camera, active controller, then the controlled vehicle (mouse handler at L2003) |
| `Vehicle.OnMouseButton` / `ClearHeldPlayerInput` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3687) | Right-release opens a part window unless the controller is dragging; held inputs cleared on entry (L6094) |
| `GizmosRenderer.DrawLine` / `Program.GizmosRenderer` / `Program.MainViewport` | [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L217) | Camera-relative line gizmos in the main view |
| `SuperMeshRenderSystem.ViewForViewport` / `ClearBuckets` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L237) | The per-viewport view handle a mesh is drawn into |
| `StaticMeshRenderable.Transform` / `Draw(ViewHandle)` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L11-L56) | One renderable per piece kind, drawn once per placed piece |
| `ImGui.IsKeyPressed` / `IsKeyDown` / `IsMouseDown` / `IsMouseClicked` / `GetIO` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8141-L8200) | Polled input, gated on `WantTextInput` and `WantCaptureMouse` |

## Pitfalls and version notes

- **Swallowing is not total.** `GameSettings.OnKeyAll` and `Popup.OnKeyAll` see every key before the
  controller, and every `Vehicle.OnMouseButton` is asked before the controller's; a global binding
  on a swallowed key still fires.[^ksa-program]
- **Release keys pass through.** The controller swallows presses and repeats, not releases, so a key
  held when building starts is still released in the game; `ClearHeldPlayerInput()` on entry covers
  the rest.
- **A borrowed view has to be given back.** `GameViewport.NextCameraMode` (the camera-mode key)
  cycles Orbit, Free and IVA and returns false for `Fixed`, so a player cannot leave a view the mod
  left in `Fixed` mode by hand; the builder, and golf which it suspends, hand the view back on every
  exit path. See [Level-horizon camera controller](level-horizon-camera-controller.md).[^ksa-gameviewport]
- **Read input in the GUI hook only.** The step can also run from the frame postfix, after
  `ImGui.Render`, where ImGui input queries are meaningless; the builder's `Input` is only called from
  `OnAfterGui`.
- **Mirrored meshes.** A mesh is drawn turned but never mirrored (mirroring turns triangles inside
  out), so a chiral piece uses a separately modelled mirror twin; see
  [Course grid and save file](course-grid-and-save-file.md).
- **Unverified in game.** The repo lists what has been seen in game since the cut; the builder is not
  on that list.
- **Version.** Built against 2026.9.22.5482. The `Program.cs` diff to 2026.10.10.5554 does not touch
  `OnKey`, `DispatchKeyEvent`, the mouse handler or `OnScroll`; `Controller`, `FixedController` and
  `GameViewport.FixedController` keep their shape.

## See also

- [Course grid model and JSON save file](course-grid-and-save-file.md)
- [Course surface from exact shapes](course-surface-exact-shapes.md)
- [Course colliders registered as ground clutter](course-colliders-as-clutter.md)
- [Cursor picking and the terrain ray](cursor-picking-and-terrain-ray.md)
- [Level-horizon camera controller swapped into the main viewport](level-horizon-camera-controller.md)
- [Borrowing the main view and handing it back](borrowed-chase-camera.md)
- [Drawing a mod's static mesh into every viewport](mod-mesh-draw-hook.md)
- [Developer panel and menu-bar entry](developer-panel-and-menu-bar.md)
- [Putting session with a mouse-swung club](putting-session.md)
- [Course frame anchored at the kitten and saved as latitude, longitude, heading and floor height](../../../howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-3.md) - how-to article demonstrating this feature
- [Share input with the game: gate on WantTextInput and WantCaptureMouse in the GUI hook](../../../howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-1.md) - how-to article demonstrating this feature
- [Share input with the game: a FixedController subclass swallows OnKey, OnMouseButton and OnScroll](../../../howto/ui/input/coordinate-imgui-focus-with-game-input/coordinate-imgui-focus-with-game-input-5.md) - how-to article demonstrating this feature
- [Place pieces on a grid: ray-plane cell pick, port-snapping ghost, top-down camera and per-viewport meshes](../../../howto/ui/input/place-pieces-on-a-grid-with-a-ghost/place-pieces-on-a-grid-with-a-ghost-1.md) - how-to article demonstrating this feature

[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-coursebuild]: src/KSAGolf/Sim/CourseBuild.cs
[^golf-levelhorizon]: src/KSAGolf/Ksa/LevelHorizonController.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-ui]: src/KSAGolf/Ksa/Ui/Ui.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguibackend]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-gizmos]: KSA.GizmosRenderer (decompiled, build 2026.10.10.5554)
[^ksa-staticmesh]: KSA.StaticMeshRenderable (decompiled, build 2026.10.10.5554)
[^imgui-api]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
