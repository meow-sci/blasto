---
type: Game Feature
title: Replace a viewport's camera controller and take input through it
description: How mods install a FixedController or IVAController subclass into a GameViewport despite the protected setters, and how a replacement controller receives and routes camera input
category: camera
group: controllers
topic: replace-a-viewport-camera-controller
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
  - mods/ksarmory/features/chase-camera-on-a-round
  - mods/gatos/features/programmable-camera-director
  - mods/unscience/features/kitchen-sink-iva-camera-unlock
  - mods/ksagolf/features/borrowed-chase-camera
lifecycle: [frame:viewports, startup:all-mods-loaded, frame:input, frame:controller]
game_types:
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.FixedController
  - KSA.IVAController
  - KSA.Controller
  - KSA.Program
  - Brutal.GlfwApi.GlfwCursorMode
  - Brutal.GlfwApi.GlfwMouseButton
tags: [camera, controller, reflection, input, fixed-camera, iva]
---
A `GameViewport` asks one controller per camera mode what to do. The five controller properties are
`{ get; protected set; }`, and the interface `IGameViewport` is get-only, so a mod that wants different
behaviour (a levelled horizon, a free-flying IVA, mouse look) has to write them by reflection. Once it has,
the replacement receives the same callbacks the stock controller did: `OnFrame`, `OnKey`, `OnMouseButton`,
`OnCursorPos`, `OnScroll`, `GetCursorMode` and `IsMouseDrag`. The six approaches cover **how to install**
(1 to 3) and **how to take input** (4 to 6).

## Engine background

| Type | What matters | Source |
|---|---|---|
| `GameViewport` | `FlyController`, `OrbitController`, `MapController`, `IvaController`, `FixedController`, all `{ get; protected set; }`; `GetActiveController()` returns the one for `Mode` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L36-L42), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L295) |
| `ViewportBase.Mode` | `{ get; protected set; }`; `SetCameraMode` runs `OnSwitchOff`/`OnSwitchOn` and clears held vehicle input | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L36), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L312) |
| `Controller` | The virtual callbacks a subclass overrides | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L22-L94) |
| `Program.OnMouseButton`, `OnCursorPos`, `OnScroll`, `DispatchKeyEvent` | Route input to `InputViewport.GetActiveController()` after ImGui, vehicles and the camera had their say | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1775), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2125) |
| `Vehicle.OnMouseButton` | Asks the active controller `IsMouseDrag()` before a right-release opens a part window | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3667-L3681) |
| `OrbitController` | The drag, threshold and wheel rules that replacements copy | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L157-L188), [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L354-L431) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Write the `FixedController` backing field, check, restore](replace-a-viewport-camera-controller-1.md) | KSAGolf, KSAStructures, KSArmory | `reflection` | `frame:draw-ui`, `frame:viewports` | Simple and checked, but depends on a compiler-generated name |
| [2. Cache the protected setters with `GetSetMethod(true)`](replace-a-viewport-camera-controller-2.md) | gatOS | `reflection` | `frame:viewports` | General recipe for any protected-set property; also parks `Mode` without `SetCameraMode` side effects; no read-back check |
| [3. Typed open-instance delegate on `IvaController`; forward input to a `FlyController`](replace-a-viewport-camera-controller-3.md) | unscience (Kitchen Sink) | `reflection` | `startup:all-mods-loaded`, `frame:input`, `frame:viewports` | Fast calls and clean failure, but a whole controller to maintain |
| [4. Right-drag look with orbit constants, `IsMouseDrag`](replace-a-viewport-camera-controller-4.md) | KSAStructures | `controller-subclass` | `frame:input`, `frame:controller` | Feels stock; a release over an ImGui panel is lost |
| [5. Orbit drag, swing and builder input](replace-a-viewport-camera-controller-5.md) | KSAGolf | `controller-subclass` | `frame:input`, `frame:controller` | One class serves three input modes; the pose source decides |
| [6. Match the orbit drag feel](replace-a-viewport-camera-controller-6.md) | KSAGolf | `camera-arbitration` | `frame:input`, `frame:viewports` | Pure and testable; only the constants are wired into the game at the pinned ref |

## See also

- [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../understand-camera-modes-and-controllers/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md), [Let the IVA camera leave the seat and fly around the cabin](../../special-views/unlock-the-iva-camera/overview.md)
- [Reading, writing and calling private game members](../../../patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md), [Block game hotkeys while typing into an ImGui field](../../../ui/input/block-game-hotkeys-while-typing/overview.md), [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md), [Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking](../../special-views/first-person-camera-for-a-kitten/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
