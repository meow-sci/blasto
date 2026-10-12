---
type: How-To
title: Sit in a chair to fly another craft - swap ControlledVehicle, borrow a camera window and restore everything on log-off
description: KSAStructures builds a chair part with an IVASeat and an EVA door, treats the controlled chair as seated, hands the controls to the nearest craft and returns control and the main view on log-off or death
category: kittens
group: animation
topic: animate-and-draw-seated-kitten-crew
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by:
  - mods/ksastructures/features/computer-flies-nearest-craft
  - mods/ksastructures/features/office-furniture-craft
techniques: [viewport-borrow]
hooks:
  - starmap StarMapAfterGui
  - assignment KSA.Program.ControlledVehicle
  - reflection KSA.ViewportBase.OptionFlags
lifecycle: [frame:draw-ui, frame:viewports, event:key]
game_types:
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.Program
  - KSA.IVASeat
  - KSA.EVADoor
  - KSA.ViewportRegistry
  - KSA.GameViewport
  - KSA.Camera
tags: [kitten, seated, chair, control, camera, viewport, borrow]
---
In KSAStructures' office a kitten walks to a chair, boards it through its EVA door and sits. Pressing Enter then hands the player's controls to the nearest craft, so its throttle, staging and attitude keys now fly that craft, while the main view stays where the seated kitten is looking (at the monitor) and a camera window films the craft. Enter again logs off. The whole feature is one engine assignment, `Program.ControlledVehicle = nearest`, wrapped in careful borrowing and returning of cameras. This article starts from the kitten's side: how a chair becomes "seated", how the key is read, which craft is picked, and the many exits. Prerequisites: StarMap, a part XML with seat and door ([add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)), and the camera borrowing patterns ([borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md)). Related: [take control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md).

## Background: how the game does it

- A kitten can only sit in a seat that belongs to a part. `EVADoor.ResolveAlignedSeats` pairs a door with the `IVASeat` whose template id equals the door's `SeatId` on the same part ([current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L33-L61)).
- `KittenEva.BoardVehicle(host, door)` adds the kitten as crew in the aligned seat (or the first available seat), disposes the EVA kitten, makes the camera follow the host, sets `Program.ControlledVehicle = host` and `UpdateAfterPartTreeModification` ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L925-L965)). So after boarding the chair, "seated" simply means "the flown craft is the chair".
- `Program.ControlledVehicle`'s setter only clears the old vehicle's held player input; it does **not** move any camera or follow target ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L514)). Keys reach the new craft because `Program.DispatchKeyEvent` forwards to `ControlledVehicle.OnKey` while `IsControlledVehicleActive`, and the craft's `PrepareWorker` copies its held input into the worker state each step ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1992-L2000)).
- `ViewportRegistry.TryOpenSecondaryViewport` leases one of the spare secondary viewports (the call behind the "Add Camera" menu item); `ReleaseSecondaryViewport` returns it ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L180-L226)).
- The engine draws seated crew only for the controlled vehicle (`UpdateSeatedCrewRenderData`). While the controls are on the played craft the chair is no longer the controlled vehicle, so its kitten falls outside that call; the feature does not hook it (the mod's page describes no such hook, and the main view is held at the monitor, so no view needs the kitten) - inferred from the code, not checked in game.

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `startup:asset-load` | `mod.toml` `assets` | Chair part, door subpart, editor tag load from XML |
| `frame:draw-ui` | `[StarMapAfterGui]` -> `OfficeComputer.Draw` | Enter, hint, ImGui painted picture (ImGui sees keys only between `NewFrame` and `Render`) |
| `frame:draw-ui` | the mod's step -> `Drive(dtPlayer)` | Main view pose, window camera, monitor texture; on player time so cameras keep moving while paused |
| `frame:viewports` (next frame) | the replacement controller's `OnFrame` -> `TryPose` | Main view re-asked in phase with the frame |
| `event:key` | engine `Program.DispatchKeyEvent` | Flight keys go to the played craft once `ControlledVehicle` is swapped |
| `frame:draw-ui` (the mod's step) | `Office.Update` -> `EnsureChair` | Spawns missing furniture (blocks on the shapes lock; see below) |

## Steps

1. **Describe the chair in part XML:** a body subpart, a hidden door subpart beside the seat, a `Control` element, an `IVASeat` with eye position and axes, and an `EVADoor` that names the seat. The `Position` is the seated kitten's eye, 0.63 m over its model origin (`SeatedOffset`); the door sits where a kitten on the ground can reach it (part-level XML is covered in [define a part with game data](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)):

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6125-L6151, L6179-L6181 -->
  <Part Id="KSArmory_Prefab_OfficeChair">
    <SubPart Id="KSAOffice_Chair_Body" InstanceOf="KSAOffice_ChairBody_SubPart" />
    <SubPart Id="KSAOffice_Chair_Door" InstanceOf="KSAOffice_ChairDoor_SubPart">
      <Transform>
        <Position X="0.35000" Y="0.45000" Z="0.00000" />
      </Transform>
    </SubPart>
    <Connector Id="_chairConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation X="3.14159" Z="3.14159" />
      </Transform>
    </Connector>
  </Part>

  <PartGameData Id="KSArmory_Prefab_OfficeChair" DisplayName="Office Chair">
    <EditorTag Value="Structures" />
    <Diameter M="1" />
    <Connector Id="_chairConnectorBase" />
    <Control />

    <!-- The seat top is 0.32 m up; Position is the eye, 0.63 m over the seated kitten's origin, whose hips sit 0.19 m above that origin. -->
    <IVASeat Id="KSAOffice_Chair_Seat">
      <Position X="0.76" Y="0" Z="0" />
      <ForwardAxis Y="1" />
      <UpAxis X="1" />
    </IVASeat>
  <!-- ... -->
  <SubPartGameData Id="KSAOffice_ChairDoor_SubPart">
    <EVADoor SeatId="KSAOffice_Chair_Seat" />
  </SubPartGameData>
```

2. **Build the one-part craft in code and set it down.** A kitten can only sit in a seat that belongs to a part, so the chair is a craft of one part. `CraftSpawner.SpawnPartParked` gets the template with `ModLibrary.Get<PartTemplate>` (`TryGet` has no `PartTemplate` branch and answers false for every part), builds an orbit 500 m above the flown craft, takes the shapes lock with `ConstraintSim.UnlockShapesBlocking`, creates `new Part(id, template)`, `CreateOwnTree()`, `Vehicle.CreateVehicle(...)`, adds it to the parent's children and finally `TeleportToLocation`s it onto a latitude and longitude ([spawn approach 2](../../characters/spawn-an-eva-kitten-from-code/spawn-an-eva-kitten-from-code-2.md) has the same shape for a kitten). The start of the method:

```csharp
// src/KSAStructures/Ksa/CraftSpawner.cs L19-L27
            if (Universe.CurrentSystem is not { } system || platform.Parent is not { } parent) return null;

            // Get, not TryGet: TryGet has no PartTemplate case and answers false for every part.
            PartTemplate template = ModLibrary.Get<PartTemplate>(partId);
            double3 spawnEcl = KsaWorld.PositionEcl(platform) + KsaWorld.LocalUp(platform) * 500.0;
            if (!ToParentInertial(parent, spawnEcl, KsaWorld.VelocityEcl(platform), out double3 posCci, out double3 velCci))
            {
                return null;
```

3. **Keep the chair out of saves.** A postfix on `Universe.SerializeSystems` strips every `VehicleData` whose id is a furniture id (unless it is the craft being flown), so a save never names a part template only this mod provides; the part ids keep their original `KSArmory_` prefix because a save that names a missing part template cannot be loaded (feature page: [Office furniture as single-part craft](../../../../mods/ksastructures/features/office-furniture-craft.md)).

4. **Detect "seated".** Boarding made the chair the controlled vehicle, so seated is a check on the id:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L42-L46
    private static bool Seated(out Vehicle chair)
    {
        chair = KsaWorld.ControlledVehicle!;
        return chair is not null && KsaWorld.IsAlive(chair) && chair.Id == Office.ChairId;
    }
```

5. **Read Enter through ImGui, and ignore it while text is wanted.** The hook is `[StarMapAfterGui]`; `IsKeyPressed(..., repeat: false)` so holding Enter does not toggle repeatedly:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L49-L66
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
```

6. **Pick the nearest non-kitten craft, borrow a camera window, remember the main view, swap the controls.** Record the main view **before** the first write (a borrowed `Fixed` view clears the follow, so a recording taken afterwards cannot be restored):

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L82-L106
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

7. **Hold the main view on the monitor and film the craft each step,** in the chair's own frame (a separation and two directions, nothing that carries the ecliptic's motion); the window camera is aimed from the office's side of the craft, 6 craft radii back (clamped 20 to 250 m) and 30 percent of that above. Stop on any death or scene end:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L134-L159
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

   The camera windows are written after the engine's viewport pass for this frame, so the mod calls `camera.OnFrame(dt)` and re-uploads the viewport's shader and camera data itself; see [aim a secondary viewport camera](../../../camera/secondary-viewports/aim-a-secondary-viewport-camera/overview.md) and [borrow a secondary viewport](../../../camera/secondary-viewports/borrow-a-secondary-viewport/overview.md). The picture reaches the monitor mesh by pointing the monitor part's own textures at the camera window's picture (the mod's viewport image feature; see also [render ImGui into a texture](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md) for the related texture-substitution idea).

8. **Log off, handing everything back.** Restore the monitor's textures, close the window, set `ControlledVehicle` back to the chair and hand the main view back; `Unload` calls it too:

```csharp
// src/KSAStructures/Ksa/OfficeComputer.cs L108-L131
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

9. **Check the log.** `office computer: playing <craft>, N m away` after Enter; `office computer: logged off` on exit; the keys fly the played craft while the view stays at the monitor.

## Complete example

- [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L1-L200) - the whole controller: `Seated`, `Draw`, `Start`, `Stop`, `Drive`, `TryPose`.
- [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L1-L105) - furniture ids, parts, `EnsureChair`, `KeepOutOfSaves`.
- [src/KSAStructures/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CraftSpawner.cs#L1-L73) - building the one-part craft.
- [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6125-L6181) - the chair, seat and door.
- Feature pages: [Computer flies nearest craft](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md), [Office furniture as single-part craft](../../../../mods/ksastructures/features/office-furniture-craft.md).

## Variations and gotchas

- **`ControlledVehicle` and the camera are independent.** Setting the property changes who receives keys, not what any camera follows; everything that assumes the camera follows the controlled vehicle (UI panels, gauges) shows the played craft.
- **Restore in order.** Record the main view before the first write; unfollow a borrowed window every frame, because the next/previous-vessel keys act on the hovered viewport and can attach a follow to it.
- **Clamp the field of view.** `Camera.SetFieldOfView` does not clamp, and the projection throws for zero or beyond 180 degrees.
- **Four spare windows exist** in this build; `TryOpenSecondaryViewport` fails when the player already has them all open.
- **Furniture spawn blocks on the shapes lock** (`UnlockShapesBlocking` spin-waits while the vehicle update is stepping), so the step may wait for one worker step.
- **Chair ids must never change:** a save that names a missing part template cannot be loaded.
- **Text focus.** Ignore Enter while `WantTextInput` is true so typing in a mod window does not log off.

## See also

- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](overview.md), [approach 1](animate-and-draw-seated-kitten-crew-1.md), [approach 2](animate-and-draw-seated-kitten-crew-2.md)
- [Take control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md), [Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)
