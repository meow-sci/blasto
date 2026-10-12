---
type: How-To
title: Fetch the protected setters once with GetSetMethod(nonPublic true) and park the viewport in Fixed
description: gatOS caches the non-public setters of GameViewport.FixedController and ViewportBase.Mode, installs its controller, parks the viewport in Fixed without SetCameraMode's side effects and restores both on release
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
approach: 2
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/gatos/features/programmable-camera-director]
techniques: [reflection, controller-subclass]
hooks: [reflection KSA.GameViewport.set_FixedController, reflection KSA.ViewportBase.set_Mode]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.GameViewport, KSA.ViewportBase, KSA.IGameViewport, KSA.FixedController, KSA.CameraMode]
tags: [camera, reflection, property-setter, fixed-camera, gatos]
---
Writing the compiler-generated backing field is one way past a protected setter; the more general
recipe is to fetch the setter itself. gatOS' `ViewportSeam` resolves the non-public setters of two
protected-set properties, `GameViewport.FixedController` and `ViewportBase.Mode`, once at type
initialisation and invokes them. The same recipe works for any `{ get; protected set; }` property in
the game. Prerequisites: StarMap, reflection.

## Background: how the game does it

Both members are auto-properties with protected setters ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L42), [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L36));
the public interface `IGameViewport` exposes only getters ([current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L8-L35)). Up to build 5348
`Viewport.FixedController` and `Mode` were public fields; the viewport rework in 5402 turned them into
properties ([Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)). The engine's own `SetCameraMode` does more than assign
`Mode`: it calls `OnSwitchOff` on the outgoing controller and `OnSwitchOn` on the incoming one and clears
the controlled vehicle's held input ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L312)). gatOS wants none of those side
effects, because clearing held input drops a guest's latched translate and rotate flags every time it
takes or releases the camera.

## Lifecycle and timing

Resolution happens in static initialisers (first use); the invocations run on the main thread from the
director, inside the command drain or the `GameViewport.OnFrame` prefix (`frame:viewports`,
[reference](../understand-camera-modes-and-controllers/overview.md)). The pose is written later, inside the
installed controller's `OnFrame`.

## Steps

1. **Fetch the setters with `GetSetMethod(nonPublic: true)` and cache them.** `BindingFlags` include
   non-public; a miss leaves a null:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs L30-L40
private const BindingFlags AnyInstance =
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

private static readonly MethodInfo? FixedControllerSetter =
    typeof(GameViewport).GetProperty(nameof(GameViewport.FixedController), AnyInstance)
        ?.GetSetMethod(nonPublic: true);

private static readonly MethodInfo? ModeSetter =
    typeof(ViewportBase).GetProperty(nameof(ViewportBase.Mode), AnyInstance)
        ?.GetSetMethod(nonPublic: true);
```

2. **Wrap each in a method that degrades.** The controller install returns false if the viewport is not a
   `GameViewport` or the setter is missing, logging once; callers treat that as "cannot own the camera":

```csharp
// gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs L60-L75
internal static bool TrySetFixedController(IGameViewport viewport, FixedController controller)
{
    if (viewport is not GameViewport target || FixedControllerSetter is null)
    {
        if (!_loggedFixedControllerMiss)
        {
            _loggedFixedControllerMiss = true;
            ModLog.Log.Warn("gatOS camera: GameViewport.FixedController setter not found in this "
                            + "build; the programmable camera cannot own the view.");
        }
        return false;
    }

    FixedControllerSetter.Invoke(target, [controller]);
    return true;
}
```

3. **Park the mode through the setter, with a fallback to `SetCameraMode`.** A direct write skips the
   switch-off/on pair; leaving Map mode must still go through `SetCameraMode`, because
   `MapController.OnSwitchOff` undoes `NoRotation` and restores vessel control:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs L92-L109
internal static bool TrySetMode(IViewport viewport, CameraMode mode)
{
    if (viewport.Mode == mode)
        return true;
    if (viewport is not ViewportBase target || ModeSetter is null)
    {
        if (!_loggedModeMiss)
        {
            _loggedModeMiss = true;
            ModLog.Log.Warn("gatOS camera: ViewportBase.Mode setter not found in this build; "
                            + "falling back to SetCameraMode (held thruster input is cleared on park).");
        }
        return false;
    }

    ModeSetter.Invoke(target, [mode]);
    return true;
}
```

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L1003-L1007
private static void ParkMode(IGameViewport viewport)
{
    if (viewport.Mode == CameraMode.Map || !ViewportSeam.TrySetMode(viewport, CameraMode.Fixed))
        viewport.SetCameraMode(CameraMode.Fixed);
}
```

4. **Install the controller, carrying the stock fields over, and remember the stock instance.** gatOS
   copies `CameraOffset`, `CameraRotation` and `DockingConnector` so a player mid-fixed-view loses nothing:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L479-L504
private CameraPoseController? EnsureController(IGameViewport viewport)
{
    if (_controller is { } installed && ReferenceEquals(viewport.FixedController, installed))
        return installed;

    try
    {
        var current = viewport.FixedController;
        var controller = new CameraPoseController(viewport.BaseCamera)
        {
            CameraOffset = current.CameraOffset,
            CameraRotation = current.CameraRotation,
            DockingConnector = current.DockingConnector,
        };
        if (!ViewportSeam.TrySetFixedController(viewport, controller))
            return null;
        _stockFixedController ??= current;
        _controller = controller;
        return controller;
    }
    catch (Exception ex)
    {
        ModLog.Log.Error($"gatOS camera: cannot install the fixed-controller seam — {ex.Message}");
        return null;
    }
}
```

5. **Silence the entry alert for your own park.** The stock `OnSwitchOn` raises a "Fixed camera" alert; the
   subclass skips it while a pose source is installed:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs L70-L74
public override void OnSwitchOn(CameraMode lastMode)
{
    if (Pose is null)
        base.OnSwitchOn(lastMode);
}
```

6. **Restore on shutdown**, only if the viewport still holds your controller ([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L453-L471)).

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs): the two setters
- [gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs): the `FixedController` subclass
- [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs): install, park, release

## Variations and gotchas

- **General recipe.** For any protected-set property: `type.GetProperty(name, Instance | Public | NonPublic)?.GetSetMethod(nonPublic: true)`,
  cached in a `static readonly MethodInfo?`, then `Invoke(target, [value])`. For hot paths prefer a typed delegate
  ([approach 3](overview.md), [article](replace-a-viewport-camera-controller-3.md)).
- **Verification.** Unlike [approach 1](replace-a-viewport-camera-controller-1.md), `ViewportSeam` does not
  read the property back after invoking; it relies on the setter being the one the constructor uses.
- **Direct `Mode` writes** skip `OnSwitchOn`: Map's `NoRotation` bookkeeping and IVA's seat setup are not run,
  which is why gatOS refuses to park from Map directly.
- **IVA and Map ownership are unsupported** by this director; their controllers overwrite their own contexts each frame.

## See also

- [Topic overview](overview.md); [approach 1](replace-a-viewport-camera-controller-1.md), [3](replace-a-viewport-camera-controller-3.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)
- [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)
