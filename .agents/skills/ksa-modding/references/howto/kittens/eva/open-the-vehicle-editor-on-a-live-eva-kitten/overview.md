---
type: Game Feature
title: Open the stock vehicle editor on a live EVA kitten, show it upright and keep its attachment nodes
description: How a mod opens the stock vehicle editor on a live EVA kitten by setting Program.EditorFlag at the physics handoff, shows the kitten upright, and gives it attachment nodes that persist through saves
category: kittens
group: eva
topic: open-the-vehicle-editor-on-a-live-eva-kitten
demonstrated_by:
  - mods/unscience/features/iron-man-kitten-vehicle-editing
  - mods/unscience/features/iron-man-connector-serialization
lifecycle:
  - frame:draw-ui
  - frame:prepare
  - frame:input
  - frame:viewports
  - editor:on-frame
  - editor:render
  - event:save
  - event:load
  - event:part-copy
  - event:unload
game_types:
  - KSA.Program
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.VehicleSaveData
  - KSA.KittenEva
  - KSA.Part
  - KSA.PartTree
  - KSA.KittenRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.OrbitController
  - KSA.Part.Connector
  - KSA.PartInstance
  - KSA.PartConnectorInstance
  - KSA.PartTemplate
  - KSA.SerializedId
  - KSA.ConnectorCapabilityFlags
tags: [kitten, eva, vehicle-editor, iron-man, harmony, attachment-nodes]
---
KSA's vehicle editor edits a vessel's `PartTree` in place and writes it back when it closes, but the game never offers it for a kitten: the debug-menu **Editor Toggle** is disabled while the controlled vehicle is a `KittenEva`. A mod can still open it, because the whole path is driven by the public static bool `Program.EditorFlag`. Doing it well takes three separate pieces, each its own article: opening the editor and protecting the body root, making the kitten visible and upright while it is edited, and giving the kitten attachment nodes that survive saves. All three come from Unscience's Iron Man sub-mod.

## Engine background

| Piece | Where | What it does | Source |
|---|---|---|---|
| Editor flag | `Program.EditorFlag`, `Program.OnFrameEditor` | `EditorFlag && Editor == null` builds `new VehicleEditor(ControlledVehicle, newVehicle: false)`, clears `ControlledVehicle`, sets simulation speed 0 | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) |
| Editor menu gate | debug menu "Editor Toggle" | Disabled when the controlled vehicle is a `KittenEva` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3966-L3979) |
| In-place editing | `VehicleEditor.ExistingVehicle`, `VehicleEditingSpace(Vehicle)` | The editing space shares the vehicle's `PartTree` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L91-L94) |
| Exit | `VehicleEditor.RequestExit`, `FinalizeExit`, `Dispose` | Flag cleared; next `PrepareFrame` disposes the editor, writes `ExistingVehicle.Parts`, restores control | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2181-L2192) |
| Draw | `Program.RenderEditor`, `SuperMeshRenderSystem.ClearBuckets` | Editor draws part geometry only; buckets are cleared before the prepass | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L554) |
| Camera | `OrbitController.GetFrame2Ecl`, `EditorOnScroll` | Rocket-shaped camera basis and pan axis | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L290-L293) |
| Blueprint | `VehicleSaveData.Create(string, PartTree)` | `Character` copied only for a live vehicle id | [current/decomp/KSA/VehicleSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaveData.cs#L55-L64) |
| Save index | `Part.RegenerateConnectionsFromPartInstance`, `PartConnectorInstance.Index` | Connections are saved as indices into `Part.Connectors` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2445-L2449) |

Editing a kitten is a kitten-and-vessel hybrid: the same object takes the vessel branches of physics and input when it is flown as a rocket (see [fly an EVA kitten as an ordinary rocket vessel](../treat-an-eva-kitten-as-a-rocket/overview.md)), and the character simulation underneath is described in the [locomotion and servo reference](../kitten-locomotion-and-servo-reference/overview.md). Run the open action at the physics handoff ([mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)); see the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame order.

## Approaches

The three articles are steps of one feature rather than alternatives; do them in order.

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Set `EditorFlag` at the handoff and guard every editor entry point](open-the-vehicle-editor-on-a-live-eva-kitten-1.md) | unscience | `harmony-prefix` | `frame:draw-ui`, `frame:prepare`, `frame:input`, `editor:on-frame` | Reuses the stock existing-vehicle path; every guard is a no-op for other sessions; new-vehicle and blueprint-launch flows are blocked rather than extended |
| [2. Draw the avatar from a `ClearBuckets` postfix and re-base the editor camera](open-the-vehicle-editor-on-a-live-eva-kitten-2.md) | unscience | `harmony-postfix`, `harmony-transpiler` | `editor:render`, `frame:viewports`, `frame:input` | Kitten stands upright and is pannable; a transpiler on `EditorOnScroll` fails at install if the IL shape changes |
| [3. Private per-instance attachment nodes that survive saves](open-the-vehicle-editor-on-a-live-eva-kitten-3.md) | unscience | `save-payload-embedding` | `frame:prepare`, `event:save`, `event:load`, `event:part-copy`, `event:unload` | Saves with connected nodes need the mod; unload must convert live links to stock endpoints |

## See also

- [Fly an EVA kitten as an ordinary rocket vessel](../treat-an-eva-kitten-as-a-rocket/overview.md), [Kitten locomotion and servo reference](../kitten-locomotion-and-servo-reference/overview.md)
- [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md), [Hide a kitten's body from one viewport](../../appearance/hide-a-kitten-body-from-one-viewport/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Anchor to parts across vehicle changes](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md), [Show live vehicle data in a window](../../../ui/windows/show-live-vehicle-data-in-a-window/overview.md)
- [Evolve a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md), [Define a part with game data](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md)
- Mod features: [Iron Man kitten vehicle editing](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md), [Iron Man connector serialization](../../../../mods/unscience/features/iron-man-connector-serialization.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Store per-save mod data next to a KSA save and notice when the player saves](../../../persistence/saves/save-mod-state-with-the-game-save/overview.md) - related topic

