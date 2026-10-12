---
type: How-To
title: Let the IVA camera leave the seat with an IVAController subclass that delegates movement to a private FlyController
description: Kitchen Sink swaps the main viewport's IvaController for a subclass that stays in IVA mode but flies like the free camera in the vessel's body frame, with a Return to Seat path, a head-visibility postfix and scene-save restore
category: camera
group: special-views
topic: unlock-the-iva-camera
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/unscience/features/kitchen-sink-iva-camera-unlock]
techniques: [controller-subclass, reflection, harmony-postfix]
hooks:
  - reflection KSA.GameViewport.set_IvaController
  - harmony-postfix KSA.IVASeat.IsCameraInThisSeat
  - subclass KSA.IVAController
lifecycle:
  - startup:all-mods-loaded
  - frame:viewports
  - frame:input
  - frame:controller
  - frame:render
  - event:save
  - event:load
game_types: [KSA.GameViewport, KSA.IVAController, KSA.FlyController, KSA.IVASeat, KSA.Camera, KSA.Vehicle]
tags: [camera, iva, free-camera, controller, interior, save]
---
KSA's IVA camera is pinned to a seat: the player can look around but not move. Kitchen Sink's "Unlock IVA Camera" lets the camera fly through the cabin with the free-camera controls while everything that makes IVA look and sound like IVA stays on
(interior models, IVA lighting, ray tracing, the IVA audio mix). It does this without patching any camera code: it replaces the object in the main viewport's `IvaController` slot with a subclass that forwards to a private `FlyController` on the same `Camera`, and keeps the viewport in
`CameraMode.IVA`. Prerequisites: StarMap, Harmony and reflection. The swap and the input forwarding are in
[the controller topic](../../controllers/replace-a-viewport-camera-controller/overview.md) ([approach 3](../../controllers/replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-3.md)); this article covers the rest.

## Background: how the game does it

**What stock IVA does every frame.** It bails to `Program.HoveredViewport.NextCameraMode()` unless the camera follows a `Vehicle` equal to `LastFollowing`, then writes `Camera.PositionCce` at the seat and `LocalRotation` from the mouse, clamped to the seat's hemisphere:

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

`OnSwitchOn` assigns `Seat = span[0]` when entering for a new vehicle, bails when the vehicle has no `IVASeat` module, and sets `SwitchThisFrame` so the first frame applies no stale mouse delta ([current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L155-L198)). A mod can declare an `IVASeat` in its own part XML and IVA will find it
through `vehicle.Parts.Modules.Get<IVASeat>()`.

**What else depends on IVA mode being active.**

| Dependent | Test | Source |
|---|---|---|
| IVA audio blend | `GetActiveController() == IvaController` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L140) |
| IVA ray tracing | `IsRaytracingActive && MainViewport.Mode == CameraMode.IVA` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4775) |
| Hide the seated kitten's head | `IVASeat.IsCameraInThisSeat(viewport)`: mode IVA and `IvaController.Seat == this` | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L103), [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L107-L110) |

Because the subclass keeps `Mode == IVA` and stays in the slot, all three keep working.

## Lifecycle and timing

| Code | Runs | Value |
|---|---|---|
| `IvaCameraUnlock.Apply` (delegate + postfix) | At load, from the unscience host's `Patcher` in `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| `UnlockedIvaController.OnFrame` | `GameViewport.OnFrame` -> `GetActiveController()` -> controller `OnFrame`, then `Camera.OnFrame` | `frame:viewports` |
| `OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll` | From `Program` on the input viewport's active controller | `frame:input` |
| `GetCursorMode` | Read by `Program.OnFrameController` | `frame:controller` |
| Enable, Disable, speed, Return to Seat | Kitchen Sink panel | `frame:draw-ui` |
| `IsCameraInThisSeat` postfix | `RenderGame` -> seated crew render data per viewport | `frame:render` |
| Save capture and restore | Scene coordinator, order 190 | `event:save`, `event:load` |

## Steps

1. **Install** the typed setter delegate and the head postfix once ([kitchen-sink.lib/IvaCameraUnlock.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.cs#L35-L58)).

2. **Build the subclass on the same camera** and store the look in the vessel's body frame (`RotationBody`) so the cabin can rotate under it. `LocalPosition` is already in the body frame because the camera follows the vehicle:

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L11-L33
internal sealed class UnlockedIvaController : IVAController
{
    private readonly FlyController _fly;
    public GameViewport Viewport { get; }
    public Vehicle Vehicle { get; }
    public IVAController Original { get; }
    public doubleQuat RotationBody { get; private set; }

    public UnlockedIvaController(GameViewport viewport, Vehicle vehicle, IVAController original, double speed)
        : base(original.Camera, "Unlocked IVA")
    {
        Viewport = viewport;
        Vehicle = vehicle;
        Original = original;
        Seat = original.Seat;
        LastFollowing = vehicle;
        RotationBody = doubleQuat.Inverse(vehicle.Body2Cce) * Camera.LocalRotation;
        _fly = new FlyController(Camera, "Unlocked IVA movement");
        SetSpeed(speed);
        _fly.CacheOffset();
    }

    public void SetSpeed(double speed) => _fly.SetSpeed(speed, DistanceUnit.Meters);
```

3. **Each frame, re-apply the body-relative look, then let stock free flight move and turn it, then store the look back:**

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L39-L54
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
```

4. **Show the seated kitten's head while the camera is out of the seat.** The private `IsCameraInThisSeat` returns true while the unlocked controller keeps the same `Seat`; the postfix forces `false` for exactly the unlocked main viewport:

```csharp
// kitchen-sink.lib/IvaCameraUnlock.cs L68-L71
private static void ShowHeadWhenUnlocked(IViewport viewport, ref bool __result)
{
    if (_active != null && ReferenceEquals(viewport, _active.Viewport)) __result = false;
}
```

5. **Return to Seat.** Compute the stock seat pose, restore the original controller, and prime its bookkeeping so its first frame does not apply a stale mouse delta or rotation correction:

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L64-L77
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

6. **Validate every frame** (same viewport, still IVA, slot still ours, same followed vehicle alive and unique, seat still one of its modules). On failure restore the original controller without seat return and, if the viewport is still IVA, set `Orbit` mode, because stock IVA would
   dereference a stale seat or cycle the hovered viewport ([kitchen-sink.lib/IvaCameraUnlock.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.cs#L123-L132)).

7. **Save it separately from KSA's own camera save.** KSA saves the camera pose, but stock IVA would snap it back to the seat next frame, so the detached pose (body-frame position and look, the seat as a part reference plus index, the speed) is saved in its own record and restored only if the native load already put the camera in IVA following that vessel:

```csharp
// kitchen-sink.lib/IvaCameraUnlock.Persistence.cs L57-L79
private static void Restore(SavedCamera saved, SaveRestoreContext context)
{
    if (!saved.Enabled) { Speed = saved.Speed; return; }
    context.Require(IsAvailable, "IVA camera unlock patch is unavailable.");
    var vehicle = VehicleProvider.FindVehicle(saved.SeatPart!.VehicleId);
    var part = saved.SeatPart.Resolve();
    context.Require(vehicle != null && !vehicle.IsDisposed && part != null,
        "Missing or ambiguous unlocked IVA vehicle/seat: " + saved.SeatPart.VehicleId);
    var seats = part!.Modules.Get<IVASeat>();
    context.Require(saved.SeatIndex < seats.Length, "Saved IVA seat module is unavailable.");
    // Native reconstruction owns the followed target and mode. Never silently retarget
    // the camera to a controlled vehicle or enter IVA on an unrelated native save.
    context.Require(Program.Editor == null && Program.MainViewport is GameViewport
        && Program.MainViewport.Mode == CameraMode.IVA
        && ReferenceEquals(Program.MainViewport.GetCamera().Following, vehicle),
        "Native camera does not match the saved unlocked IVA target/mode.");
    var viewport = (GameViewport)Program.MainViewport;
    viewport.IvaController.Seat = seats[saved.SeatIndex];
    viewport.IvaController.LastFollowing = vehicle!;
    Speed = saved.Speed;
    context.Require(Enable(), LastError ?? "Could not restore unlocked IVA camera.");
    _active!.RestorePose(saved.PositionBody, saved.RotationBody);
}
```

## Complete example

- [kitchen-sink.lib/IvaCameraUnlock.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.cs), [kitchen-sink.lib/UnlockedIvaController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/UnlockedIvaController.cs), [kitchen-sink.lib/IvaCameraUnlock.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.Persistence.cs)
- [kitchen-sink.lib/KitchenSinkSubmod.IvaCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/KitchenSinkSubmod.IvaCamera.cs): the panel; [kitchen-sink.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.tests/README.md): the headless tests

## Variations and gotchas

- **No cabin collision.** Free flight inherits `FlyController.ClampCamera` (2 m above terrain) and `Camera.ClampCamera` (0.5 m).
- **Main viewport only.** Secondary viewports in IVA mode keep stock behaviour and stock head hiding.
- **Camera Controller Override** patches every `FlyController.OnFrame`, including the private one, so a playing camera sequence can take over the unlocked camera ([Animate the flight camera with eased moves, keyframe sequences and saved recipes](../chain-camera-moves-into-keyframe-sequences/overview.md)); the authors list the interaction as a pending in-game check.
- **Seat switching is suspended** until Return to Seat, which always goes to the seat the camera left.
- **Written against 5541**; the members used (`IVAController`, `IVASeat`, `FlyController`, `GameViewport`, `Camera`) are unchanged in 5554 per the feature page. A sealed setter would make `Apply` fail and the panel report the feature as unavailable.
- **Hidden heads in other places.** Stock hides the head only in the viewport whose IVA controller is seated in that seat.

## See also

- [Topic overview](overview.md)
- [Free-flying IVA camera by swapping the main viewport's IVA controller](../../../../mods/unscience/features/kitchen-sink-iva-camera-unlock.md)
- [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md), [Hide part meshes or subparts without removing them](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md), [Hide a kitten's body from the main view only, keeping its shadow](../../../kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md), [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md)
