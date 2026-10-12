---
type: How-To
title: Queue TeleportToLocation once per action
description: Pick a craft and a destination, then call Vehicle.TeleportToLocation exactly once per action from a GUI hook and let ApplyInputEvents apply it at the next PrepareFrame, with a hover and pick-up UI over it
category: physics
group: kinematics
topic: place-a-vessel-on-a-surface-at-latitude-and-longitude
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/craft-mover
  - mods/ksacars/features/craft-mover-mouse-pick-place
  - mods/ksarmory/features/spawn-and-teleport-test-craft
techniques: [starmap-lifecycle, imgui-input]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Vehicle, KSA.InputEvents, KSA.Celestial]
tags: [physics, kinematics, teleport, surface, latitude, longitude, craft-mover, test-range]
---

Outcome: a tool that picks any craft up with one click and sets it down on the ground under the next, resting on the terrain
and upright. KSAGolf, KSACars and KSArmory ship the same mover (carried between the three), which lays out a test range
without flying anything into place. Prerequisites: StarMap. The placement is a single call; everything else is picking.

## Background: how the game does it

`Vehicle.TeleportToLocation` computes the start state immediately and only queues it:

```csharp
// current/decomp/KSA/Vehicle.cs L4219-L4233
	public void TeleportToLocation(Celestial celestial, double lat, double lon)
	{
		double3 double5 = new double3(_props.BoundingBoxAsmb.HalfWidth, _props.BoundingBoxAsmb.HalfHeight, _props.BoundingBoxAsmb.HalfLength);
		double3 boundsMinAsmb = MassToGeometryAsmb - double5;
		double3 boundsMaxAsmb = MassToGeometryAsmb + double5;
		InitialKinematicState initialKinematicStateForLocation = GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon, boundsMinAsmb, boundsMaxAsmb, double3.Zero, Orbit.OrbitLineColor);
		InputEvents.TeleportInputData data = new InputEvents.TeleportInputData
		{
			Vehicle = this,
			Orbit = initialKinematicStateForLocation.Orbit,
			Body2Cce = initialKinematicStateForLocation.Body2Cce,
			BodyRates = initialKinematicStateForLocation.BodyRates
		};
		InputEvents.TeleportInputBuffer.Add(data);
	}
```

`ApplyInputEvents` drains the buffer in the next frame's `PrepareFrame`, clears the craft's burn plan, calls
`Vehicle.Teleport(orbit, body2Cce, bodyRates)` and resets the transfer planner, so the placement happens in the one
main-thread window where physics results are settled. Calling it every frame would rebuild the orbit and velocity each
time, which is why the craft does not follow the cursor.[^ksa-inputevents]

## Lifecycle and timing

| Moment | What runs |
|---|---|
| `frame:draw-ui` | `StarMapAfterGui`: after the panel draws (so a click on a window is not a click on the world), the mover reads ImGui mouse state and submits gizmos; only while a craft is flown |
| `frame:input` (next frame) | `PrepareFrame` -> `InputEvents.ApplyInputEvents` applies the queued teleport, before `ExecuteNextVehicleSolvers` dispatches the next step |

Queuing is what makes calling from the GUI hook safe: the mod never mutates vehicle state while the solver jobs are running.
A teleported craft is still at its old position for the rest of the current frame. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Find the body by id in the current system and call the engine once. Return false on any failure so the UI can say so:

```csharp
// src/KSACars/Ksa/KsaWorld.cs L361-L389
    /// on the ground rather than with its origin there.
    /// </summary>
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

2. Hover: every frame, project each live craft to the screen and give it a pick radius equal to its apparent size clamped to 14-140 px;
   the nearest within reach is the hover. See [cursor picking](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-4.md).
3. Click handling: ignore the click when `ImGui.GetIO().WantCaptureMouse`; first click holds the craft; the second resolves a
   target (a hovered craft's own footing, otherwise the cursor ray on the terrain) as body, latitude and longitude via
   `Celestial.GetLatitudeFromCce`/`GetLongitudeFromCce`.
4. "Leave it": pointing at the held craft, or within 12 m of where it is, changes nothing. Placing re-derives a position from
   latitude and longitude and the surface rule, which on a launch pad is metres below the craft, so a click that should change
   nothing would otherwise drop it:

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

5. While held, draw gizmo rings for the footprint and landing spot from the GUI hook (gizmos submitted from the frame postfix are
   cleared before they are drawn). The craft itself does not follow the cursor.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CraftMover.cs) | Hover, pick up, put down, draw |
| [src/KSACars/Ksa/CraftMover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/CraftMover.cs) | The same tool in KSACars |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L358-L389) | `TryPlaceOnSurface` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | KSArmory's copy used by the debug pane and the bridge |

## Variations and gotchas

- The craft arrives east-facing and upright. Over the sea it is set on the seabed. A point inside a launch pad's footprint stands
  the craft on the pad.
- One action at a time: each placement is a buffered event that re-initialises the kinematic state.
- "Send to body" panel buttons call the same function at latitude 0, longitude 0 on any body.
- Spawned craft use the same call after the shape-lock spawn; see [teleport approach 2](../teleport-a-vessel-by-building-a-new-orbit/teleport-a-vessel-by-building-a-new-orbit-2.md).

## See also

- [Overview and comparison](overview.md)
- [Transpile for head-up bounds](place-a-vessel-on-a-surface-at-latitude-and-longitude-2.md), [lend a roof height](place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md)
- Mod features: [KSAGolf craft mover](../../../../mods/ksagolf/features/craft-mover.md), [KSACars craft mover](../../../../mods/ksacars/features/craft-mover-mouse-pick-place.md), [KSArmory spawn and teleport test craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md)

[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
