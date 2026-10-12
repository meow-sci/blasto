---
type: Mod Feature
title: Free-flying IVA camera by swapping the main viewport's IVA controller
description: Kitchen Sink swaps the main viewport's IVAController for a subclass that delegates movement to a private FlyController in the vessel body frame, keeping IVA mode, audio, ray tracing and interiors while the camera leaves the seat
mod: unscience
submod: kitchen-sink
category: camera
techniques: [reflection, harmony-postfix, controller-subclass, save-sidecar]
hooks:
  - reflection KSA.GameViewport.IvaController (protected setter as an open delegate)
  - subclass-override KSA.IVAController.OnFrame, OnKey, OnMouseButton, OnCursorPos, OnScroll, GetCursorMode, OnSwitchOff
  - harmony-postfix KSA.IVASeat.IsCameraInThisSeat(IViewport) (private)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, validity check)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, Unlock and Return to Seat)
  - unscience ISaveParticipantSource (scene save record kitchen-sink-iva-camera)
lifecycle: [frame:viewports, frame:input, frame:controller, frame:draw-ui, frame:render, event:save, event:load]
game_types:
  - KSA.GameViewport
  - KSA.IVAController
  - KSA.FlyController
  - KSA.Controller
  - KSA.IVASeat
  - KSA.Camera
  - KSA.Vehicle
  - KSA.Part
  - KSA.Program
tags: [camera, iva, controllers, free-camera, reflection, harmony, scene-saves]
---

KSA's IVA (interior) camera is pinned to a seat: the player can look around but not move. Kitchen
Sink's "Unlock IVA Camera" lets the camera fly freely through the cabin with the normal free-camera
controls (hold left mouse to look, scroll to change speed from 0.01 to 100 m/s, default 0.5) while
everything that makes IVA look and sound like IVA stays on: interior models, IVA lighting, the game's
IVA ray tracing setting and the IVA audio mix. The camera moves with the vessel as it translates and
rotates. "Return to Seat" (or turning the toggle off) puts the camera back exactly where stock IVA
expects it.[^ks-readme]

Mechanism in one line: instead of patching any camera code, the feature replaces the object in the
main viewport's `IvaController` slot with `UnlockedIvaController : IVAController`, which forwards frame
and input calls to a private `FlyController` on the same `Camera`; the viewport stays in
`CameraMode.IVA`, so every stock `Mode == IVA` and `GetActiveController() == IvaController` check still
passes.[^iva-unlock][^unlocked-controller]

## How it works

Install (`IvaCameraUnlock.Apply`, both hosts):[^iva-unlock]

- `AccessTools.PropertySetter(typeof(GameViewport), nameof(GameViewport.IvaController))` gets the
  protected setter and `CreateDelegate<Action<GameViewport, IVAController>>()` turns it into a typed
  open-instance delegate. No Harmony patch is needed to swap controllers.
- One Harmony postfix on the private `IVASeat.IsCameraInThisSeat(IViewport)`, looked up with its exact
  parameter list.
- If either lookup fails, `Apply` removes what it installed, records `LastError` (shown in the panel) and
  rethrows so the host logs it.

Enable (`IvaCameraUnlock.Enable`), only when `CanEnable` holds: not in the editor, the main viewport is
a `GameViewport` in IVA mode, the followed object is a live, uniquely identified `Vehicle`, and the IVA
controller's `Seat` is one of that vessel's `IVASeat` modules.

1. Build `UnlockedIvaController(viewport, vehicle, viewport.IvaController, Speed)`. It shares
   `original.Camera`, copies `Seat`, sets `LastFollowing`, stores the current look as
   `RotationBody = Inverse(vehicle.Body2Cce) * Camera.LocalRotation`, and creates
   `new FlyController(Camera, "Unlocked IVA movement")` with `SetSpeed(speed, DistanceUnit.Meters)`.
2. `_setController(viewport, controller)`; keep the original for restoration.
3. `vehicle.ClearHeldPlayerInput()` so throttle or RCS keys held at that moment do not stick.

Per frame (`UnlockedIvaController.OnFrame`):[^unlocked-controller]

1. `IvaCameraUnlock.Update()` validates (see below); stop if the slot is no longer ours.
2. Re-apply the body-relative look: `Camera.LocalRotation = Vehicle.Body2Cce * RotationBody`, then
   `_fly.CacheOffset()` so the fly controller starts from that orientation.
3. If input is blocked (another input viewport, a modal window, the console, or ImGui wants the
   keyboard or text input), `ReleaseInput()` (the fly controller's `OnSwitchOff`, which clears held
   keys and look state) and skip movement. Otherwise run `_fly.OnFrame(inViewport, dt)`.
4. Store the new look back into the body frame:
   `RotationBody = Normalize(Inverse(Body2Cce) * Camera.LocalRotation)`.

Position needs no extra work: for a camera following an object, `Camera.PositionCce` is
`LocalPosition` transformed by `following.GetBodyFixed2Ecl()`, and for a `Vehicle` that is
`Body2Cce`. `LocalPosition` is therefore already in the vessel's body frame and moves and rotates with
it.[^ksa-camera][^ksa-vehicle]

Input overrides: `OnKey`, `OnMouseButton` and `OnCursorPos` forward to the fly controller unless
blocked (seat switching is intentionally not forwarded); `OnScroll` multiplies the base speed by
`2^(offset.Y / 50)` so it is independent of the sprint modifier; `GetCursorMode` returns the fly
controller's mode (cursor captured while looking) or `Normal` when blocked; `IsMouseDrag` and
`CancelMouseDrag` forward. `OnSwitchOff(nextMode)` calls `Disable(returnToSeat: false)`, so a native
camera-mode change first puts the original IVA controller back.

Validation (`IvaCameraUnlock.IsValid`, run from the controller frame and from the sub-mod `Update`):
not in the editor; same main viewport; still IVA mode; the slot still holds this controller; the camera
still follows the same vessel; the vessel is not disposed and `VehicleProvider.FindVehicle(id)` returns
exactly it; the seat is still one of its modules. On failure the original controller is restored
without seat return and, if the viewport is still in IVA, `SetCameraMode(CameraMode.Orbit)` is called
on the main viewport. Stock `IVAController.OnFrame` instead calls `Program.HoveredViewport.NextCameraMode()`
when its target changes, which acts on whichever viewport is hovered.[^ksa-ivacontroller]

Return to Seat (`Disable(returnToSeat: true)` -> `ReturnToSeat()`): computes the stock seat pose
(`seat.Parent.PositionVehicleAsmbOffset(seat.PositionAsmb)` into body coordinates, look along the
seat's forward and up axes), restores the original controller, and primes its `LastFollowing`,
`LastLocalRotation`, `LastFollowingAsmb2Cce`, clears `LastCursorDiffs` and sets `SwitchThisFrame` so the
stock controller's first frame does not apply a stale mouse delta or rotation correction.

Head visibility: stock `IVASeat.UpdateSeatedRenderable` sets
`_renderable.HideHead = IsCameraInThisSeat(viewport)`, which is true when the viewport is in IVA mode
and its IVA controller's `Seat` is this seat. The unlocked controller keeps the same `Seat`, so the
postfix forces `false` for exactly the unlocked main viewport; the seated kitten's head becomes visible
when the camera leaves it.[^ksa-ivaseat]

Scene save (record `kitchen-sink-iva-camera`, version 1, restore order 190):[^iva-persistence]

- Saved: `Enabled`, `Speed`, `SeatPart` (a `SavedPartReference` to the seat's part), `SeatIndex` (index
  in that part's `IVASeat` modules), `PositionBody` (`Camera.LocalPosition`) and `RotationBody`.
  Every property is `[JsonRequired]`; validation checks finite values, speed range, a unit quaternion
  and the part reference shape.
- Restore: requires the patch, the vessel and seat, and that the native load already put the main
  camera in IVA mode following that vessel. It never enters IVA or retargets by itself. It then sets the
  stock controller's `Seat` and `LastFollowing`, calls `Enable()` and `RestorePose(...)`. KSA's own save
  stores the camera pose too, but stock IVA would snap it back to the seat on the next frame, which is
  why the detached pose is saved separately.
- Reset (before the old world is destroyed) disables without seat return and resets speed.

## Lifecycle and timing

| Code | When | Notes |
|---|---|---|
| `UnlockedIvaController.OnFrame` | `frame:viewports`: `GameViewport.OnFrame` -> `GetActiveController()` (IVA -> `IvaController` slot) -> `OnFrame`, then `Camera.OnFrame` bakes matrices | Vessel `Body2Cce` is the main-thread value applied this frame; the camera is main-thread only |
| IVA audio blend | `frame:viewports`, `GameViewport.HandleIvaAudioBlend` (`GetActiveController() == IvaController`) | True for the subclass, so IVA ambience and capsule rattle stay on[^ksa-gameviewport] |
| `OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll` | `frame:input`, from `Program.DispatchKeyEvent` and the mouse handlers on `InputViewport.GetActiveController()` | |
| `GetCursorMode` | `frame:controller`: `Program.OnFrameController` (L2520) reads it to set the GLFW cursor mode and ImGui's `NoMouse` flag | |
| Enable, Disable, speed, Return to Seat | `frame:draw-ui` (Kitchen Sink panel) | Takes effect in the next frame's viewport update |
| `IvaCameraUnlock.Update` | `frame:draw-ui` (sub-mod `Update`) and `frame:viewports` (controller) | |
| `IsCameraInThisSeat` postfix | `frame:render`: `RenderGame` -> `ControlledVehicle.UpdateSeatedCrewRenderData` (L4702; L4495 for secondary views) -> `IVASeat.UpdateSeatedRenderable` | Secondary views keep stock head hiding |
| IVA ray tracing | `frame:render`: `if (IsRaytracingActive && MainViewport.Mode == CameraMode.IVA)` (L4775) | Unchanged, because the mode stays IVA[^ksa-program] |
| Save capture and restore | `event:save` / `event:load`, order 190 | Restore needs the native IVA mode and follow target first |

## Key code

| Path | Role |
|---|---|
| [kitchen-sink.lib/IvaCameraUnlock.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.cs#L35-L153) | Setter delegate, head postfix, enable, disable, validation |
| [kitchen-sink.lib/UnlockedIvaController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/UnlockedIvaController.cs#L11-L107) | IVA subclass delegating to a private `FlyController` |
| [kitchen-sink.lib/IvaCameraUnlock.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.Persistence.cs#L13-L79) | Exact seat and pose save record |
| [kitchen-sink.lib/KitchenSinkSubmod.IvaCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.IvaCamera.cs#L7-L28) | Checkbox, speed field, Return to Seat |
| [kitchen-sink.lib/KitchenSinkLib.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkLib.cs#L21-L25) | Per-frame validity check in `Update` |

## Snippets

Install: a typed delegate to a protected setter, plus one scoped postfix:

```csharp
// kitchen-sink.lib/IvaCameraUnlock.cs L40-L71
            var setter = AccessTools.PropertySetter(typeof(GameViewport), nameof(GameViewport.IvaController))
                ?? throw new MissingMethodException(nameof(GameViewport), "set_IvaController");
            var assign = setter.CreateDelegate<Action<GameViewport, IVAController>>();
            _headOriginal = AccessTools.Method(typeof(IVASeat), "IsCameraInThisSeat", new[] { typeof(IViewport) })
                ?? throw new MissingMethodException(nameof(IVASeat), "IsCameraInThisSeat");
            _headPostfix = AccessTools.Method(typeof(IvaCameraUnlock), nameof(ShowHeadWhenUnlocked));
            harmony.Patch(_headOriginal, postfix: new HarmonyMethod(_headPostfix));
            _setController = assign;
// ...
    private static void ShowHeadWhenUnlocked(IViewport viewport, ref bool __result)
    {
        if (_active != null && ReferenceEquals(viewport, _active.Viewport)) __result = false;
    }
```

The controller: body-frame look, native free flight, then back to the body frame:

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L39-L77
    public override void OnFrame(IViewport inViewport, double inDeltaTime)
    {
        try
        {
            IvaCameraUnlock.Update();
            if (!ReferenceEquals(Viewport.IvaController, this)) return;
            // Camera.LocalPosition already follows the vehicle's body frame. Make orientation
            // follow it too, then let stock free flight apply this frame's translation/look.
            Camera.LocalRotation = Vehicle.Body2Cce * RotationBody;
            _fly.CacheOffset();
            if (InputBlocked) ReleaseInput();
            else _fly.OnFrame(inViewport, inDeltaTime);
            RotationBody = doubleQuat.Normalize(doubleQuat.Inverse(Vehicle.Body2Cce) * Camera.LocalRotation);
        }
        catch (Exception ex) { IvaCameraUnlock.Fail(ex); }
    }
// ...
    public void ReturnToSeat()
    {
        var seat = Original.Seat;
        double3 position = seat.Parent.PositionVehicleAsmbOffset(seat.PositionAsmb);
        Camera.PositionCce = double3.Transform(Vehicle.PosAsmbToBody(position), Vehicle.Body2Cce);
        Camera.LocalRotation = KSA.Camera.LookAtRotation(
            double3.Transform(seat.ForwardAxisAsmb, Vehicle.Asmb2Cce * seat.Parent.Asmb2VehicleAsmb),
            double3.Transform(seat.UpAxisAsmb, Vehicle.Asmb2Cce * seat.Parent.Asmb2VehicleAsmb));
        Original.LastFollowing = Vehicle;
        Original.LastLocalRotation = Camera.LocalRotation;
        Original.LastFollowingAsmb2Cce = Vehicle.Asmb2Cce;
        Array.Clear(Original.LastCursorDiffs);
        Original.SwitchThisFrame = true;
    }
```

What the stock controller does every frame (pin to the seat; on a target change, cycle the hovered
viewport's mode):

```csharp
// current/decomp/KSA/IVAController.cs L27-L41
	public override void OnFrame(IViewport inViewport, double inDeltaTime)
	{
		if (!(Camera.Following is Vehicle vehicle))
		{
			Program.HoveredViewport.NextCameraMode();
			return;
		}
		if (vehicle != LastFollowing)
		{
			Program.HoveredViewport.NextCameraMode();
			return;
		}
		CursorPositionScreen = double2.Unpack(Cursor.GetPosition(inViewport));
		double3 posAsmb = Seat.Parent.PositionVehicleAsmbOffset(Seat.PositionAsmb);
		Camera.PositionCce = vehicle.PosAsmbToBody(posAsmb).Transform(vehicle.Body2Cce);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GameViewport.IvaController { get; protected set; }` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L40) | Slot replaced through the setter delegate |
| `GameViewport.GetActiveController`, `HandleIvaAudioBlend`, `SetCameraMode` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L134-L140) | IVA identity checks; safe exit to Orbit (`SetCameraMode` L302) |
| `IVAController` (`Seat`, `LastFollowing`, `LastLocalRotation`, `LastFollowingAsmb2Cce`, `LastCursorDiffs`, `SwitchThisFrame`, `OnFrame`) | [current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L9-L41) | Base class and seat-return priming |
| `FlyController(Camera, string)`, `SetSpeed`, `CacheOffset`, `OnFrame`, `OnSwitchOff`, `GetCursorMode` | [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L112-L137) | Private movement engine (`CacheOffset` L866, `GetCursorMode` L880, `OnSwitchOff` L907) |
| `IVASeat.IsCameraInThisSeat(IViewport)` (private), `UpdateSeatedRenderable` | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L110) | Head hiding decision |
| `Camera.PositionCce`, `LocalPosition`, `LocalRotation`, `LookAtRotation` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L81-L100) | Body-relative camera position |
| `Vehicle.Body2Cce`, `Asmb2Cce`, `GetBodyFixed2Ecl`, `PosAsmbToBody`, `ClearHeldPlayerInput` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L476-L500) | Body frame (`GetBodyFixed2Ecl` L1307, `PosAsmbToBody` L1284, `ClearHeldPlayerInput` L6094) |
| `Program.OnFrameController`, `DispatchKeyEvent`, `IsWindowOpen`, `InputViewport`, IVA ray tracing gate | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2551) | Cursor mode, input routing, `Mode == IVA` render gate (L4775) |

## Pitfalls and version notes

- The swap only works because `IvaController` has a protected setter and `GameViewport` always asks for
  the controller through that property. If a future build caches the controller elsewhere or seals the
  setter, `Apply` fails and the panel reports the feature as unavailable.[^scope-camera]
- Free flight inherits `FlyController.ClampCamera` (2 m above terrain) and `Camera.ClampCamera` (0.5 m);
  there is no cabin collision.[^ksa-flycontroller]
- Only the main viewport is unlocked. Secondary viewports in IVA mode keep stock behavior and stock
  head hiding.
- Camera Controller Override's prefix on `FlyController.OnFrame` also applies to the private fly
  controller, so a playing camera sequence can take over the unlocked IVA camera (the authors list this
  interaction as a pending in-game check).[^ks-tests-readme]
- Seat switching is suspended while unlocked; Return to Seat always goes to the seat the camera left.
- The fly controller's `OnSwitchOff` is used as "release held input"; it is called whenever input is
  blocked, so held movement keys stop when a text field takes focus.
- Managed tests in `kitchen-sink.tests` link the real `IvaCameraUnlock` and controller code against
  fixture game types (47 checks); native input, rendering and audio acceptance is still listed as
  pending by the authors.[^ks-tests-readme]
- Written against 5541; every member it uses is unchanged in 5554 (`IVAController.cs`, `IVASeat.cs`,
  `FlyController.cs`, `GameViewport.cs` and `Camera.cs` have no diff between the two builds).

## See also

- [IVA force render](./lib-iva-force-render.md) (interiors outside IVA; not needed inside IVA)
- [Kitchen Sink capsule glass](./kitchen-sink-capsule-glass.md)
- [Camera animations](./camera-controller-override-camera-animations.md)
- [Saved part identity](./lib-save-part-identity.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Sub-mod: Kitchen Sink](../submods/kitchen-sink.md)
- [Mod overview](../overview.md)
- [Swap IvaController with a typed open-instance delegate and forward every input callback to a private FlyController](../../../howto/camera/controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-3.md) - how-to article demonstrating this feature
- [Let the IVA camera leave the seat with an IVAController subclass that delegates movement to a private FlyController](../../../howto/camera/special-views/unlock-the-iva-camera/unlock-the-iva-camera.md) - how-to article demonstrating this feature

[^ks-readme]: kitchen-sink/README.md
[^iva-unlock]: kitchen-sink.lib/IvaCameraUnlock.cs
[^unlocked-controller]: kitchen-sink.lib/UnlockedIvaController.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ivacontroller]: KSA.IVAController (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: KSA.IVASeat (decompiled, build 2026.10.10.5554)
[^iva-persistence]: kitchen-sink.lib/IvaCameraUnlock.Persistence.cs
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^scope-camera]: scope/camera.md (authors' camera integration analysis)
[^ksa-flycontroller]: KSA.FlyController (decompiled, build 2026.10.10.5554)
[^ks-tests-readme]: kitchen-sink.tests/README.md
