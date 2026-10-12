---
type: How-To
title: Install a FixedController subclass by writing the auto-property backing field, check it took and restore on unload
description: GameViewport.FixedController has a protected setter, so KSAGolf, KSAStructures and KSArmory write its compiler-generated backing field by reflection, read the property back to prove the write and put the stock controller back from StarMapUnload
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
  - mods/ksarmory/features/chase-camera-on-a-round
techniques: [reflection, controller-subclass, starmap-lifecycle]
hooks:
  - reflection KSA.GameViewport.<FixedController>k__BackingField
  - subclass KSA.FixedController
  - starmap StarMapUnload
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.GameViewport, KSA.IGameViewport, KSA.FixedController, KSA.Controller, KSA.Program]
tags: [camera, controller, reflection, backing-field, fixed-camera]
---
A mod that wants its own `Fixed`-mode behaviour (an explicit up vector, input, a pose asked inside the
viewport pass) subclasses `FixedController` and has to put the instance into the main viewport.
`IGameViewport.FixedController` is get-only and `GameViewport.FixedController` is an auto-property with a
protected setter, so mods have to reach the compiler-generated backing field. KSAGolf, KSAStructures and
KSArmory share one pattern: look the field up once, write it, **read the property back**, keep the stock
controller, restore it on unload. Prerequisites: StarMap and reflection; the subclass itself is covered in
[Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md).

## Background: how the game does it

```csharp
// current/decomp/KSA/GameViewport.cs L36-L42
public OrbitController OrbitController { get; protected set; }

public MapController MapController { get; protected set; }

public IVAController IvaController { get; protected set; }

public FixedController FixedController { get; protected set; }
```

`GetActiveController()` returns `FixedController` for `CameraMode.Fixed` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L295)), so
once the property returns your instance, the engine calls it whenever that viewport is in `Fixed`. The
engine assigns the property only in the `GameViewport` constructor ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L87-L104)) and
`ResetToDefaults` re-initialises its `CameraOffset`, `CameraRotation` and `DockingConnector` fields, not the
instance, so nothing contends for the slot. `FixedController` is public, unsealed, has a public
constructor taking a `Camera`, and its `OnFrame` and the `Controller` input methods are virtual
([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L7-L22), [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L22-L94)).

## Lifecycle and timing

The installation runs on the main thread, lazily, the first time a borrower takes the view (the GUI hook,
`frame:draw-ui`), not at load; the controller then runs in `frame:viewports` for every frame the viewport
is in `Fixed`. Installing from `[StarMapAllModsLoaded]` would be too early: that hook is a postfix on `ModLibrary.LoadAll`,
which runs before `Program` builds the viewports ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L941-L962)), and `ViewportRegistry.MainViewport` throws until then
([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L50); [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). Restoration runs from `[StarMapUnload]`.

## Steps

1. **Look the field up once, warn once, degrade quietly.** The name is compiler-generated, so it is the
   one thing the mod cannot compile-check. A miss leaves the stock controller in place:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1709-L1724
private static FieldInfo? FixedControllerField(IGameViewport viewport)
{
    if (_lookedForFixedControllerField) return _fixedControllerField;
    _lookedForFixedControllerField = true;

    _fixedControllerField = viewport.GetType().GetField(
        "<FixedController>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

    if (_fixedControllerField is null)
    {
        Log.Warn("camera: no FixedController backing field on "
                 + $"{viewport.GetType().Name} - keeping KSA's own controller, so the horizon "
                 + "is not levelled and a driven view's aim lags a frame. If the property has a "
                 + "public setter again, use it and delete this.");
    }
```

2. **Construct the subclass on the viewport's `BaseCamera`**, remember the stock controller, write the
   field, then check with the get-only property that the write took. A silent no-op would look exactly
   like success because *some* controller is always found:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1734-L1766
private static FixedController? LevelTheHorizon(IGameViewport viewport)
{
    if (viewport.FixedController is LevelHorizonController already) return already;

    try
    {
        if (viewport.BaseCamera is not { } camera) return viewport.FixedController;
        if (FixedControllerField(viewport) is not { } field) return viewport.FixedController;

        var level = new LevelHorizonController(camera);

        _stockFixedController ??= viewport.FixedController;
        field.SetValue(viewport, level);

        // The write is the whole point, and a silent no-op would read exactly like a working
        // swap: everything downstream still finds *a* controller.
        if (!ReferenceEquals(viewport.FixedController, level))
        {
            Log.Warn("camera: the FixedController write did not take - keeping KSA's own");
            _stockFixedController = null;
            return viewport.FixedController;
        }

        Log.Info("camera: levelling the horizon on the main view");

        return level;
    }
    catch (Exception e)
    {
        Log.Warn($"could not level the horizon, using KSA's own controller: {e.Message}");
        return viewport.FixedController;
    }
}
```

3. **Write the controller's inputs before switching mode.** The install is performed from the single
   entry every borrower uses, which then sets `CameraRotation` and `CameraOffset` before
   `SetCameraMode(Fixed)` ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1821-L1874); the reason is in
   [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md)).

4. **Restore the stock controller on unload**, tolerating failure:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L1769-L1790
public static void RestoreStockController()
{
    if (_stockFixedController is not { } stock) return;

    try
    {
        if (Program.MainViewport is { } viewport
            && FixedControllerField(viewport) is { } field)
        {
            field.SetValue(viewport, stock);
        }
        Log.Info("camera: gave the fixed controller back");
    }
    catch (Exception e)
    {
        Log.Warn($"could not restore the fixed controller: {e.Message}");
    }
    finally
    {
        _stockFixedController = null;
    }
}
```

   and call it from `[StarMapUnload]` after the borrowers are released ([src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L191-L204)).

## Complete example

| Mod | Install | Restore |
|---|---|---|
| KSAGolf | [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1734-L1766) | [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1769-L1790) |
| KSAStructures | [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L608-L640) | [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L643-L664) |
| KSArmory | [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5435-L5467) | [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5470-L5491) |

The subclass is [src/KSAGolf/Ksa/LevelHorizonController.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/LevelHorizonController.cs#L39-L191).

## Variations and gotchas

- **Restore is mostly cosmetic.** `StarMapCore.DeInit` unpatches Harmony and then runs every
  `[StarMapUnload]` ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L28-L35),
  [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L142-L151)); the KSAGolf feature page records that StarMap calls it only at
  process exit, so the subclass in practice stays installed for the whole session. Write it so a
  controller with no pose and no up behaves exactly like stock (`base.OnFrame`).
- **Fragile name.** If KSA gives the property a public setter or renames it, the lookup returns null and
  the mod keeps the stock controller; gatOS' answer to the same problem is
  [approach 2](overview.md) ([article](replace-a-viewport-camera-controller-2.md)).
- **Other writers.** A second mod doing the same swap replaces yours or yours replaces theirs; the
  second writer can detect it by comparing the property to its own instance each frame
  ([Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)).
- **Only the main viewport.** `Program.MainViewport` is a `GameViewport` here; `IGameViewport` is the declared type, so guard
  the `GetType().GetField` lookup against other implementations.

## See also

- [Topic overview](overview.md); [approach 2](replace-a-viewport-camera-controller-2.md), [3](replace-a-viewport-camera-controller-3.md), [4](replace-a-viewport-camera-controller-4.md), [5](replace-a-viewport-camera-controller-5.md), [6](replace-a-viewport-camera-controller-6.md)
- [Level-horizon camera controller swapped into the main viewport](../../../../mods/ksagolf/features/level-horizon-camera-controller.md), [A replacement fixed camera controller with an up vector and an in-phase pose](../../../../mods/ksastructures/features/level-horizon-camera-controller.md), [Chase camera riding a mod-simulated round](../../../../mods/ksarmory/features/chase-camera-on-a-round.md)
- [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md)
