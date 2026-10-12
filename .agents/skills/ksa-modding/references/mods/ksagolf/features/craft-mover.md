---
type: Mod Feature
title: Craft mover
description: Click a craft to pick it up and click the ground to set it down, or send the flown craft to any body, through Vehicle.TeleportToLocation so the engine rebuilds an upright resting state from the craft's bounding box
mod: ksagolf
category: vehicles-and-parts
techniques: [imgui-window, gizmo-drawing]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Vehicle, KSA.Celestial, KSA.InputEvents, KSA.Universe, KSA.CelestialSystem]
tags: [teleport, placement, picking, gizmos, developer-tools, input]
---

A development tool for laying out a test range: with "Move craft with the mouse" ticked, the craft
under the cursor gets a ring around its feet; a click picks it up, a second click on the ground
sets it down there (with a ring, a stalk and a line showing where it will land), and clicking the
held craft again leaves it where it is. A separate panel row sends the controlled craft to latitude
0, longitude 0 on any body in the system.

All placement goes through `Vehicle.TeleportToLocation(celestial, lat, lon)`, the engine's own
"put this craft on the ground here" call, which builds the initial kinematic state from the craft's
bounding box so the hull arrives resting on the terrain instead of with its origin at the point.[^ksa-vehicle]

## How it works

| Step | Detail |
|---|---|
| Hover | Every frame: collect live vehicles, project each to the screen (`TryProjectAhead`), give each a pick radius equal to its apparent size (`TryApparentRadiusPixels` of `MeanRadius`, clamped 14 to 140 px), choose the nearest within reach (`Picking.NearestWithin`)[^golf-craftmover][^golf-picking] |
| Click filter | `ImGui.IsMouseClicked(Left)` without Shift, ignored when `ImGui.GetIO().WantCaptureMouse` (the click belongs to a window) |
| Pick up | Remember the craft; it does not follow the cursor, because each placement re-initialises its kinematic state and orbit |
| Target | If the cursor is over a craft, that craft's own footing (`Celestial.GetSurfacePositionEclFromCce`), else the first point where the cursor ray goes under the ground (`TryCursorGroundPoint`, see [Cursor picking and terrain ray](cursor-picking-and-terrain-ray.md)); converted to latitude, longitude and body id with `GetLatitudeFromCce` / `GetLongitudeFromCce`[^ksa-celestial] |
| Put down | Within 12 m of where it is, or on itself: leave it. Otherwise `KsaWorld.TryPlaceOnSurface(craft, bodyId, lat, lon)`, which finds the `Celestial` by id and calls `TeleportToLocation`[^golf-ksaworld] |
| Draw | Rings are tori of gizmo spheres draped on the ground, oriented by local gravity; drawn through the two-instant draw anchor (see [Frame and epoch draw anchor](frame-epoch-draw-anchor.md)) |
| Send to body | Panel buttons per body (tooltip says whether it has an atmosphere) call the same `TryPlaceOnSurface` at 0, 0[^golf-ui] |

Engine side, `TeleportToLocation` computes `GetInitialKinematicStateForLocation(celestial,
Universe.GetNextSimStep().NextTime, lat, lon, boundsMin, boundsMax, ...)` and adds an
`InputEvents.TeleportInputData` to `InputEvents.TeleportInputBuffer`.[^ksa-vehicle] That buffer is
applied in `InputEvents.ApplyInputEvents`, which clears the craft's burn plan, calls
`Vehicle.Teleport(orbit, body2Cce, bodyRates)` and resets the transfer planner.[^ksa-inputevents]

## Lifecycle and timing

| Moment | What runs |
|---|---|
| `frame:draw-ui` | `[StarMapAfterGui]`: after the panel draws (so a click on a window is not a click on the world), `_mover.Update` reads ImGui mouse state and `_mover.Draw` submits gizmos; only while a craft is flown[^golf-mod] |
| `frame:input` (next frame) | `PrepareFrame` -> `InputEvents.ApplyInputEvents` applies the queued teleport, before `ExecuteNextVehicleSolvers` dispatches the next physics step[^ksa-program] |

Queuing is what makes calling from the GUI hook safe: the mod never mutates vehicle state while the
solver jobs are running; the engine applies the change in the one main-thread window where physics
results are settled.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CraftMover.cs) | Hover, pick up, put down, draw |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L534-L560) | `TryPlaceOnSurface` |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L727-L755) | `TryCraftSurfacePoint` |
| [src/KSAGolf/Sim/Picking.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Picking.cs) | Nearest-within-reach screen picking |
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L247-L307) | Mover toggle and send-to-body row |

## Snippets

```csharp
// src/KSAGolf/Ksa/CraftMover.cs L153-L189
    private void PutDown()
    {
        if (_held is not { } craft) return;

        // Pointing at the held craft means "leave it". Placing re-derives a position from the
        // latitude and longitude and the surface rule, which is not where the craft is: on a
        // launch pad it is metres below, so a click that should change nothing drops it.
        if (ReferenceEquals(_hovered, craft))
        {
            Log.Info($"left {KsaWorld.DisplayName(craft)} where it was");
            _held = null;
            return;
        }

        if (!TryTarget(out double3 target, out double lat, out double lon, out string body))
        {
            Log.Info("nothing under the cursor to set it down on");
            return;
        }

        // Or near enough to it. The pick radius is generous, so the cursor can be a little off the
        // craft and still mean the same thing.
        if (Vec.Len(target - KsaWorld.PositionEcl(craft)) < StayPut)
        {
            Log.Info($"left {KsaWorld.DisplayName(craft)} where it was");
            _held = null;
            return;
        }

        if (KsaWorld.TryPlaceOnSurface(craft, body, lat, lon))
        {
            Log.Info($"placed {KsaWorld.DisplayName(craft)} on {body} at "
                     + $"{lat:F4}, {lon:F4}");
        }

        _held = null;
    }
```

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L534-L560
    public static bool TryPlaceOnSurface(Vehicle craft, string bodyName,
                                         double latitudeDeg, double longitudeDeg)
    {
        if (!IsAlive(craft)) return false;
        if (!double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg)) return false;

        try
        {
            if (Universe.CurrentSystem is not { } system) return false;

            for (int i = 0; i < system.Count; i++)
            {
                if (system.GetIndex(i) is not Celestial body) continue;
                if (body.Id != bodyName) continue;

                craft.TeleportToLocation(body, latitudeDeg, longitudeDeg);
                return true;
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Warn($"could not place {DisplayName(craft)}: {e.Message}");
            return false;
        }
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Vehicle.TeleportToLocation(Celestial, double, double)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Queue a surface placement built from the craft's bounds |
| `InputEvents.TeleportInputBuffer`, `ApplyInputEvents` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs) | Where the queued teleport is applied |
| `Celestial.GetLatitudeFromCce`, `GetLongitudeFromCce`, `GetSurfacePositionEclFromCce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs) | Converting a ground point to the placement arguments |

## Pitfalls and version notes

- Placement re-derives the height from latitude, longitude and the surface rule, which is not where
  a craft on a launch pad stands (metres off), so "put it back where it was" must be a no-op rather
  than a teleport to its own coordinates.[^golf-craftmover]
- A ray through a craft's middle meets the ground behind it; aiming at a craft uses that craft's
  footing instead of the ray hit.
- The send-to-body row always uses 0, 0, which can be ocean on a body with water.
- `KsaWorld` also carries `Remove` and `Destroy` helpers that take the vehicle-solver barrier
  (`JobSystems.VehicleSolver.Wait()`) and `ConstraintSim.UnlockShapesBlocking()` before
  `Universe.DestroyVehicle`; nothing in this revision calls them.[^golf-ksaworld]

## See also

- [Cursor picking and the terrain ray](cursor-picking-and-terrain-ray.md)
- [Frame and epoch draw anchor](frame-epoch-draw-anchor.md)
- [Developer panel and menu-bar entry](developer-panel-and-menu-bar.md)
- [Terrain height queries off the engine's height field](terrain-height-queries.md)
- [Queue TeleportToLocation once per action](../../../howto/physics/kinematics/place-a-vessel-on-a-surface-at-latitude-and-longitude/place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md) - how-to article demonstrating this feature
- [Pick objects under the cursor: screen-space nearest with a per-item reach](../../../howto/ui/input/pick-objects-under-the-cursor/pick-objects-under-the-cursor-1.md) - how-to article demonstrating this feature

[^golf-craftmover]: src/KSAGolf/Ksa/CraftMover.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-ui]: src/KSAGolf/Ksa/Ui/Ui.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-picking]: src/KSAGolf/Sim/Picking.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
