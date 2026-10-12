# Game Feature

* [Show a modal ImGui dialog during loading, before the game loop runs](overview.md) - How KSA's own ConfirmMods and SelectSystem dialogs pump private frames inside the Program constructor, and two ways mods copy that pattern - a restart question and a fail-fast error dialog

# How-To

* [Loading dialog: a modal question modelled on ConfirmMods, with its own frame pump](show-a-dialog-during-loading-1.md) - Ask the player a question while mods are being prepared by subclassing Popup for the content and running a private poll, ImGui-frame and ImGui-only render pass loop until the answer arrives
* [Loading dialog: a PopupTask loop with ErrorAndExit](show-a-dialog-during-loading-2.md) - Wrap any Popup in a small task that pumps frames during ModLibrary.PrepareAll so a fatal error can be shown with copyable text and the failing element before the process exits
