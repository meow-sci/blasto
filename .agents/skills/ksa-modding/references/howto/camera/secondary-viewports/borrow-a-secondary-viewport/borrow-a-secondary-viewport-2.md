---
type: How-To
title: Claim a viewport per camera with an IViewportOwner, track the lease every update and keep KSA's window
description: Hot Pursuit gives each camera its own owner token for TryClaimSecondaryViewport, re-applies name, size, field of view, follow and visibility after every claim, polls TryGetOwned and shows the picture in KSA's resizable window
category: camera
group: secondary-viewports
topic: borrow-a-secondary-viewport
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/hot-pursuit-part-mounted-cameras]
techniques: [viewport-lease]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:viewports, event:save, event:load]
game_types: [KSA.ViewportRegistry, KSA.IGameViewport, KSA.IViewportOwner, KSA.GameViewport, KSA.ViewportBase]
tags: [camera, secondary-viewport, lease, owner, hot-pursuit]
---
Hot Pursuit puts a camera on any part and shows it in KSA's own camera window. Each camera keeps a stable claim on one slot through an owner token, so it can ask "is this still mine?" every frame and notice the player closing the window. This article shows the keep-KSA's-window way of showing the camera in a window: set the name, resize once, set visibility, and let KSA draw. Prerequisites: StarMap; the pose is [approach 3](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md).

## Background: how the game does it

```csharp
// current/decomp/KSA/ViewportRegistry.cs L185-L198
public static bool TryClaimSecondaryViewport(IViewportOwner inOwner, [NotNullWhen(true)] out IGameViewport? outViewport)
{
	ArgumentNullException.ThrowIfNull(inOwner, "inOwner");
	if (OwnedSecondaryViewports.TryGetValue(inOwner, out outViewport))
	{
		return true;
	}
	if (!TryLeaseSecondaryViewport(out outViewport))
	{
		return false;
	}
	OwnedSecondaryViewports.Add(inOwner, outViewport);
	return true;
}
```

```csharp
// current/decomp/KSA/IViewportOwner.cs L1-L6
namespace KSA;

public interface IViewportOwner
{
}
```

`IViewportOwner` is an empty marker interface. The registry keys ownership by **reference** and allows one viewport per owner, so each camera needs its own owner object ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L28-L30)). `TryGetOwned` answers whether that owner still holds a viewport ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L250-L258)).
The stock window's close button calls `ReleaseSecondaryViewport(this)`, so the lease is lost without the mod being told ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L193-L215)). Viewport size changes only through `RequestResize`, which is refused unless resizing is allowed ([current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L67-L75)).

## Lifecycle and timing

| Work | Where | Value |
|---|---|---|
| Claim, configure, `SyncLease` | `Update` from `StarMapBeforeGui` (or the hidden-HUD replay) | `frame:draw-ui` |
| Pose | Prefix on `FixedController.OnFrame` | `frame:viewports` |
| Stock window drawn; closing it releases the lease | `Program.OnDrawUiViewports` | `frame:draw-ui` |
| Restore reclaims slots | Scene load, order 180 | `event:load` |

## Steps

1. **Give every camera its own owner token:**

```csharp
// hot-pursuit.lib/HotPursuitCamera.cs L59-L63

/// <summary>Reference-identity token used to claim exactly one registry slot.</summary>
public sealed class CameraViewportOwner : IViewportOwner
{
}
```

2. **Claim and configure.** A claim runs `ResetToDefaults`, so everything is re-applied after it: name, `Fixed` mode, a resize with resizing allowed only around the call, field of view, the follow, visibility ([approach 5](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md)):

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L147-L164
internal bool TryOpenViewport(HotPursuitCamera entry)
{
    try
    {
        if (!ViewportRegistry.TryClaimSecondaryViewport(entry.Owner, out var viewport))
            return false;
        entry.Viewport = viewport;
        entry.LeaseLost = false;
        ConfigureViewport(entry, viewport);
        return true;
    }
    catch (Exception ex)
    {
        ReleaseLease(entry);
        Console.WriteLine($"hot-pursuit: failed to claim viewport for camera #{entry.Id}: {ex.Message}");
        return false;
    }
}
```

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

3. **Poll the lease every update, and again inside the pose prefix.** A viewport the registry no longer attributes to the owner is dropped; the entry shows "Reopen viewport" instead of keeping a stale reference:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L174-L203
private void SyncLease(HotPursuitCamera entry)
{
    try
    {
        if (ViewportRegistry.TryGetOwned(entry.Owner, out var viewport))
        {
            if (!ReferenceEquals(entry.Viewport, viewport))
            {
                entry.Viewport = viewport;
                entry.LeaseLost = false;
                ConfigureViewport(entry, viewport);
            }
            return;
        }

        if (entry.Viewport != null)
        {
            // GameViewport.DrawImGui releases the registry lease when the stock window closes.
            // Do not retain or use that stale reference.
            entry.Viewport = null;
            entry.LeaseLost = true;
        }
    }
    catch (Exception ex)
    {
        entry.Viewport = null;
        entry.LeaseLost = true;
        Console.WriteLine($"hot-pursuit: viewport lease check failed for camera #{entry.Id}: {ex.Message}");
    }
}
```

4. **Release on remove, reset and dispose:**

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L225-L236
private static void ReleaseLease(HotPursuitCamera entry)
{
    try
    {
        ViewportRegistry.ReleaseSecondaryViewport(entry.Owner);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"hot-pursuit: failed to release camera #{entry.Id} viewport: {ex.Message}");
    }
    entry.Viewport = null;
}
```

5. **Restore on load** by reclaiming slots for cameras that were open; if none is free the camera is restored without a viewport and a warning tells the player to reopen it ([hot-pursuit.lib/HotPursuitSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Saves.cs#L55-L70)).

## Complete example

- [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L147-L236), [hot-pursuit.lib/HotPursuitCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCamera.cs), [hot-pursuit.lib/HotPursuitSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Ui.cs)

## Variations and gotchas

- **Four cameras at most**, minus any slot used by the Add Camera action, docking cameras or other mods. The panel shows `AvailableSecondaryCount`.
- **`SetResizeAllowed(true)` only around `RequestResize`**: the viewport is otherwise not resizable, and every size is a new set of render targets.
- **Feeds are full extra scene renders**; each visible camera costs roughly a reduced main view, and the reduced pipeline has no ocean, clouds, particles, exhaust or shadows ([Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md)).
- **Closing the stock window releases the lease** and the entry reports it; nothing resets the camera mode when it closes.

## See also

- [Topic overview](overview.md); [approach 1](borrow-a-secondary-viewport-1.md), [3](borrow-a-secondary-viewport-3.md)
- [Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md)
- [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../aim-a-secondary-viewport-camera/overview.md), [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md)
