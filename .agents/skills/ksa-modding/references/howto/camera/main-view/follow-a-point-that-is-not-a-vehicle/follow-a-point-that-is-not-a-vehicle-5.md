---
type: How-To
title: Re-apply the follow after every viewport claim on a secondary camera
description: Hot Pursuit repeats SetFollow with changeControl and alert false after each secondary viewport claim, because a claim resets the viewport to defaults, and unfollows when its target is gone
category: camera
group: main-view
topic: follow-a-point-that-is-not-a-vehicle
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/hot-pursuit-part-mounted-cameras]
techniques: [viewport-lease]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Camera, KSA.IGameViewport, KSA.ViewportRegistry, KSA.Vehicle]
tags: [camera, follow, secondary-viewport, lease, reset]
---
A secondary camera follows nothing unless told to, and a freshly claimed viewport has been reset. Hot Pursuit therefore sets the follow again whenever its camera does not follow its vehicle: on claim, on update, and when the
lease was lost and regained. This article covers **changing the follow target without a jump** on secondary viewports. Leasing is in [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md).

## Background: how the game does it

`ViewportRegistry.TryClaimSecondaryViewport` leases the first free secondary and calls `ResetToDefaults` on it; `ReleaseSecondaryViewport` hides and resets it again
([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L200-L215), [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L226-L248)). `ResetToDefaults` returns the mode to `Orbit`, zeroes the Fixed offsets and calls
`OrbitController.EnsureFollowTarget`, which follows the world sun when nothing is followed ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L85), [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L725-L735)), so whatever a mod set before the claim is gone. `SetFollow(vehicle, tidalLocking, changeControl: false, alert: false)` is safe for a secondary camera because `changeControl: false` leaves the player's controlled vehicle alone
([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L618)).

## Lifecycle and timing

Configuration runs in `ConfigureViewport` on claim and from `Update` (`frame:draw-ui`, `StarMapBeforeGui` or the hidden-HUD replay); the camera pose is then rewritten by the prefix on `FixedController.OnFrame` in the next viewport pass
(`frame:viewports`, [approach 3](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)). Because the pose is rewritten in phase every frame, `SetFollow`'s teleport can last only until the next viewport pass; it is re-applied rarely (on a claim or when the follow was lost), so at most one frame of the feed is drawn from the teleported position.

## Change the follow target without a jump

1. **Re-apply everything after a claim**, including the mode and the follow:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L205-L223
private void ConfigureViewport(HotPursuitCamera entry, IGameViewport viewport)
{
    viewport.SetName($"Hot Pursuit Camera {entry.Id}");
    viewport.SetCameraMode(CameraMode.Fixed);
    viewport.SetResizeAllowed(true);
    viewport.RequestResize(new int2(entry.Width, entry.Height));
    viewport.SetResizeAllowed(false);
    viewport.BaseCamera.SetFieldOfView(entry.FieldOfView);
    if (entry.IsResolved)
    {
        viewport.BaseCamera.SetFollow(entry.Vehicle!, tidalLocking: true,
            changeControl: false, alert: false);
        viewport.SetVisible(entry.Visible);
    }
    else
    {
        viewport.SetVisible(false);
    }
}
```

2. **Keep it true each update**: follow only if the camera does not already follow the vehicle, set the field of view and visibility, and unfollow when the target cannot be resolved:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L33-L59
public void Update(double dt)
{
    foreach (var entry in _cameras)
    {
        ResolveTarget(entry);
        SyncLease(entry);
        if (entry.Viewport == null)
            continue;

        var camera = entry.Viewport.BaseCamera;
        if (!entry.IsResolved)
        {
            if (camera.Following != null)
                camera.Unfollow(changeControl: false);
            entry.Viewport.SetVisible(false);
            continue;
        }

        if (camera.Following != entry.Vehicle)
        {
            camera.SetFollow(entry.Vehicle!, tidalLocking: true,
                changeControl: false, alert: false);
        }
        camera.SetFieldOfView(entry.FieldOfView);
        entry.Viewport.SetVisible(entry.Visible);
    }
}
```

3. **Use `changeControl: false, alert: false`** for every call, as the default would steal control to the followed vessel for each camera.

4. **Check the lease before you configure**; a stock window close releases it ([Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md)).

## Complete example

- [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L33-L59), [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L147-L164), [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L205-L223)

## Variations and gotchas

- **Teleport is overwritten by the pose prefix**, not undone: the prefix rewrites position every frame from the next viewport pass on, so `SetFollow` can be called without restoring.
- **`tidalLocking: true`** is passed; the engine stores it, saves it and shows it in the View menu as "Follow Terrain", and callers pass it through to `SetFollow`; no code in `KSA/*.cs` was found that reads it to change the camera pose in this build ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3709-L3712)).
- **The follow is on the leased camera only** (`entry.Viewport.BaseCamera`), never on the main viewport.

## See also

- [Topic overview](overview.md); [approach 3](follow-a-point-that-is-not-a-vehicle-3.md), [4](follow-a-point-that-is-not-a-vehicle-4.md)
- [Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md), [Mount a camera on a vessel part and look through it](../../secondary-viewports/mount-a-camera-on-a-part/overview.md)
