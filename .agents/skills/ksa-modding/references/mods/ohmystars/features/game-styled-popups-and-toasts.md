---
type: Mod Feature
title: Game-styled popups and toasts
description: Show KSA-styled modal confirmations by subclassing the game's Popup class, which self-registers for Popup.DrawAll, and give feedback with TimedAlert toasts
mod: ohmystars
category: ui
techniques: [imgui-window, game-popup-subclass]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Popup
  - KSA.PopupButton
  - KSA.PopupButtonKind
  - KSA.ConfirmActionPopup
  - KSA.TimedAlert
  - KSA.Alert
  - KSA.ConsoleStyle
  - KSA.Program
tags: [imgui, popup, modal, confirmation, alerts, toast, console-style]
---

The Star Editor asks before every destructive action ("Restore Defaults", "Game Default", deleting
or overwriting a preset) and offers a three-way "Restore Settings" choice, all in modals that look
exactly like the game's own confirmation dialogs. It gets them by subclassing the game's abstract
`KSA.Popup` and drawing with the same console-modal helpers the stock `ConfirmActionPopup` uses;
short status messages go to the game's `TimedAlert` toast list. No window bookkeeping is needed:
constructing a popup is enough to show it.[^oms-starseditwindow-cs] [^ksa-popup-cs]

## How it works

1. **Subclass `Popup`.** `ChoicePopup` stores an upper-cased title, a message and an array of
   `PopupButton<Popup>` built with `Popup.CreateButton(label, action, kind)`; each button runs the
   caller's action and then sets `popup.Active = false`
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L26-L58)). `PopupButtonKind` is `Neutral`,
   `Primary` or `Danger` and selects the console button style
   ([current/decomp/KSA/PopupButton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PopupButton.cs), [current/decomp/KSA/PopupButtonKind.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PopupButtonKind.cs)).
2. **Draw like `ConfirmActionPopup`.** `OnDrawUi` calls the protected `BeginConsoleModal(WindowId,
   title, Popup.DEFAULT_SIZE_UV)`, then `ConsoleStyle.BeginBody`, `PushWidgetStyle`,
   `Popup.CenteredWrappedText`, `EndConsoleBody`, `Popup.DrawConsoleButtonRow` and
   `Popup.EndConsoleModal`, the same sequence as the stock class
   ([current/decomp/KSA/ConfirmActionPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmActionPopup.cs#L29-L40)).[^ksa-confirmactionpopup-cs]
   `BeginConsoleModal` sizes the modal from screen-relative UV times the interface scale and goes
   through `ConsoleStyle.BeginModal`, which centres it on the main viewport and opens an ImGui popup
   modal with `NoSavedSettings` ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L107-L117),
   [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L769-L800)).[^ksa-consolestyle-cs]
3. **Show by constructing.** The protected `Popup` constructor assigns a unique `PopupId`/`WindowId`
   and adds the instance to a static list; `Popup.DrawAll` first drops inactive popups, then draws
   from that list ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L44-L72)). `ShowYesNo` is therefore a
   one-liner, `new ChoicePopup(title, message, ("YES", onYes, Primary), ("NO", null, Neutral))`
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L60-L63)).
4. **Why a custom class.** For a plain confirm/cancel the public factory
   `ConfirmActionPopup.Create(action, title, message)` already exists
   ([current/decomp/KSA/ConfirmActionPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmActionPopup.cs#L24-L27)); the mod needs
   arbitrary labels and a third button, so it reproduces the layout with its own button array.
5. **Toasts.** `TimedAlert.CreateWarning(text, seconds)` and `TimedAlert.Create(text, color,
   seconds)` add a message to the alert list that `Alert.DrawAll` stacks on screen; durations are in
   player time ([current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L69-L78),
   [current/decomp/KSA/Alert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Alert.cs#L17-L25)).[^ksa-timedalert-cs] The mod uses them for an
   empty preset name, a saved preset and star binaries that could not be found
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1180-L1194), [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1272-L1282)).

## Lifecycle and timing

- **Created in `[StarMapBeforeGui]`, drawn right after it.** The Star Editor window runs from the
  StarMap prefix on `Program.OnDrawUiFrame`
  ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L24)),[^starmap-patches-programpatcher-cs]
  and the body of `OnDrawUiFrame` starts with `Alert.DrawAll(dt)` and `Popup.DrawAll()`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3134)).[^ksa-program-cs] A popup or toast
  created by a button click therefore appears in the same frame (`frame:draw-ui`).
- **Actions run inside `Popup.DrawAll`.** The YES/NO lambdas execute during `OnDrawUiFrame`, on the
  main thread, with the physics step in flight. The Star Editor's actions reload the star field
  there, which is UI-thread work; anything that mutates simulated state should still be queued for
  `frame:input` instead.
- **Hidden HUD pauses them.** `OnDrawUiFrame` runs only while `Program.DrawUI` is true, so an open
  popup is not drawn while the HUD is hidden, yet `Popup.AnyOpen` stays true, which keeps the
  `OpenGameSettings` key from toggling the Esc menu and keeps the Esc menu from drawing ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1977), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3204)).
- **Input.** Open popups get first refusal of key and mouse events through `Popup.OnKeyAll` and
  `Popup.OnMouseAll` when they implement the listener interfaces
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1750), [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L76-L100)).
  `ChoicePopup` implements neither and relies on the ImGui modal.

## Key code

| Path | Role |
|---|---|
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L26-L63) | `ChoicePopup` and `ShowYesNo` |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L334-L349) | Restore Defaults and three-way Restore Settings |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1180-L1194) | Preset save confirmation and empty-name toast |

## Snippets

```csharp
// src/StarsEditWindow.cs L26-L63
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

	private static void ShowYesNo(string title, string message, Action onYes)
	{
		new ChoicePopup(title, message, ("YES", onYes, PopupButtonKind.Primary), ("NO", null, PopupButtonKind.Neutral));
	}
```

```csharp
// src/StarsEditWindow.cs L334-L349
		ImGui.Dummy(new float2(0f, 4f));
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

```csharp
// current/decomp/KSA/Popup.cs L44-L72
	public static bool AnyOpen => Popups.Count > 0;

	public static void DrawAll()
	{
		Popups.RemoveAll((Popup popup) => !popup.Active);
		foreach (Popup popup in Popups)
		{
			popup.OnDrawUi();
			try
			{
				ImGuiWindowPtr topMostPopupModal = ImGui.Internal.GetTopMostPopupModal();
				if (topMostPopupModal.IsNull() || topMostPopupModal.Ctx.IsNotNull())
				{
					break;
				}
			}
			catch
			{
			}
		}
	}

	protected Popup()
	{
		PopupId = $"~Popup{_popupCount}";
		_popupCount++;
		WindowId = GetType().Name + PopupId;
		Popups.Add(this);
	}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Popup` (constructor, `Active`, `WindowId`, `OnDrawUi`, `DrawAll`, `AnyOpen`) | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L12-L74) | Base class and registry |
| `Popup.CreateButton`, `BeginConsoleModal`, `EndConsoleBody`, `EndConsoleModal`, `CenteredWrappedText`, `DrawConsoleButtonRow`, `DEFAULT_SIZE_UV` | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L102-L241) | Console-styled modal layout |
| `PopupButton<T>`, `PopupButtonKind` | [current/decomp/KSA/PopupButton.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PopupButton.cs) | Footer buttons |
| `ConfirmActionPopup.Create` | [current/decomp/KSA/ConfirmActionPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmActionPopup.cs#L24-L40) | Stock two-button alternative and layout reference |
| `ConsoleStyle.BeginModal`, `BeginBody`, `PushWidgetStyle` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L769-L800) | Centred popup modal |
| `TimedAlert.Create`, `CreateWarning` | [current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L69-L78) | Toasts |
| `Program.OnDrawUiFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3160) | Where alerts and popups are drawn |

## Pitfalls and version notes

- **One popup at a time.** `DrawAll` draws the first active popup and then leaves the loop on its
  top-most-modal check, so a second popup created while one is open waits for the first to close.
- **Not drawn without the HUD.** See Lifecycle; a confirmation opened just before the player hides
  the HUD stays pending and keeps `Popup.AnyOpen` true.
- **Protected helpers.** `BeginConsoleModal`, `DrawConsoleButtonRow` and the text helpers are
  `protected`, so they are only reachable from a subclass; their signatures can change with any game
  update.
- **Action errors.** An exception thrown by a button action escapes `Popup.DrawAll` inside
  `OnDrawUiFrame`; wrap the work as the mod does elsewhere when it can fail.

## See also

- [Star binary switcher](star-binary-switcher.md)
- [ModMenu entry and game-styled windows](modmenu-entry-and-console-style-windows.md)
- [Star instance restyle patch](star-instance-restyle-patch.md)
- [Popups: a Popup subclass that registers itself and is drawn by Popup.DrawAll](../../../howto/ui/windows/show-alerts-and-popups-in-game/show-alerts-and-popups-in-game-2.md) - how-to article demonstrating this feature

[^oms-starseditwindow-cs]: OhMyStars src/StarsEditWindow.cs
[^ksa-popup-cs]: KSA.Popup (decompiled, build 2026.10.10.5554)
[^ksa-confirmactionpopup-cs]: KSA.ConfirmActionPopup (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle-cs]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
[^ksa-timedalert-cs]: KSA.TimedAlert (decompiled, build 2026.10.10.5554)
[^starmap-patches-programpatcher-cs]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
