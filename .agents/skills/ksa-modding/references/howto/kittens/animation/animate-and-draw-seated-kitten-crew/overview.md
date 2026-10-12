---
type: Game Feature
title: Animate seated kittens, draw them on unflown craft and fly another craft from a chair
description: How a seat builds and draws its own KittenRenderable, only for the controlled vehicle, and three mod techniques for posing a seated kitten, drawing crew of craft nobody is flying and sitting in a chair to fly a different craft
category: kittens
group: animation
topic: animate-and-draw-seated-kitten-crew
demonstrated_by:
  - mods/ksacars/features/driver-hands-steering-ik
  - mods/ksacars/features/seats-and-eva-doors
  - mods/ksacars/features/seated-crew-of-unflown-craft
  - mods/ksastructures/features/computer-flies-nearest-craft
  - mods/ksastructures/features/office-furniture-craft
lifecycle: [frame:draw-ui, frame:render, startup:all-mods-loaded, frame:viewports, event:key]
game_types:
  - KSA.IVASeat
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.IAnimProcessor
  - KSA.AnimatedRenderable
  - KSA.Vehicle
  - KSA.Program
  - KSA.IViewport
  - KSA.ViewportType
  - KSA.KittenEva
  - KSA.EVADoor
  - KSA.ViewportRegistry
  - KSA.GameViewport
  - KSA.Camera
tags: [kitten, seat, iva, crew, anim-processor, harmony-postfix, control]
---
A kitten in a seat is a different object from an EVA kitten. It belongs to an `IVASeat` module on a part, it is drawn by a `KittenRenderable` that the seat builds (with `seated: true`) when a kitten is assigned, and the engine draws seated crew only for the vehicle the player controls. These three facts shape what a mod can do: attach a pose processor to the seat's renderable (and re-attach when the seat rebuilds it), draw the crew of craft that are not controlled, or exploit "controlled vehicle" itself so that sitting in a chair becomes flying something else.

## Engine background

| Piece | Behaviour | Source |
|---|---|---|
| `IVASeat` | Part module with `PositionAsmb` (the kitten's eye), `ForwardAxisAsmb`, `UpAxisAsmb`, `AssignedKittenHash`; public `Renderable`; `UpdateSeatedRenderable` creates `new KittenRenderable(character, seated: true)` when the assigned kitten changes and draws it | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L40-L105) |
| `Vehicle.UpdateSeatedCrewRenderData` | Draws every seat in `Crew` **only when `this == Program.ControlledVehicle`** | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741) |
| `Program.RenderGame`, `RenderViewport` | Call it after `ClearBuckets`, once per viewport, in the same loop that draws EVA kittens | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4481-L4496), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4687-L4703) |
| `KittenRenderable` (seated) | Loads the seated idle clip and idle actions, disables the MMU and helmet, sets `HideHead` per view from `IsCameraInThisSeat` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L136-L244) |
| `KittenLocomotionTuning.SeatedOffset` | `(0, 0, -0.63)`: the seated model origin sits 0.63 m below the seat's eye along its up axis | [current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L145-L149) |
| `EVADoor`, `KittenEva.BoardVehicle` | A door aligned with a seat; boarding adds the kitten to the aligned seat, disposes the EVA kitten, makes the camera follow the host and sets `Program.ControlledVehicle = host` | [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L33-L61), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L925-L965) |
| `Program.ControlledVehicle` | The setter only clears the old vehicle's held input; keys reach it through `DispatchKeyEvent` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L514), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1992-L2000) |

Content creators do not need code for seats: a part declares `<IVASeat>` and a hidden subpart declares `<EVADoor SeatId="..."/>` ([add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md)). The mod techniques below start from a vehicle that already has working seats.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Per-frame attach check, processor on `CharacterModel.AnimProcessors`, reflection nulling the idle actions](animate-and-draw-seated-kitten-crew-1.md) | KSACars | `two-bone-ik` | `frame:draw-ui`, `frame:render` | Re-attach whenever the seat builds a new renderable; any exception switches the solve off |
| [2. Harmony postfix with its own id, skipping character portrait viewports](animate-and-draw-seated-kitten-crew-2.md) | KSACars | `harmony-postfix` | `startup:all-mods-loaded`, `frame:render` | The only point where submitting a skinned kitten lands in the current buckets; piggybacks on the controlled vehicle's call |
| [3. Seated means the flown craft is the chair; swap `ControlledVehicle` and restore on log-off](animate-and-draw-seated-kitten-crew-3.md) | KSAStructures | `viewport-borrow` | `frame:draw-ui`, `frame:viewports`, `event:key` | Chair and door built in code, camera borrowed and handed back; many ways to leave the borrowed state |

## See also

- [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../pose-kitten-bones-with-an-anim-processor/overview.md) ([two-bone IK](../pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-4.md))
- [Reach a kitten's avatar, skeleton, bones and animation catalog](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md) ([draw path](../../characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md))
- [Add crew seats and EVA doors](../../../vehicles-and-parts/parts/add-crew-seats-and-eva-doors/overview.md), [Take control of another vehicle](../../../vehicles-and-parts/vehicles/take-control-of-another-vehicle/overview.md)
- [Render ImGui into a texture](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Borrow the main camera and hand it back](../../../camera/main-view/borrow-the-main-camera-and-hand-it-back/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Keep mod-only vehicles out of save files](../../../persistence/saves/keep-mod-vehicles-out-of-saves/overview.md) - related topic

