---
type: How-To
title: Switch the controlled vehicle and restore the main view afterwards
description: Assign Program.ControlledVehicle to another craft, show it in a borrowed secondary viewport, and hand the controls and the main view back when done
category: vehicles-and-parts
group: vehicles
topic: take-control-of-another-vehicle
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/computer-flies-nearest-craft]
techniques: [viewport-borrow, starmap-lifecycle, reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.ViewportBase.OptionFlags]
lifecycle: [frame:draw-ui, frame:viewports, event:key]
game_types: [KSA.Program, KSA.Vehicle, KSA.ViewportRegistry, KSA.IGameViewport, KSA.ViewportBase, KSA.KittenEva]
tags: [control, viewport, camera, restore, seated]
---
Outcome: while the player sits in a chair (an EVA kitten's seat), pressing Enter makes the nearest craft the
controlled vehicle, so its throttle, staging and attitude keys fly it, while the main view stays on the
seated kitten and a spare camera window shows the craft. Enter again returns control to the chair. The key
engine fact is one assignment, `Program.ControlledVehicle = nearest`; the rest of this article is the careful
borrowing and returning that makes the assignment survivable.

Prerequisite: StarMap. Demonstrated by [ksastructures/computer-flies-nearest-craft](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md). The seat/door side is in
[Add crew seats and EVA doors to a part](../../parts/add-crew-seats-and-eva-doors/overview.md).

## Background: how the game does it

- The property setter clears the old vehicle's held input and stores the new vehicle; it does not touch the
  camera, so the main view keeps following whatever it followed
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L514)[^ksa-program].
- Key events go to `ControlledVehicle.OnKey` when `IsControlledVehicleActive`
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1985-L2000); `Vehicle.ClearHeldPlayerInput` zeroes thruster flags, sprint, grab and
  engine flags [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6094-L6100)[^ksa-vehicle]. The vehicle's own `PrepareWorker` copies its held input into the
  worker on the next step.
- A seated kitten is the controlled vehicle only because boarding the chair's EVA door made the *chair* the
  controlled vehicle (chair parts, `Vehicle.Id == "Office chair"`).
- `ViewportRegistry.TryOpenSecondaryViewport` is the call behind View > Add Camera. KSA builds four spare
  windows at startup and leases them [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L180-L245)[^ksa-viewportregistry].
- `ViewportBase.OptionFlags` has a protected setter, so hiding the engine's own window around the picture
  needs reflection.

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]` -> `Draw` | read Enter via ImGui (ignored while `WantTextInput`), start/stop |
| `frame:draw-ui` | the step -> `Drive(dtPlayer)` | aim the main view and the borrowed window camera |
| `frame:viewports` (next frame) | a controller replaced in the main viewport | re-asks the pose in phase |
| `event:key` | engine, `Program.DispatchKeyEvent` | flight keys go to the played craft |

ImGui sees keys only between `NewFrame` and `Render`, which is why the toggle is read in the draw-UI
phase. A borrowed window's camera is written after the engine's viewport pass for this frame, so the mod
re-runs `camera.OnFrame(dt)` and re-uploads the camera and shader data itself. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) for the hook points.

## Steps

1. Detect the trigger and the seated state from `StarMapAfterGui`:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L42-46, L49-72
    private static bool Seated(out Vehicle chair)
    {
        chair = KsaWorld.ControlledVehicle!;
        return chair is not null && KsaWorld.IsAlive(chair) && chair.Id == Office.ChairId;
    }
// ...
    public void Draw()
    {
        try
        {
            bool enter = !ImGui.GetIO().WantTextInput
                         && (ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false));

            if (Playing)
            {
                if (enter) { Stop(); return; }
                Paint();
                Hint($"Playing {KsaWorld.DisplayName(_played!)} - Enter logs off");
            }
            else if (Seated(out Vehicle chair))
            {
                if (enter) Start(chair);
                else Hint("Enter: play KSA on the computer");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"office computer: {e.Message}");
        }
    }
```

2. Pick the craft and take the controls. **Record the main view before the first write**; setting a Fixed
   camera mode clears the follow, and a reading taken afterwards describes the borrowed state:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L82-106
    private void Start(Vehicle chair)
    {
        Vehicle? nearest = null;
        double best = double.MaxValue;
        double3 here = KsaWorld.PositionEcl(chair);
        foreach (Vehicle v in KsaWorld.Vehicles)
        {
            if (v is KittenEva || ReferenceEquals(v, chair) || !KsaWorld.IsAlive(v) || v.Id is Office.ChairId or Office.MonitorId) continue;

            double d = Vec.Len(KsaWorld.PositionEcl(v) - here);
            if (d < best) (best, nearest) = (d, v);
        }

        if (nearest is null) { Log.Info("office computer: there is no craft to play"); return; }
        if (!KsaWorld.TryOpenCameraWindow(out _window)) { Log.Warn("office computer: no camera window to show the game in"); return; }

        KsaWorld.TryHideViewportWindow(_window);
        KsaWorld.ResizeViewport(_window, new int2(768, 416));
        _shown = -1;
        _saved = KsaWorld.RememberMainView();
        _chair = chair;
        _played = nearest;
        Program.ControlledVehicle = nearest;
        Log.Info($"office computer: playing {KsaWorld.DisplayName(nearest)}, {best:F0} m away");
    }
```

3. Implement `RememberMainView` and `TryHandBackMainView` with the follow, the camera mode and the field of
   view (carry the FOV: a borrower that zooms and returns everything but the zoom leaves the player with no
   control that widens it):

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L486-499
    public static MainView RememberMainView()
    {
        try
        {
            if (Program.MainViewport is not { } viewport) return default;

            return new MainView(viewport.GetCamera()?.Following, viewport.Mode, MainViewFovDeg(), true);
        }
        catch (Exception e)
        {
            Log.Warn($"could not read the main view: {e.Message}");
            return default;
        }
    }
```

4. Open the spare window, hide the engine's window chrome by clearing `HasUi | RenderOrbitLines`, and give
   the window back when finished:

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L831-860
    public static bool TryOpenCameraWindow(out int index)
    {
        index = -1;

        try
        {
            if (!ViewportRegistry.TryOpenSecondaryViewport(out IGameViewport? opened)) return false;

            opened.SetVisible(true);

            // The index is a position in the registry's span, so it is read back rather than
            // remembered -- opening one is exactly the event that can move the others.
            ReadOnlySpan<IGameViewport> viewports = GameViewports;
            for (int i = 0; i < viewports.Length; i++)
            {
                if (ReferenceEquals(viewports[i], opened))
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Warn($"camera: could not open a window ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }
```

```csharp
// src/KSAStructures/Ksa/KsaWorld.cs L883-904
    public static bool TryHideViewportWindow(int index)
    {
        try
        {
            if (OptionFlagsSetter is null) return false;
            if (!TryViewport(index, out IGameViewport viewport)) return false;
            if (viewport.Type != ViewportType.Secondary || !viewport.Visible) return false;

            // Orbit lines too: a view looking past the Moon otherwise shows its orbit across the picture.
            const ViewportOptionFlags taken = ViewportOptionFlags.HasUi | ViewportOptionFlags.RenderOrbitLines;

            ViewportOptionFlags flags = viewport.OptionFlags;
            if ((flags & taken) == 0) return true;

            OptionFlagsSetter.Invoke(viewport, [flags & ~taken]);
            return (viewport.OptionFlags & taken) == 0;
        }
        catch
        {
            return false;
        }
    }
```

5. Each frame aim the window camera at the craft (here: 6 radii back, 30 % above, clamped 20-250 m) and, if
   you keep the main view somewhere else, write that pose too:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L134-159
    public void Drive(double dt)
    {
        if (!Playing) return;
        if (!KsaWorld.IsAlive(_chair) || !KsaWorld.IsAlive(_played) || !KsaWorld.InFlightScene) { Stop(); return; }

        try
        {
            if (TryPose(KsaWorld.PositionEcl(_chair!), out double3 offset, out double3 forward, out double3 up, out double fov))
                KsaWorld.TryLookFromMainViewport(offset, forward, up, fov, this);

            // From the office's side of the craft, a little above it, so the tower is behind the camera.
            double3 at = KsaWorld.PositionEcl(_played!);
            double3 vertical = KsaWorld.LocalUp(_played!);
            double3 away = Vec.Unit(Vec.RejectFrom(at - KsaWorld.PositionEcl(_chair!), vertical));
            if (!Vec.IsFinite(away) || Vec.Len2(away) < 0.5) away = Vec.Unit(Vec.AnyPerpendicular(vertical));

            double distance = Math.Clamp(6.0 * KsaWorld.MeanRadius(_played!), 20.0, 250.0);
            double3 eye = at - (away * distance) + (vertical * (distance * 0.3));
            KsaWorld.TryLookFromViewport(_window, eye, Vec.Unit(at - eye), vertical, FovDeg, dt);
            ShowOnTheMesh(live: true);
        }
        catch (Exception e)
        {
            Log.Warn($"office computer: {e.Message}");
        }
    }
```

6. Stop cleanly: close the window, assign `Program.ControlledVehicle` back to the chair, hand the main view
   back. Also stop when either vehicle dies or the flight scene ends:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L108-131
    public void Stop()
    {
        if (!Playing) return;

        try
        {
            ShowOnTheMesh(live: false);
            if (_window >= 0) KsaWorld.CloseCameraWindow(_window);
            if (KsaWorld.IsAlive(_chair))
            {
                Program.ControlledVehicle = _chair;
                KsaWorld.TryHandBackMainView(_saved, _chair, out _, out _);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"office computer: could not log off cleanly ({e.Message})");
        }

        _window = -1;
        _chair = null;
        _played = null;
        Log.Info("office computer: logged off");
    }
```

7. Expected log: `office computer: playing <craft>, <metres> m away` on start and `office computer: logged
   off` on stop.

## Complete example

- [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs)[^ksastructures-officecomputer] - start, stop, drive
- [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L470-L600)[^ksastructures-ksaworld] - main-view record and hand-back
- [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L820-L960) - borrowed window helpers

## Variations and gotchas

- **Handing back is not always possible.** A follow target that was destroyed is skipped by
  `TryHandBackMainView` (`CanFollow`), because the engine has already pointed the view at the wreckage.
- **Unrelated crafts keep their controls.** Setting `ControlledVehicle` clears only the outgoing vehicle's
  held input; any engine you lit stays lit.
- **A seated kitten is a vehicle too**, so filter `KittenEva` and your own props when choosing "nearest".
- **Spare viewports are scarce.** There are four; `TryOpenSecondaryViewport` returns false when all are
  leased, and the player closing the window releases the lease for you.
- **Protected setter.** `OptionFlags` is reached through `GetSetMethod(nonPublic: true)`; if it moves, the
  engine's own window stays around the picture, which is cosmetic.
- **Game updates.** `ControlledVehicle`, `TryOpenSecondaryViewport` and `ReleaseSecondaryViewport` are
  the public surface; the reflection lines are the ones to recheck.

## See also

- [Overview](overview.md), [Approach 2: fly it without switching](take-control-of-another-vehicle-2.md)
- [ksastructures/computer-flies-nearest-craft](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md)
- [Add crew seats and EVA doors to a part](../../parts/add-crew-seats-and-eva-doors/overview.md)

[^ksa-viewportregistry]: current/decomp/KSA/ViewportRegistry.cs (decompiled, build 2026.10.10.5554)
[^ksastructures-officecomputer]: KSAStructures: src/KSAStructures/Ksa/OfficeComputer.cs
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksastructures-ksaworld]: KSAStructures: src/KSAStructures/Ksa/KsaWorld.cs
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
