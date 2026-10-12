---
type: Game Feature
title: Show a modal ImGui dialog during loading, before the game loop runs
description: How KSA's own ConfirmMods and SelectSystem dialogs pump private frames inside the Program constructor, and two ways mods copy that pattern - a restart question and a fail-fast error dialog
category: ui
group: windows
topic: show-a-dialog-during-loading
demonstrated_by: [mods/starmap/features/startup-modal-popup, mods/kittenextensions/features/pre-game-popup-loop]
lifecycle: [startup:asset-load]
game_types:
  - KSA.Popup
  - KSA.PopupButton
  - KSA.ConfirmMods
  - KSA.SelectSystem
  - KSA.SetupTaskBase
  - KSA.ImGuiHelper
  - KSA.ImGuiBackend
  - KSA.Program
  - KSA.ModLibrary
  - RenderCore.Renderer
tags: [loading, startup, modal, popup, frame-pump, error-dialog]
---
While mods are being prepared there is no game loop: `Program.OnFrame` is not running, so none of the StarMap GUI hooks can fire and a `Popup` created now would never be drawn. The game solves this for its own startup dialogs by running a private mini frame loop inside the `Program` constructor, and a mod that needs to ask a question, or fail fast with a readable error, copies that loop.

## Engine background

| Piece | File | Role |
|---|---|---|
| `Program` constructor | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L794-L909) | Creates the renderer (L794), main pass (L825), ImGui context and backend (L842-L853), shows the loading screen, then runs `ConfirmMods`, `ModLibrary.PrepareAll` and `SelectSystem` |
| `ConfirmMods` | [current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L13-L123) | The pump to copy: poll, ImGui frame, one ImGui-only render pass, submit, rebuild on failure |
| `SelectSystem` | [current/decomp/KSA/SelectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SelectSystem.cs#L21-L21) | A second game dialog using the same loop, with `Show => ConfigOnStartPopup.Active` |
| `ImGuiHelper.BlankBackground`, `GetMainOverlayDrawList`, `StartFrame` | [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L1727-L1732) | Frame setup and background fill |
| `Popup` | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L46-L117) | Content: console modal helpers and `Popup.DrawAll` |

**Frame order.** This runs at `startup:asset-load`, before the game loop exists. Once the loop is running (`frame:draw-ui`), constructing a `Popup` is enough; see [show alerts and popups in game](../show-alerts-and-popups-in-game/overview.md). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Modal question modelled on ConfirmMods](show-a-dialog-during-loading-1.md) | StarMap | Harmony prefix on `PrepareAll`, `Popup` subclass, private frame pump, restart process | `startup:asset-load` | Closest to the game's own dialog; needs renderer and ImGui created |
| 2 | [PopupTask loop with ErrorAndExit](show-a-dialog-during-loading-2.md) | KittenExtensions | Reusable `PopupTask` wrapper, fatal error with copy-to-clipboard | `startup:asset-load` | Fail-fast diagnostics; the copy omits two lines of the game's pump |

## Choosing

- A question whose answer changes what happens next (restart, continue): approach 1.
- A failure the player must read and report before exiting: approach 2.

## See also

- [Show alerts and popups in game](../show-alerts-and-popups-in-game/overview.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)

