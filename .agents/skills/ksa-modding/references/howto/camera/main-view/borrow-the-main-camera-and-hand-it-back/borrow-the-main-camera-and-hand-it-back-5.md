---
type: How-To
title: Rank several camera users with a pure ViewClaim ladder and drive the claimants in claim order
description: KSArmory's chase, sight, camera windows, watch camera and developer bridge share the main view through a headless-tested ViewClaim ladder where a loser yields without restoring and the bridge's held pose is just another claimant
category: camera
group: main-view
topic: borrow-the-main-camera-and-hand-it-back
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/borrow-main-view-camera, mods/ksarmory/features/file-command-bridge-and-mcp]
techniques: [view-claim-ladder, state-record-and-restore]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials]
game_types: [KSA.Program, KSA.Camera, KSA.CameraMode, KSA.GameViewport, KSA.IFollowable]
tags: [camera, arbitration, claim-ladder, borrow, stand-down, yield]
---
When several features (and, by extension, several mods) want the one main view, the question is not how to take it but who wins and what the loser does. KSArmory has a chase camera, a gunner's sight, camera windows, a
watch camera and a developer bridge that can hold a pose. This article covers **arbitrating between features and mods**: a pure `ViewClaim` ladder in the mod core and a driver that calls the claimants in claim order.

## Background: how the game does it

The engine arbitrates nothing: each viewport has one mode and one follow target, and the last writer wins. `SetFollow` and `SetCameraMode` take effect immediately, and `Camera.Following`
can be read back and compared by identity ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L312)). Mod arbitration therefore has to be done with the mod's own state: what mode did I set, and what did I point the view at?

## Lifecycle and timing

`DriveCameras` runs once per step from `StepOnce` (the GUI hook, or the hidden-HUD prefix on `Program.OnFrameCelestials`; `frame:draw-ui`, `frame:celestials`). The claimants are written in a fixed order each step; a mode change takes effect in the next
`frame:viewports`, while a `SetFollow` moves the camera at once (see [approach 1](borrow-the-main-camera-and-hand-it-back-1.md)) ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **State the actions.** Idle, Take, Hold, GiveBack, Yield, StandDown, with the semantics documented on the enum (Yield keeps the recording, StandDown forgets it):

```csharp
// src/KSArmory/Sim/ViewClaim.cs L4-L33
public enum ViewAction
{
    /// <summary>Not holding it and not wanting it. Touch nothing.</summary>
    Idle,

    /// <summary>Take it: record what it was doing first, then drive it.</summary>
    Take,

    /// <summary>Already holding it and still entitled to. Drive it again.</summary>
    Hold,

    /// <summary>Holding it and no longer entitled to. Put back what was recorded.</summary>
    GiveBack,

    /// <summary>
    /// Something with a stronger claim is driving it. Write nothing, and keep the recording: it is
    /// the only way back to what the player was doing before any of this started.
    /// </summary>
    Yield,

    /// <summary>
    /// Holding it on paper, but the player has taken it back by hand. Forget the recording without
    /// putting the mode back — they chose that.
    ///
    /// <para>The caller must also stop <em>asking</em> for the view. Standing down alone leaves
    /// the setting requesting it, and the next frame is a plain <see cref="Take"/>: the view
    /// blinks back to the borrower and reads as the mod refusing to let go.</para>
    /// </summary>
    StandDown,
}
```

2. **Order the rungs**: stand down first (restoring over a view the player reclaimed drags them off what they turned to), then yield (so a borrower that is no longer wanted still waits for the stronger claim), then hold or give back, and take only if nothing outranks it:

```csharp
// src/KSArmory/Sim/ViewClaim.cs L77-L87
public static ViewAction ForOptic(bool wanted, bool resolved, bool outranked,
                                  bool holding, bool stillOurs)
{
    if (holding && !stillOurs) return ViewAction.StandDown;
    if (holding && outranked) return ViewAction.Yield;
    if (holding) return wanted && resolved ? ViewAction.Hold : ViewAction.GiveBack;

    // Never take it out from under a stronger claim. Waiting rather than queueing: the chase
    // gives the view back on its own, and the next frame's Take picks it up.
    return wanted && resolved && !outranked ? ViewAction.Take : ViewAction.Idle;
}
```

3. **Define "still ours" from both halves**, and treat neither as evidence while outranked, because the stronger claim sets both itself:

```csharp
// src/KSArmory/Sim/ViewClaim.cs L113-L114
public static bool StillOurs(bool inTakenMode, bool followsWhatWePointedAt, bool outranked)
    => inTakenMode && (outranked || followsWhatWePointedAt);
```

4. **Drive the claimants in claim order every step.** The watch camera, then the bridge, then the chase (which takes the view outright), then the camera windows, and last the sight, which is told whether the chase holds the view:

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L433-L438, L446-L468, L476-L485
private void DriveCameras(double dt)
{
    if (_roster is null || _ui is null) return;

    _watch.Apply(dt);
    _bridge?.DriveCamera();
    // ...
    if (_chase.RiddenSystem is { Platform: null } riding)
    {
        _chase.Apply(riding, enabled: true, dt, _lastSimStep, _config.FreezeChaseTransition,
                     _sight.BaseFovDeg);
    }
    else if (_roster.For(_ui.Focused) is { } selected && _roster.LastReleasing(selected) is var chased)
    {
        _chase.Apply(chased.Weapon, chased.Policy.ChaseRounds && KsaWorld.InFlightScene,
                     dt, _lastSimStep, _config.FreezeChaseTransition, _sight.BaseFovDeg);
    }
    else
    {
        _chase.Release();
    }

    DriveCameraWindows(dt);

    if (KsaWorld.InFlight && _heads?.Driving(_ui.Focused) is { } head)
    {
        TakeMainView(head.Head, head.Policy);
    }
    else if (KsaWorld.InFlightScene)
    {
        // ...
        _sight.Release();
    }
    else
    {
        // Out of flight the recording describes a scene that no longer exists, and restoring
        // a dead scene's camera mode and follow onto the editor is a view the player cannot
        // account for. The new scene brings its own camera.
        _sight.Forget();
    }
}
```

5. **Let the loser yield, not restore.** When the chase takes the view from the sight, the sight keeps its recording; the chase records the sight's pose, and handing the chase back returns the view to the sight. Swapping the order would fly a chase transition through the sight's magnified field.

6. **Tie annotations to the same permission.** `SightPaints` makes the overlay follow the claim so a yielding sight does not paint over a picture showing something else:

```csharp
// src/KSArmory/Sim/ViewClaim.cs L131-L137
public static bool SightPaints(bool opticSelected, bool onMainView, bool holding, bool outranked)
{
    if (!opticSelected) return false;
    if (!onMainView) return true;

    return holding && !outranked;
}
```

7. **The bridge's held pose is a claimant**, written in claim order like the others ([src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L198-L216)).

## Complete example

- [src/KSArmory/Sim/ViewClaim.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ViewClaim.cs), [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L433-L485)
- [src/KSArmory/Ksa/SightCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SightCamera.cs), [src/KSArmory/Ksa/ChaseCamera.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ChaseCamera.cs), [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs)

## Variations and gotchas

- **Across mods** nothing like this exists in the game: the second writer must detect the first (compare the follow target, the mode or the controller instance, as gatOS does in [approach 4](borrow-the-main-camera-and-hand-it-back-4.md)) and stand down rather than fight.
- **Waiting, not queueing.** A claim that loses waits; the winner gives the view back on its own and the next step's `Take` picks it up.
- **Order is the whole content.** Two rungs are load-bearing for reasons not visible from the rung; reordering them undoes a takeover made this frame.
- **A secondary camera window is never contested**; only the main view needs the ladder.

## See also

- [Topic overview](overview.md); [approach 1](borrow-the-main-camera-and-hand-it-back-1.md), [2](borrow-the-main-camera-and-hand-it-back-2.md), [4](borrow-the-main-camera-and-hand-it-back-4.md)
- [Borrow the player's main view and hand it back](../../../../mods/ksarmory/features/borrow-main-view-camera.md), [Drive a running game from outside through a file command bridge and an MCP server](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
- [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md)
