---
type: How-To
title: "Popups: a Popup subclass that registers itself and is drawn by Popup.DrawAll"
description: Show a console-styled modal with Yes/No or three-way buttons by subclassing the game's abstract Popup and constructing it, since the constructor registers the instance for Popup.DrawAll
category: ui
group: windows
topic: show-alerts-and-popups-in-game
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/game-styled-popups-and-toasts, mods/ohmystars/features/star-binary-switcher]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Popup
  - KSA.PopupButton
  - KSA.PopupButtonKind
  - KSA.ConsoleStyle
  - KSA.ConfirmActionPopup
  - KSA.TimedAlert
tags: [popup, modal, confirm, console-style, buttons, toast, ohmystars]
---
Oh My Stars asks before every destructive action ("Restore Defaults", deleting or overwriting a preset) and offers a three-way "Restore Settings" choice, in modals that look exactly like the game's own confirmation dialogs. It gets them by subclassing the game's abstract `KSA.Popup` and drawing with the same console-modal helpers the stock `ConfirmActionPopup` uses. No window bookkeeping is needed: constructing a popup is enough to show it. Short status messages go to the game's toast list (see [approach 1](show-alerts-and-popups-in-game-1.md)). Demonstrated by Oh My Stars. Prerequisite: StarMap.

## Background: how the game does it

- **Self-registration.** The protected `Popup` constructor assigns a unique `PopupId`/`WindowId` (`TypeName + "~Popup" + n`) and adds the instance to a static list.[current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L66-L72)[^ksa-popup]
- **Drawing.** `Popup.DrawAll` removes inactive popups and calls each remaining popup's abstract `OnDrawUi()`, stopping after the first one that left a modal open.[current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L46-L64)[^ksa-popup] `Program.OnDrawUiFrame` calls it right after `Alert.DrawAll`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3134)[^ksa-program]
- **Console modal helpers.** `BeginConsoleModal(id, title, sizeUv)` sizes the modal from screen-relative UV times the interface scale and goes through `ConsoleStyle.BeginModal`; `EndConsoleBody`, `CenteredWrappedText`, `DrawConsoleButtonRow<TPopup>(parent, PopupButton<TPopup>[])` and `EndConsoleModal` finish it. `Popup.CreateButton<TPopup>(text, action, kind)` builds the buttons; `PopupButtonKind` is `Neutral`, `Primary` or `Danger`.[current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L102-L117)[^ksa-popup]
- **The stock model.** `ConfirmActionPopup.Create(action, title, message)` is the public factory for plain confirm/cancel; its `OnDrawUi` is the sequence to copy.[current/decomp/KSA/ConfirmActionPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmActionPopup.cs#L24-L40)[^ksa-confirmactionpopup]
- **Input.** Open popups get first refusal of key and mouse events through `Popup.OnKeyAll` and `OnMouseAll` when they implement the listener interfaces; `Popup.AnyOpen` suppresses the Esc menu toggle.[current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L76-L100)[^ksa-popup]

## Lifecycle and timing

The Star Editor window runs from `[StarMapBeforeGui]` (prefix on `Program.OnDrawUiFrame`), and `OnDrawUiFrame` starts with `Alert.DrawAll` and `Popup.DrawAll`, so a popup created by a button click appears in the same frame. The YES/NO actions run inside `Popup.DrawAll` on the main thread with the physics step in flight: UI-thread work is fine, but anything that mutates simulated state should be queued for a safe point. `OnDrawUiFrame` runs only while `Program.DrawUI` is true, so an open popup is not drawn while the HUD is hidden, yet `Popup.AnyOpen` stays true (per the mod feature page). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Subclass `Popup`.** Store an upper-cased title, a message and an array of buttons built with `Popup.CreateButton`; each button runs the caller's action and then sets `popup.Active = false`. Draw with the same sequence as `ConfirmActionPopup`:

```csharp
// src/StarsEditWindow.cs L26-L58
// Game-styled choice modal, laid out like the game's own ConfirmActionPopup.
private sealed class ChoicePopup : Popup
{
    private readonly string _title;

    private readonly string _message;

    private readonly PopupButton<Popup>[] _buttons;

    public ChoicePopup(string title, string message, params (string Label, Action? Action, PopupButtonKind Kind)[] choices)
    {
        _title = title.ToUpperInvariant();
        _message = message;
        _buttons = choices.Select(static choice => Popup.CreateButton<Popup>(choice.Label, popup =>
        {
            choice.Action?.Invoke();
            popup.Active = false;
        }, choice.Kind)).ToArray();
    }

    protected override void OnDrawUi()
    {
        if (BeginConsoleModal(WindowId.AsSpan(), _title.AsSpan(), Popup.DEFAULT_SIZE_UV))
        {
            ConsoleStyle.BeginBody();
            ConsoleStyle.PushWidgetStyle();
            Popup.CenteredWrappedText(_message.AsSpan());
            EndConsoleBody();
            Popup.DrawConsoleButtonRow<Popup>(this, _buttons);
            Popup.EndConsoleModal();
        }
    }
}
```

2. **Show by constructing.** A one-line helper creates a Yes/No popup; the instance is not stored anywhere:

```csharp
// src/StarsEditWindow.cs L60-L63
private static void ShowYesNo(string title, string message, Action onYes)
{
    new ChoicePopup(title, message, ("YES", onYes, PopupButtonKind.Primary), ("NO", null, PopupButtonKind.Neutral));
}
```

3. **Use it from a button.** Guard a destructive action behind the popup; the action runs only when the player picks YES:

```csharp
// src/StarsEditWindow.cs L335-L349
if (ConsoleWidgets.Button("Restore Defaults"))
{
    ShowYesNo("Restore Defaults", "Restore all settings and star file(s) to the game's defaults?", RestoreAllDefaults);
}

ImGui.SameLine();
if (ConsoleWidgets.Button("Restore Settings"))
{
    new ChoicePopup(
        "Restore Settings",
        "Restore the last used settings from the settings file, or the settings the mod initially loaded with this session?",
        ("LAST USED", RestoreLastUsedSettings, PopupButtonKind.Primary),
        ("INITIAL LOAD", RestoreInitialSettings, PopupButtonKind.Primary),
        ("CANCEL", null, PopupButtonKind.Neutral));
}
```

4. **Give feedback with toasts.** An empty-name error, a saved preset and a "could not find" notice use `TimedAlert`:

```csharp
// src/StarsEditWindow.cs L1183-L1194
    if (name.Length == 0)
    {
        TimedAlert.CreateWarning("Enter a name before saving the star settings");
        return;
    }

    bool exists = SavedSettings.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    string message = exists
        ? $"Overwrite the existing saved setting '{name}' with the current settings?"
        : $"Save the current settings as '{name}'?";
    ShowYesNo("Save Settings", message, () => SaveNamedSettingConfirmed(name));
}
```

## Complete example

| File | Role |
|---|---|
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L26-L63) | `ChoicePopup` and `ShowYesNo` |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L335-L349) | Restore defaults and the three-way choice |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1183-L1194) | Save confirmation and the empty-name toast |

## Variations and gotchas

- **Use the factory when it fits.** For plain confirm/cancel, `ConfirmActionPopup.Create(action, title, message)` already exists; subclass only for arbitrary labels or a third button.
- **Set `Active = false` to close.** `DrawAll` removes inactive popups next frame.
- **Popups block Esc.** While any popup is open the game's `OpenGameSettings` key does not toggle the Esc menu.
- **Keep popup actions short.** They run inside the game's UI pass.
- **Version.** Checked against 2026.10.10.5554; `Popup` constructors, `BeginConsoleModal` and friends are `protected`, so they are part of the public inheritance surface but a game update could change them.

## See also

- [Overview of show-alerts-and-popups-in-game](overview.md)
- [Approach 1: TimedAlert from main-thread hooks](show-alerts-and-popups-in-game-1.md)
- [Show a dialog during loading](../show-a-dialog-during-loading/overview.md)
- [Match the stock console UI style](../match-the-stock-console-ui-style/overview.md)
- Mod features: [Game-styled popups and toasts](../../../../mods/ohmystars/features/game-styled-popups-and-toasts.md), [Star binary switcher](../../../../mods/ohmystars/features/star-binary-switcher.md)

[^ksa-popup]: KSA.Popup (decompiled, build 2026.10.10.5554)
[^ksa-confirmactionpopup]: KSA.ConfirmActionPopup (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
