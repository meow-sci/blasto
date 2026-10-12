---
type: How-To
title: Swap IvaController with a typed open-instance delegate and forward every input callback to a private FlyController
description: Kitchen Sink builds an Action<GameViewport, IVAController> from the protected IvaController setter, installs an IVAController subclass and forwards frame and input callbacks to a private FlyController unless another input owner is active
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/unscience/features/kitchen-sink-iva-camera-unlock]
techniques: [reflection, controller-subclass, harmony-postfix]
hooks:
  - reflection KSA.GameViewport.set_IvaController
  - harmony-postfix KSA.IVASeat.IsCameraInThisSeat
  - subclass KSA.IVAController
lifecycle: [startup:all-mods-loaded, frame:input, frame:viewports, frame:controller]
game_types:
  - KSA.GameViewport
  - KSA.IVAController
  - KSA.FlyController
  - KSA.Controller
  - KSA.Program
  - Brutal.GlfwApi.GlfwCursorMode
  - Brutal.GlfwApi.GlfwMouseButton
tags: [camera, iva, controller, reflection, delegate, input, flycontroller]
---
Kitchen Sink's "Unlock IVA Camera" replaces the main viewport's `IVAController` with a subclass that keeps
IVA mode (so interiors, audio and ray tracing stay on) but lets the free-camera controls move the
camera through the cabin. The mechanism here is the controller swap and the **input forwarding**; the
full feature (seat return, saves, head visibility) is [Let the IVA camera leave the seat and fly around the cabin](../../special-views/unlock-the-iva-camera/overview.md). Prerequisites:
StarMap, Harmony and reflection.

## Background: how the game does it

`GameViewport.IvaController` is `{ get; protected set; }` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L40)) and
`GetActiveController()` returns it for `CameraMode.IVA` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L295)), so a subclass
placed in the slot receives `OnFrame`, the key, mouse and scroll callbacks, `GetCursorMode` and
`IsMouseDrag` while the mode is IVA. The stock `IVAController` pins the camera to an `IVASeat` and
cycles the hovered viewport's mode when its preconditions fail ([current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L27-L41)). Input
reaches whichever viewport owns it: `Program.OnMouseButton` latches `_inputViewport` on a press and
dispatches to `InputViewport.GetActiveController()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2050)).

## Lifecycle and timing

- **Install** (`IvaCameraUnlock.Apply`): at load. The unscience host calls it from `Patcher.Patch()`, itself called in its
  `[StarMapAllModsLoaded]` method ([unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L139), [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L90); `startup:all-mods-loaded`); it only
  needs the type, not a viewport, because the delegate is an open-instance one.
- **Swap in/out** (`Enable`/`Disable`): from the Kitchen Sink panel, `frame:draw-ui`.
- **Callbacks**: `OnKey`, `OnMouseButton`, `OnCursorPos`, `OnScroll` in `frame:input`; `OnFrame` in
  `frame:viewports`; `GetCursorMode` read by `Program.OnFrameController` in `frame:controller`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552)).

## Steps

1. **Build a typed open-instance delegate on the protected setter.** `AccessTools.PropertySetter` finds the
   non-public setter; `CreateDelegate<Action<GameViewport, IVAController>>()` makes a call that costs the same as
   a normal method call. If any lookup fails, `Apply` unpatches what it did, records the error for the
   panel and rethrows:

```csharp
// kitchen-sink.lib/IvaCameraUnlock.cs L35-L58
public static void Apply(Harmony harmony)
{
    if (IsAvailable) return;
    try
    {
        var setter = AccessTools.PropertySetter(typeof(GameViewport), nameof(GameViewport.IvaController))
            ?? throw new MissingMethodException(nameof(GameViewport), "set_IvaController");
        var assign = setter.CreateDelegate<Action<GameViewport, IVAController>>();
        _headOriginal = AccessTools.Method(typeof(IVASeat), "IsCameraInThisSeat", new[] { typeof(IViewport) })
            ?? throw new MissingMethodException(nameof(IVASeat), "IsCameraInThisSeat");
        _headPostfix = AccessTools.Method(typeof(IvaCameraUnlock), nameof(ShowHeadWhenUnlocked));
        harmony.Patch(_headOriginal, postfix: new HarmonyMethod(_headPostfix));
        _setController = assign;
        LastError = null;
        Console.WriteLine("kitchen-sink: IVA camera unlock available");
    }
    catch (Exception ex)
    {
        Remove(harmony);
        LastError = "IVA camera unlock is unavailable: " + ex.Message;
        Console.WriteLine("kitchen-sink: " + LastError);
        throw;
    }
}
```

2. **Subclass the controller and share the camera.** The subclass passes `original.Camera`, copies
   `Seat` and `LastFollowing`, and owns a private `FlyController` on the same camera:

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L11-L31
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
```

3. **Install it and keep the original for the way back.** Clear held vehicle input so throttle or RCS keys
   do not stick across the swap; `Disable` writes the original back only if the slot still holds ours:

```csharp
// kitchen-sink.lib/IvaCameraUnlock.cs L87-L99
public static bool Enable()
{
    if (Enabled) return true;
    if (!CanEnable(out string reason)) { LastError = reason; return false; }
    var viewport = (GameViewport)Program.MainViewport;
    var vehicle = (Vehicle)viewport.GetCamera().Following!;
    var controller = new UnlockedIvaController(viewport, vehicle, viewport.IvaController, Speed);
    _setController!(viewport, controller);
    _active = controller;
    vehicle.ClearHeldPlayerInput();
    LastError = null;
    return true;
}
```

```csharp
// kitchen-sink.lib/IvaCameraUnlock.cs L101-L113
public static void Disable(bool returnToSeat = true)
{
    var active = _active;
    _active = null;
    if (active == null) return;
    active.ReleaseInput();
    bool valid = IsValid(active);
    if (ReferenceEquals(active.Viewport.IvaController, active))
    {
        _setController!(active.Viewport, active.Original);
        if (returnToSeat && valid) active.ReturnToSeat();
    }
}
```

## Take input through the controller

The replacement controller receives the input callbacks, so it decides who gets them. Kitchen Sink
forwards to the private `FlyController` **unless** something else owns input:

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L35-L37
private bool InputBlocked => !ReferenceEquals(Program.InputViewport, Viewport)
    || Program.IsWindowOpen || Program.ConsoleWindow.IsOpen
    || ImGui.GetIO().WantCaptureKeyboard || ImGui.GetIO().WantTextInput;
```

- Another viewport has the mouse (`Program.InputViewport` is not this one), a modal window or the console is open, or ImGui
  wants the keyboard or text input: movement is released (`_fly.OnSwitchOff`, which clears held keys and look state)
  and the callbacks return false so the game's own handling continues.
- `OnKey` suspends seat switching on purpose; `OnScroll` scales the base speed by `2^(offset.Y/50)` so saves never compound the
  sprint key; `GetCursorMode`, `IsMouseDrag` and `CancelMouseDrag` forward so the engine hides the
  cursor while looking and the right-click part window does not open after a drag.

```csharp
// kitchen-sink.lib/UnlockedIvaController.cs L79-L106
public void ReleaseInput() => _fly.OnSwitchOff(CameraMode.IVA);

public override void OnSwitchOff(CameraMode nextMode) => IvaCameraUnlock.Disable(returnToSeat: false);

public override bool OnKey(GlfwKeyEvent keyEvent)
{
    if (InputBlocked) { ReleaseInput(); return false; }
    // Seat switching is intentionally suspended until Return to Seat.
    return _fly.OnKey(keyEvent);
}

public override bool OnMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    => !InputBlocked && _fly.OnMouseButton(window, button, action, mods);

public override bool OnCursorPos(GlfwWindow window, double2 pos)
    => !InputBlocked && _fly.OnCursorPos(window, pos);

public override bool OnScroll(GlfwWindow window, double2 offset)
{
    if (InputBlocked) return false;
    // Keep base speed independent of the native sprint key so saves never compound it.
    IvaCameraUnlock.Speed *= Math.Pow(2, Math.Clamp(offset.Y / 50, -20, 20));
    return true;
}

public override GlfwCursorMode GetCursorMode() => InputBlocked ? GlfwCursorMode.Normal : _fly.GetCursorMode();
public override bool IsMouseDrag() => _fly.IsMouseDrag();
public override void CancelMouseDrag() => _fly.CancelMouseDrag();
```

The validity check runs every frame from `OnFrame` and from the sub-mod's `Update`. If the followed vehicle,
seat, viewport or controller slot changed, it restores the original controller and, if the viewport
is still in IVA, sets `Orbit` mode so stock IVA never dereferences a stale seat or cycles the wrong viewport
([kitchen-sink.lib/IvaCameraUnlock.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.cs#L123-L132)).

## Complete example

- [kitchen-sink.lib/IvaCameraUnlock.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.cs): install, enable, disable, validity
- [kitchen-sink.lib/UnlockedIvaController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/UnlockedIvaController.cs): the subclass and the forwarding
- [kitchen-sink.lib/IvaCameraUnlock.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/kitchen-sink.lib/IvaCameraUnlock.Persistence.cs): scene save

## Variations and gotchas

- **Why not a Harmony prefix?** The slot is the supported seam: `GetActiveController` always asks the
  property, so nothing else needs patching. The only patch is a postfix on the private
  `IVASeat.IsCameraInThisSeat(IViewport)` so the kitten's head shows ([Let the IVA camera leave the seat and fly around the cabin](../../special-views/unlock-the-iva-camera/overview.md)).
- **Cost of a typed delegate** is a one-off `CreateDelegate`; compare the `MethodInfo.Invoke` in
  [approach 2](replace-a-viewport-camera-controller-2.md).
- **Failure unpatches everything.** A missing setter or method makes `Apply` roll back and surface
  `LastError`, which the panel shows.
- **Camera Controller Override** patches `FlyController.OnFrame` for every instance, including the
  private one here, so a playing sequence can take over the unlocked camera
  ([approach 4](../write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## See also

- [Topic overview](overview.md); [approach 1](replace-a-viewport-camera-controller-1.md), [2](replace-a-viewport-camera-controller-2.md), [4](replace-a-viewport-camera-controller-4.md)
- [Free-flying IVA camera by swapping the main viewport's IVA controller](../../../../mods/unscience/features/kitchen-sink-iva-camera-unlock.md)
- [Let the IVA camera leave the seat and fly around the cabin](../../special-views/unlock-the-iva-camera/overview.md), [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md), [Block game hotkeys while typing into an ImGui field](../../../ui/input/block-game-hotkeys-while-typing/overview.md), [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md)
