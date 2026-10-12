---
type: How-To
title: Park the viewport in Fixed through the Mode setter, suppress the entry alert and keep CameraRotation coherent
description: gatOS parks the main viewport directly through the reflected Mode setter, silences the Fixed camera alert for its own park and rewrites CameraOffset and CameraRotation after each pose so a fallback into base.OnFrame never sees a zero rotation
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
approach: 3
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/gatos/features/programmable-camera-director]
techniques: [controller-subclass, reflection]
hooks: [reflection KSA.ViewportBase.set_Mode, subclass KSA.FixedController.OnSwitchOn]
lifecycle: [frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.ViewportBase, KSA.CameraMode, KSA.Camera]
tags: [camera, fixed-camera, alert, mode-setter, gatos]
---
gatOS parks the main viewport in `Fixed` without going through the player's mode-switch path, so a take or
release neither raises the "Fixed camera" alert in the footage nor clears a guest's latched
control inputs. The controller then keeps the stock input fields coherent so that any fallback into `base.OnFrame` is safe.
Reflection and the controller install are in [the controller topic](../replace-a-viewport-camera-controller/overview.md)
([approach 2](../replace-a-viewport-camera-controller/replace-a-viewport-camera-controller-2.md)).

## Background: how the game does it

`SetCameraMode` calls the outgoing controller's `OnSwitchOff`, assigns `Mode`, calls the incoming
controller's `OnSwitchOn` and then `Program.ControlledVehicle?.ClearHeldPlayerInput()` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L312)).
`FixedController.OnSwitchOn` only raises `TimedAlert.Create(... AlertFixedCamera ...)` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L140-L143)). Writing
`Mode` directly skips all of it, but also skips Map's `NoRotation` bookkeeping and IVA's seat setup, which is why gatOS goes through
`SetCameraMode` when leaving Map.

## Lifecycle and timing

Parking happens in the director's update, called from the prefix on the main `GameViewport.OnFrame` (`frame:viewports`);
the controller's `OnFrame` that follows writes the pose in phase ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **Park directly, fall back to `SetCameraMode`** for Map or a missing setter:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L1003-L1007
private static void ParkMode(IGameViewport viewport)
{
    if (viewport.Mode == CameraMode.Map || !ViewportSeam.TrySetMode(viewport, CameraMode.Fixed))
        viewport.SetCameraMode(CameraMode.Fixed);
}
```

   where the setter is cached once ([gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs#L92-L109)).

2. **Silence the alert on the fallback path too.** The direct `Mode` write never calls `OnSwitchOn`, so it
   raises nothing. The `SetCameraMode(Fixed)` fallback (leaving Map, or a missing setter) does call it, so the
   override skips the base alert while a pose source is installed; a player who switches to `Fixed` by hand
   with no pose still sees it:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs L70-L74
public override void OnSwitchOn(CameraMode lastMode)
{
    if (Pose is null)
        base.OnSwitchOn(lastMode);
}
```

3. **After each pose, rewrite the stock input fields** from what was applied, so any fallback into `base.OnFrame`
   (a fault, the source cleared) continues from this pose instead of a stale or zero `CameraRotation`:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs L88-L122
{
    if (Pose is not { } source || Faulted)
    {
        base.OnFrame(inViewport, inDeltaTime);
        return;
    }

    if (Camera.Following is not { } following)
        return;

    try
    {
        var followedEcl = following.GetPositionEcl();
        if (!source.TryPose(Camera, followedEcl, inDeltaTime, out var offset, out var rotation))
            return; // hold: the camera keeps riding the followable on its previous offset

        Camera.PositionEcl = followedEcl + offset;
        Camera.LocalRotation = rotation;

        // Keep the stock input fields coherent with what was applied, so any fallback into
        // base.OnFrame (fault, source cleared) continues from this pose instead of a stale or —
        // fatally — zero CameraRotation.
        CameraOffset = offset;
        var forward = (-double3.UnitZ).Transform(rotation);
        if (forward.IsFinite() && !forward.IsNearlyZero())
            CameraRotation = forward;
    }
    catch (Exception ex)
    {
        // Engine loop: never rethrow. One error line, then hold the last pose until the director
        // notices the fault in the next prefix and hands the camera back.
        Faulted = true;
        ModLog.Log.Error($"gatOS camera: in-phase pose solve failed — {ex.Message}");
    }
}
```

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs), [gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/ViewportSeam.cs), [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L1003-L1007)

## Variations and gotchas

- **Held input survives.** A direct write does not clear the controlled vehicle's held input, which is the point for gatOS and a hazard for a mod that does not expect it.
- **Map and IVA are unsupported** by the director; leaving Map must run `MapController.OnSwitchOff`.
- **Faults keep the last pose** until the director hands the camera back; the controller never rethrows into the frame loop.
- **A stale `DockingConnector`** on the stock controller takes the docking branch in `OnFrame` and ignores the mod's fields, so gatOS carries it across when installing ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L24-L35)).

## See also

- [Topic overview](overview.md); [approach 1](drive-the-main-camera-in-fixed-mode-1.md), [2](drive-the-main-camera-in-fixed-mode-2.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)
- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)
